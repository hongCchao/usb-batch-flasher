[CmdletBinding()]
param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\dist\offline\UsbBatchTool.Offline.dll')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Only synthetic files and cache entries created by this run are used. No device
# scanning, physical disk writes, formatting, or CacheService.Clear calls occur.
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedAssembly = [IO.Path]::GetFullPath($AssemblyPath)
if (-not [IO.File]::Exists($resolvedAssembly)) {
    throw "找不到离线程序集，请先运行 build-offline.ps1：$resolvedAssembly"
}
$expectedBase = [IO.Path]::GetDirectoryName($resolvedAssembly)
$scratchParent = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\test-data'))
[IO.Directory]::CreateDirectory($scratchParent) | Out-Null
$scratchRoot = Join-Path $scratchParent ('service-tests-' + [Guid]::NewGuid().ToString('N'))

[Reflection.Assembly]::LoadFrom($resolvedAssembly) | Out-Null
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($runtimeAssembly in (Get-ChildItem -LiteralPath $PSHOME -Filter '*.dll' -File)) {
    try {
        [Reflection.AssemblyName]::GetAssemblyName($runtimeAssembly.FullName) | Out-Null
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($runtimeAssembly.FullName))
    }
    catch [BadImageFormatException] { }
}
$references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($resolvedAssembly))

$runnerSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UsbBatchTool;
using UsbBatchTool.Services;

public sealed class UsbServiceTestResult
{
    public string[] Passed { get; set; } = Array.Empty<string>();
    public int Assertions { get; set; }
    public string CacheBase { get; set; } = "";
    public int CleanedCacheDirectories { get; set; }
}

public static class UsbServiceTestRunner
{
    private sealed class NoProgress : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) { }
    }

    private sealed class TestScope : IDisposable
    {
        private readonly string _scratchRoot;
        private readonly string _cacheRoot;
        private readonly HashSet<string> _ownedCacheRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Passed = new List<string>();
        public int Assertions { get; private set; }
        public int CleanedCacheDirectories { get; private set; }
        public string RunId { get; } = Guid.NewGuid().ToString("N");

        public TestScope(string scratchRoot, string expectedBase)
        {
            if (!string.Equals(Path.GetFullPath(AppPaths.Base).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(expectedBase).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AppPaths.Base 未指向待测试离线程序集目录，已停止测试。");
            _scratchRoot = Path.GetFullPath(scratchRoot);
            if (Directory.Exists(_scratchRoot) || File.Exists(_scratchRoot))
                throw new InvalidOperationException("测试临时目录已存在，未进行覆盖。");
            _cacheRoot = Path.GetFullPath(AppPaths.Cache);
            Directory.CreateDirectory(_scratchRoot);
        }

        public string FilePath(string name) => Path.Combine(_scratchRoot, name);

        public byte[] Payload(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            var identity = Encoding.UTF8.GetBytes("USB service test " + RunId);
            Array.Copy(identity, bytes, Math.Min(identity.Length, bytes.Length));
            return bytes;
        }

        public void Assert(bool condition, string message)
        {
            Assertions++;
            if (!condition) throw new InvalidOperationException("断言失败：" + message);
        }

        public void AssertBytes(string path, byte[] expected, string message)
        {
            Assert(File.Exists(path), message + "（文件不存在）");
            Assert(File.ReadAllBytes(path).SequenceEqual(expected), message + "（内容不一致）");
        }

        public string RegisterCache(string source)
        {
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            string path = Path.GetFullPath(Path.Combine(_cacheRoot, hash.ToLowerInvariant()));
            if (!_ownedCacheRoots.Contains(path))
            {
                if (Directory.Exists(path) || File.Exists(path))
                    throw new InvalidOperationException("待测试内容已存在于缓存，未触碰已有缓存：" + path);
                _ownedCacheRoots.Add(path);
            }
            return path;
        }

        public async Task<PreparedSource> Prepare(CacheService service, string source, bool zip)
        {
            if (zip || source.EndsWith(".img.gz", StringComparison.OrdinalIgnoreCase)) RegisterCache(source);
            return await service.PrepareAsync(source, zip, new NoProgress(), CancellationToken.None).ConfigureAwait(false);
        }

        public async Task RejectZip(CacheService service, string source, string label)
        {
            string cache = RegisterCache(source);
            bool rejected = false;
            try { await Prepare(service, source, true).ConfigureAwait(false); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, label + "必须抛出 InvalidDataException");
            Assert(!Directory.Exists(cache), label + "失败后应清理本次解压的缓存目录");
            Passed.Add(label);
        }

        public void Dispose()
        {
            // All targets are explicit full paths first registered as nonexistent.
            // Never delete the cache root, an existing user's entry, or a reparse point.
            foreach (var path in _ownedCacheRoots)
            {
                string full = Path.GetFullPath(path);
                string? parent = Path.GetDirectoryName(full);
                string name = Path.GetFileName(full);
                if (!string.Equals(parent, _cacheRoot, StringComparison.OrdinalIgnoreCase)
                    || name.Length != 64 || !name.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    throw new InvalidOperationException("测试缓存清理路径验证失败：" + full);
                if (!Directory.Exists(full)) continue;
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("测试缓存变成重解析点，拒绝递归清理：" + full);
                Directory.Delete(full, true);
                CleanedCacheDirectories++;
            }
            if (Directory.Exists(_scratchRoot))
            {
                if (!Path.GetFileName(_scratchRoot).StartsWith("service-tests-", StringComparison.Ordinal)
                    || (File.GetAttributes(_scratchRoot) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("测试临时目录验证失败，未执行清理。");
                Directory.Delete(_scratchRoot, true);
            }
        }
    }

    public static async Task<UsbServiceTestResult> RunAsync(string scratchRoot, string expectedBase)
    {
        var scope = new TestScope(scratchRoot, expectedBase);
        var service = new CacheService();
        var result = new UsbServiceTestResult { CacheBase = AppPaths.Cache };
        try
        {
            // Raw image preparation must preserve the original payload rather than extract it.
            byte[] imageBytes = scope.Payload(8192, 104);
            string image = scope.FilePath("firmware.img");
            File.WriteAllBytes(image, imageBytes);
            var raw = await scope.Prepare(service, image, false).ConfigureAwait(false);
            scope.Assert(raw.PayloadPath == image, "IMG 原样作为烧录源");
            scope.Assert(raw.TotalBytes == imageBytes.Length && !raw.IsZip && !raw.CacheHit, "IMG 元数据准确");
            scope.Assert(raw.Sha256 == Convert.ToHexString(SHA256.HashData(imageBytes)), "IMG SHA-256 正确");
            scope.AssertBytes(raw.PayloadPath, imageBytes, "IMG 内容未修改");
            scope.Passed.Add("IMG：原样准备、大小和 SHA-256 正确");

            string gzip = scope.FilePath("firmware.img.gz");
            using (var output = File.Create(gzip))
            using (var compressed = new GZipStream(output, CompressionLevel.Optimal))
                compressed.Write(imageBytes, 0, imageBytes.Length);
            var unpacked = await scope.Prepare(service, gzip, false).ConfigureAwait(false);
            scope.Assert(!unpacked.CacheHit && !unpacked.IsZip, "IMG.GZ 第一次解压不命中缓存");
            scope.Assert(unpacked.TotalBytes == imageBytes.Length, "IMG.GZ 返回解压后镜像大小");
            scope.AssertBytes(unpacked.PayloadPath, imageBytes, "IMG.GZ 正确解压为原始镜像");
            var markerTime = new DateTime(2025, 1, 2, 3, 4, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(unpacked.PayloadPath, markerTime);
            var unpackedAgain = await scope.Prepare(service, gzip, false).ConfigureAwait(false);
            scope.Assert(unpackedAgain.CacheHit, "IMG.GZ 相同文件重复准备命中缓存");
            scope.Assert(unpackedAgain.PayloadPath == unpacked.PayloadPath && unpackedAgain.Sha256 == unpacked.Sha256, "IMG.GZ 缓存位置和哈希一致");
            scope.Assert(File.GetLastWriteTimeUtc(unpackedAgain.PayloadPath) == markerTime, "IMG.GZ 命中时未重新解压");
            scope.Passed.Add("IMG.GZ：解压内容正确，相同文件复用缓存且不重新解压");

            byte[] nestedBytes = scope.Payload(3072, 205);
            byte[] readmeBytes = Encoding.UTF8.GetBytes("readme " + scope.RunId);
            string zip = scope.FilePath("bundle.zip");
            WriteZip(zip, new[] { ("nested/data.bin", nestedBytes, 0), ("readme.txt", readmeBytes, 0), ("empty/", Array.Empty<byte>(), 0) });
            var extracted = await scope.Prepare(service, zip, true).ConfigureAwait(false);
            scope.Assert(extracted.IsZip && !extracted.CacheHit, "ZIP 第一次准备正确标记模式和缓存状态");
            scope.Assert(extracted.TotalBytes == nestedBytes.Length + readmeBytes.Length, "ZIP 总大小按实际文件内容统计");
            scope.AssertBytes(Path.Combine(extracted.PayloadPath, "nested", "data.bin"), nestedBytes, "ZIP 保留嵌套目录及文件内容");
            scope.AssertBytes(Path.Combine(extracted.PayloadPath, "readme.txt"), readmeBytes, "ZIP 根目录文件完整");
            scope.Assert(Directory.Exists(Path.Combine(extracted.PayloadPath, "empty")), "ZIP 保留空目录");
            string manifestPath = Path.Combine(Path.GetDirectoryName(extracted.PayloadPath)!, "source.json");
            scope.Assert(File.Exists(manifestPath), "ZIP 完成后保存缓存清单");
            var cachedZip = await scope.Prepare(service, zip, true).ConfigureAwait(false);
            scope.Assert(cachedZip.CacheHit && cachedZip.PayloadPath == extracted.PayloadPath, "ZIP 相同文件重复准备命中原缓存");
            scope.Passed.Add("ZIP：目录结构、文件内容、总大小及重复缓存命中正确");

            // A cache hit must verify every extracted file, not just source.json.
            string cachedData = Path.Combine(extracted.PayloadPath, "nested", "data.bin");
            byte[] damagedData = File.ReadAllBytes(cachedData);
            damagedData[damagedData.Length - 1] ^= 0x55;
            File.WriteAllBytes(cachedData, damagedData);
            var repaired = await scope.Prepare(service, zip, true).ConfigureAwait(false);
            scope.Assert(!repaired.CacheHit, "缓存文件损坏时不允许命中");
            scope.AssertBytes(cachedData, nestedBytes, "缓存文件损坏后重新解压并修复内容");
            scope.Passed.Add("ZIP 缓存：文件字节损坏后重解压，不复制损坏缓存");

            bool networkSourceRejected = false;
            try { PhysicalPathGuard.AssertNotOnTarget(new UsbDisk(), @"\\localhost\share\source.zip"); }
            catch (IOException) { networkSourceRejected = true; }
            scope.Assert(networkSourceRejected, "本机 UNC 共享源必须在访问物理磁盘前拒绝");
            scope.Passed.Add("同盘保护：拒绝无法确定物理磁盘的 UNC 来源");

            long originalLength = new FileInfo(zip).Length;
            DateTime originalWrite = File.GetLastWriteTimeUtc(zip);
            byte[] changedBytes = (byte[])nestedBytes.Clone();
            changedBytes[changedBytes.Length - 1] ^= 0x7f;
            WriteZip(zip, new[] { ("nested/data.bin", changedBytes, 0), ("readme.txt", readmeBytes, 0), ("empty/", Array.Empty<byte>(), 0) });
            File.SetLastWriteTimeUtc(zip, originalWrite);
            scope.Assert(new FileInfo(zip).Length == originalLength, "变更测试保持同名 ZIP 的文件大小不变");
            scope.Assert(File.GetLastWriteTimeUtc(zip) == originalWrite, "变更测试保持修改时间不变");
            var changed = await scope.Prepare(service, zip, true).ConfigureAwait(false);
            scope.Assert(changed.Sha256 != extracted.Sha256 && changed.PayloadPath != extracted.PayloadPath, "同名同大小同时间但内容改变时使用不同哈希缓存");
            scope.Assert(!changed.CacheHit, "内容改变后的 ZIP 不错误复用旧缓存");
            scope.AssertBytes(Path.Combine(changed.PayloadPath, "nested", "data.bin"), changedBytes, "新缓存包含变更内容");
            scope.AssertBytes(Path.Combine(extracted.PayloadPath, "nested", "data.bin"), nestedBytes, "旧缓存内容保持不变");
            scope.Passed.Add("内容变更：同名、同大小、同时间 ZIP 仍按新 SHA-256 解压，旧缓存保留");

            byte[] unsafePayload = Encoding.UTF8.GetBytes("synthetic " + scope.RunId);
            string slashTraversal = scope.FilePath("traversal-slash.zip");
            WriteZip(slashTraversal, new[] { ("../escaped-" + scope.RunId + ".txt", unsafePayload, 0) });
            await scope.RejectZip(service, slashTraversal, "ZIP 拒绝 ../ 路径越界").ConfigureAwait(false);
            string backslashTraversal = scope.FilePath("traversal-backslash.zip");
            WriteZip(backslashTraversal, new[] { ("..\\escaped-" + scope.RunId + ".txt", unsafePayload, 0) });
            await scope.RejectZip(service, backslashTraversal, "ZIP 拒绝反斜杠路径越界").ConfigureAwait(false);

            string absolute = scope.FilePath("absolute.zip");
            string absoluteTarget = scope.FilePath("unexpected-absolute-" + scope.RunId + ".txt");
            WriteZip(absolute, new[] { (absoluteTarget, unsafePayload, 0) });
            await scope.RejectZip(service, absolute, "ZIP 拒绝绝对路径").ConfigureAwait(false);
            scope.Assert(!File.Exists(absoluteTarget), "ZIP 绝对路径条目没有写入临时目标");

            string symlink = scope.FilePath("symlink.zip");
            WriteZip(symlink, new[] { ("link.txt", unsafePayload, unchecked((int)0xA1FF0000)) });
            await scope.RejectZip(service, symlink, "ZIP 拒绝 Unix 符号链接").ConfigureAwait(false);
            string reparse = scope.FilePath("reparse.zip");
            WriteZip(reparse, new[] { ("reparse.txt", unsafePayload, (int)FileAttributes.ReparsePoint) });
            await scope.RejectZip(service, reparse, "ZIP 拒绝 Windows 重解析点").ConfigureAwait(false);
            string duplicate = scope.FilePath("duplicate.zip");
            WriteZip(duplicate, new[] { ("Readme.txt", unsafePayload, 0), ("README.txt", unsafePayload, 0) });
            await scope.RejectZip(service, duplicate, "ZIP 拒绝大小写冲突条目").ConfigureAwait(false);

            var binding = new PortBinding
            {
                Name = "测试端口",
                LastModel = "old-model",
                LastSerial = "serial-before",
                LocationPaths = new List<string> { "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)" }
            };
            var replacement = new UsbDisk
            {
                Number = 999,
                Model = "replacement-model",
                SerialNumber = "serial-after",
                LocationPaths = new List<string> { "pciroot(0)#pci(1400)#usbroot(0)#usb(3)" }
            };
            scope.Assert(binding.Matches(replacement), "相同物理路径更换型号和序列号后仍匹配端口（忽略大小写）");
            var moved = new UsbDisk { SerialNumber = "serial-before", LocationPaths = new List<string> { "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)" } };
            scope.Assert(!binding.Matches(moved), "不同物理路径即使序列号相同也不匹配");
            scope.Assert(!binding.Matches(new UsbDisk()), "没有位置路径的设备不匹配");
            string alias = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(7)";
            binding.LocationPaths.Add(alias);
            scope.Assert(binding.Matches(new UsbDisk { SerialNumber = "another", LocationPaths = new List<string> { alias } }), "端口追加的 USB2/3 路径别名可以匹配替换设备");
            scope.Passed.Add("端口匹配：只使用位置路径，支持替换 U 盘和 USB2/3 别名");

            RunRawFileTests(scope);

            result.Passed = scope.Passed.ToArray();
            result.Assertions = scope.Assertions;
        }
        finally
        {
            scope.Dispose();
            result.CleanedCacheDirectories = scope.CleanedCacheDirectories;
        }
        return result;
    }

    private static void RunRawFileTests(TestScope scope)
    {
        const int sector = 512;
        byte[] rawBytes = scope.Payload(4 * 1024 * 1024 + 123, 312);
        string sourcePath = scope.FilePath("raw-copy-source.img");
        string targetPath = scope.FilePath("raw-copy-target.bin");
        File.WriteAllBytes(sourcePath, rawBytes);
        using (var source = OpenTestFile(scope, sourcePath, FileAccess.Read))
        using (var target = OpenTestFile(scope, targetPath, FileAccess.ReadWrite, FileMode.CreateNew))
        {
            CallRaw("CopyImage", source, target, sector, new NoProgress(), CancellationToken.None);
            target.Flush();
            long alignedLength = ((rawBytes.LongLength + sector - 1) / sector) * sector;
            scope.Assert(target.Length == alignedLength, "原始写入将非整扇区镜像补齐至 512 字节边界");
            target.Position = 0;
            byte[] written = new byte[checked((int)target.Length)];
            target.ReadExactly(written);
            scope.Assert(written.AsSpan(0, rawBytes.Length).SequenceEqual(rawBytes), "跨 4 MiB 缓冲块写入后镜像字节完全一致");
            scope.Assert(written.AsSpan(rawBytes.Length).IndexOfAnyExcept((byte)0) == -1, "最后一个扇区的填充字节全部为零");
            CallRaw("VerifyImage", source, target, sector, new NoProgress(), CancellationToken.None);
            scope.Assert(source.Position == source.Length, "读回校验覆盖完整源镜像");
            scope.Assert(target.Position == target.Length, "读回校验读取完整末尾扇区");
            scope.Passed.Add("纯文件原始写入：跨缓冲块、非整扇区补零和逐字节读回校验通过");

            // Corrupt the second block, making sure verification checks past 4 MiB.
            long corruptedAt = rawBytes.Length - 7;
            target.Position = corruptedAt;
            target.WriteByte((byte)(rawBytes[(int)corruptedAt] ^ 0x80));
            target.Flush();
            bool mismatchRejected = false;
            try { CallRaw("VerifyImage", source, target, sector, new NoProgress(), CancellationToken.None); }
            catch (IOException) { mismatchRejected = true; }
            scope.Assert(mismatchRejected, "读回校验必须检测第二个缓冲块中篡改的一字节");
            scope.Passed.Add("纯文件读回校验：篡改镜像尾部一字节后准确失败");
        }

        const int clearBytes = 1024 * 1024;
        byte[] layoutBytes = Enumerable.Repeat((byte)0xA7, 5 * clearBytes).ToArray();
        string layoutPath = scope.FilePath("raw-layout-target.bin");
        File.WriteAllBytes(layoutPath, layoutBytes);
        using (var layout = OpenTestFile(scope, layoutPath, FileAccess.ReadWrite))
        {
            CallRaw("ClearOldLayout", layout, layout.Length, sector, CancellationToken.None);
            layout.Flush();
            scope.Assert(layout.Position == 0, "清理旧分区后目标位置归零");
            byte[] actual = new byte[layoutBytes.Length];
            layout.ReadExactly(actual);
            scope.Assert(actual.AsSpan(0, clearBytes).IndexOfAnyExcept((byte)0) == -1, "旧分区清理将首 1 MiB 清零");
            scope.Assert(actual.AsSpan(actual.Length - clearBytes).IndexOfAnyExcept((byte)0) == -1, "旧 GPT 备份清理将尾 1 MiB 清零");
            scope.Assert(actual.AsSpan(clearBytes, actual.Length - 2 * clearBytes).IndexOfAnyExcept((byte)0xA7) == -1, "清理首尾时保留中间 3 MiB 哨兵内容");
            scope.Assert(layout.Length == layoutBytes.Length, "清理分区不改变目标文件总长度");
            scope.Passed.Add("纯文件旧分区清理：首尾各 1 MiB 清零，中间内容和总长度保留");
        }

        byte[] sentinel = scope.Payload(2 * clearBytes + sector, 413);
        string cancelledPath = scope.FilePath("raw-cancelled-target.bin");
        File.WriteAllBytes(cancelledPath, sentinel);
        using (var source = OpenTestFile(scope, sourcePath, FileAccess.Read))
        using (var target = OpenTestFile(scope, cancelledPath, FileAccess.ReadWrite))
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            AssertCancelled(scope, () => CallRaw("CopyImage", source, target, sector, new NoProgress(), cancelled.Token), "镜像写入");
            target.Flush();
            target.Position = 0;
            byte[] actual = new byte[sentinel.Length];
            target.ReadExactly(actual);
            scope.Assert(actual.SequenceEqual(sentinel) && target.Length == sentinel.Length, "预取消镜像写入不更改目标字节或长度");
            AssertCancelled(scope, () => CallRaw("ClearOldLayout", target, target.Length, sector, cancelled.Token), "旧分区清理");
            target.Flush();
            target.Position = 0;
            target.ReadExactly(actual);
            scope.Assert(actual.SequenceEqual(sentinel) && target.Length == sentinel.Length, "预取消分区清理不更改目标字节或长度");
            AssertCancelled(scope, () => CallRaw("VerifyImage", source, target, sector, new NoProgress(), cancelled.Token), "读回校验");
            target.Position = 0;
            target.ReadExactly(actual);
            scope.Assert(actual.SequenceEqual(sentinel), "预取消校验不修改目标内容");
            scope.Passed.Add("纯文件预取消：写入、分区清理和校验均取消且目标内容保持不变");
        }

        // Exercise the production Win32 offset/read/write adapter on an isolated
        // regular file. This never opens a physical device or changes disk state.
        string nativePath = scope.FilePath("native-io-target.bin");
        File.WriteAllBytes(nativePath, Enumerable.Repeat((byte)0xA5, 6 * clearBytes).ToArray());
        using (var file = OpenTestFile(scope, nativePath, FileAccess.ReadWrite))
        using (var source = OpenTestFile(scope, sourcePath, FileAccess.Read))
        using (var native = OpenNativeAdapter(file, writable: true))
        {
            CallRaw("ClearOldLayout", native, native.Length, sector, CancellationToken.None);
            native.Flush();
            native.Position = native.Length - sector;
            var tail = new byte[sector];
            native.ReadExactly(tail);
            scope.Assert(tail.All(b => b == 0), "原生定位后读取已清零的磁盘尾部");
            CallRaw("CopyImage", source, native, sector, new NoProgress(), CancellationToken.None);
            native.Flush();
            CallRaw("VerifyImage", source, native, sector, new NoProgress(), CancellationToken.None);
            scope.Assert(native.Position == ((source.Length + sector - 1) / sector) * sector,
                "原生校验从偏移 0 覆盖全部镜像并补齐末尾扇区");
            native.Position = 0;
            var first = new byte[sector];
            native.ReadExactly(first);
            scope.Assert(first.SequenceEqual(rawBytes.Take(sector)), "原生首扇区写入与源镜像一致");
            native.Position = 4 * clearBytes;
            var final = new byte[sector];
            native.ReadExactly(final);
            scope.Assert(final.Take(123).SequenceEqual(rawBytes.Skip(4 * clearBytes)), "原生跨缓冲区定位正确");
            scope.Assert(final.Skip(123).All(b => b == 0), "原生末尾扇区填充为零");
            bool unaligned = false;
            try { native.Position = 1; } catch (IOException) { unaligned = true; }
            scope.Assert(unaligned, "原生接口拒绝非整扇区定位");
            native.Position = native.Length;
            bool overrun = false;
            try { native.Write(first, 0, first.Length); } catch (IOException) { overrun = true; }
            scope.Assert(overrun, "原生接口拒绝超出目标容量写入");
            native.Position = 512;
            var corrupt = new byte[sector];
            native.ReadExactly(corrupt);
            byte expected = corrupt[16];
            corrupt[16] ^= 0x80;
            native.Position = 512;
            native.Write(corrupt, 0, corrupt.Length);
            native.Flush();
            bool exactMismatch = false;
            try { CallRaw("VerifyImage", source, native, sector, new NoProgress(), CancellationToken.None); }
            catch (IOException ex) { exactMismatch = ex.Message.Contains("528") && ex.Message.Contains($"0x{expected:X2}"); }
            scope.Assert(exactMismatch, "原生校验准确报告 GPT 头偏移 528 字节及预期内容");
        }
        using (var file = OpenTestFile(scope, nativePath, FileAccess.Read))
        using (var native = OpenNativeAdapter(file, writable: false))
        {
            bool rejected = false;
            try { native.Write(new byte[sector], 0, sector); } catch (NotSupportedException) { rejected = true; }
            scope.Assert(rejected, "只读原生适配器拒绝写入");
        }
        scope.Passed.Add("原生磁盘 I/O 适配器：普通测试文件上的首尾定位、跨块写入、读回、越界和只读保护通过");
    }

    private static Stream OpenNativeAdapter(FileStream file, bool writable)
    {
        var type = typeof(RawDiskWriter).Assembly.GetType("UsbBatchTool.Services.PhysicalDiskStream", true)!;
        return (Stream)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: new object[] { file.SafeFileHandle, file.Length, 512, writable }, culture: null)!;
    }

    private static FileStream OpenTestFile(TestScope scope, string path, FileAccess access, FileMode mode = FileMode.Open)
    {
        // This guard prevents test refactors from accidentally opening a device path.
        string full = Path.GetFullPath(path);
        string expectedParent = Path.GetFullPath(Path.GetDirectoryName(scope.FilePath("guard"))!);
        if (!string.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(@"\\", StringComparison.Ordinal)
            || full.Contains("PhysicalDrive", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许打开本次测试临时目录内的普通文件。");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("测试目标不能是重解析点。");
        return new FileStream(full, mode, access, FileShare.None, 4096, FileOptions.None);
    }

    private static void CallRaw(string name, params object[] arguments)
    {
        // Only these three stream algorithms are permitted. Never invoke
        // WriteAsync, WriteCore, NativeDisk, DeviceScanner, or any physical API.
        if (name != "CopyImage" && name != "VerifyImage" && name != "ClearOldLayout")
            throw new InvalidOperationException("不允许调用此写入器方法。");
        MethodInfo method = typeof(RawDiskWriter).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(RawDiskWriter).FullName, name);
        try { method.Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static void AssertCancelled(TestScope scope, Action action, string name)
    {
        bool cancelled = false;
        try { action(); }
        catch (OperationCanceledException) { cancelled = true; }
        scope.Assert(cancelled, "预取消时" + name + "必须抛出 OperationCanceledException");
    }

    private static void WriteZip(string path, (string Name, byte[] Content, int Attributes)[] entries)
    {
        using var output = File.Create(path);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2025, 1, 2, 3, 4, 0, TimeSpan.Zero);
            entry.ExternalAttributes = item.Attributes;
            using var content = entry.Open();
            content.Write(item.Content, 0, item.Content.Length);
        }
    }
}
'@

$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
$syntaxTrees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($runnerSource, $parseOptions))
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$options = $options.WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(('UsbServiceTests_' + [Guid]::NewGuid().ToString('N')), $syntaxTrees, $references, $options)
$peStream = [IO.MemoryStream]::new()
try {
    $emitResult = $compilation.Emit($peStream)
    if (-not $emitResult.Success) {
        $failures = @($emitResult.Diagnostics | Where-Object Severity -eq Error | ForEach-Object ToString)
        throw ('测试运行器编译失败：' + [Environment]::NewLine + ($failures -join [Environment]::NewLine))
    }
    [Reflection.Assembly]::Load($peStream.ToArray()) | Out-Null
}
finally { $peStream.Dispose() }
$result = [UsbServiceTestRunner]::RunAsync($scratchRoot, $expectedBase).GetAwaiter().GetResult()
foreach ($passed in $result.Passed) {
    Write-Host "通过：$passed" -ForegroundColor Green
}
Write-Host "服务测试全部通过：$($result.Passed.Count) 组，$($result.Assertions) 个断言。"
Write-Host "已清理本次生成的 $($result.CleanedCacheDirectories) 个解压缓存目录和临时测试文件。"
Write-Host "缓存基目录：$($result.CacheBase)；未清空其他缓存，未访问任何物理磁盘。"
