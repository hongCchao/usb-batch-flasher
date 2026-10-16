using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace UsbBatchTool.Services;

public sealed record PreparedSource(string OriginalPath, string PayloadPath, string Sha256, long TotalBytes, bool IsZip, bool CacheHit);

public sealed class CacheService
{
    private sealed record CacheEntry(string RelativePath, long Size, string Sha256);
    private sealed record Manifest(string Sha256, long Size, long ModifiedTicks, string Kind, string PayloadName, long ExtractedBytes, DateTime CompletedAt, List<CacheEntry>? Files = null, List<string>? Directories = null);
    private const long MaxZipExtractedBytes = 1L << 40;
    private const int MaxZipEntries = 200000;
    private const long MaxGzipExtractedBytes = 1L << 40;

    public async Task<PreparedSource> PrepareAsync(string source, bool zip, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("源文件不存在，请重新选择。", source);
        var info = new FileInfo(source);
        long length = info.Length, modified = info.LastWriteTimeUtc.Ticks;
        bool gzip = source.EndsWith(".img.gz", StringComparison.OrdinalIgnoreCase);
        if (zip ? !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) : !gzip && !source.EndsWith(".img", StringComparison.OrdinalIgnoreCase) && !source.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件格式与当前模式不匹配。");
        if (length == 0) throw new InvalidDataException("源文件为空。");

        string hash;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[4 * 1024 * 1024];
            long done = 0;
            var watch = Stopwatch.StartNew();
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, n); done += n;
                progress.Report(new("计算 SHA-256", done, length, done / Math.Max(watch.Elapsed.TotalSeconds, 0.01)));
            }
            hash = Convert.ToHexString(sha.GetHashAndReset());
        }
        var current = new FileInfo(source);
        if (current.Length != length || current.LastWriteTimeUtc.Ticks != modified) throw new IOException("计算校验值期间源文件发生变化，请重新选择。");
        if (!zip && !gzip) return new(source, source, hash, length, false, false);

        Directory.CreateDirectory(AppPaths.Cache);
        EnsureNoReparse(AppPaths.Cache);
        var root = Path.Combine(AppPaths.Cache, hash.ToLowerInvariant());
        if (Directory.Exists(root)) EnsureNoReparse(root);
        var manifestPath = Path.Combine(root, "source.json");
        string payloadName = zip ? "extracted" : "image.img";
        string payload = Path.Combine(root, payloadName);
        if (File.Exists(manifestPath))
        {
            try
            {
                var m = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, ct));
                if (m is not null && m.Sha256 == hash && m.Kind == (zip ? "zip" : "gzip") && m.Size == length && m.Files is not null &&
                    await VerifyCacheAsync(root, payload, m, zip, ct))
                    return new(source, payload, hash, m.ExtractedBytes, zip, true);
            }
            catch (JsonException) { }
        }
        // Partial output is never reused. Only application-owned hash directories are removed.
        if (Directory.Exists(root)) { EnsureNoReparseRecursive(root); Directory.Delete(root, true); }
        Directory.CreateDirectory(root);
        try
        {
            long extracted;
            if (zip) extracted = await ExtractZipAsync(source, payload, progress, ct);
            else extracted = await ExtractGzipAsync(source, payload, progress, ct);
            if (extracted <= 0 && !zip) throw new InvalidDataException("压缩文件内没有可写入的数据。");
            if (zip && !Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Any()) throw new InvalidDataException("ZIP 中没有可复制的文件。");
            current.Refresh();
            if (current.Length != length || current.LastWriteTimeUtc.Ticks != modified) throw new IOException("解压期间源文件发生变化，缓存未保留。");
            var entries = await InventoryAsync(root, payload, zip, ct);
            var directories = zip ? Directory.GetDirectories(payload, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(payload, d).Replace('\\', '/')).ToList() : [];
            var manifest = new Manifest(hash, length, modified, zip ? "zip" : "gzip", payloadName, extracted, DateTime.Now, entries, directories);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
            return new(source, payload, hash, extracted, zip, false);
        }
        catch
        {
            if (Directory.Exists(root)) { EnsureNoReparseRecursive(root); Directory.Delete(root, true); }
            throw;
        }
    }

    private static async Task<long> ExtractGzipAsync(string source, string destination, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        byte[] buffer = new byte[4 * 1024 * 1024];
        long total = 0;
        int count;
        while ((count = await gzip.ReadAsync(buffer, ct)) > 0)
        {
            total = checked(total + count);
            if (total > MaxGzipExtractedBytes) throw new InvalidDataException("IMG.GZ 解压大小超过安全上限（1 TiB）。");
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace < count + 16L * 1024 * 1024) throw new IOException("解压缓存磁盘空间不足。");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            progress.Report(new("解压 IMG.GZ", input.Position, input.Length, 0, $"已解压 {DisplayBytes.Format(total)}"));
        }
        await output.FlushAsync(ct);
        return total;
    }

    private static async Task<long> ExtractZipAsync(string source, string root, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count > MaxZipEntries) throw new InvalidDataException("ZIP 文件数量超过安全上限（200000）。");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ValidateZipEntry(entry);
            string path = SafeChildPath(root, entry.FullName);
            if (!seen.Add(path)) throw new InvalidDataException("ZIP 中存在大小写冲突或重复路径：" + entry.FullName);
            total = checked(total + entry.Length);
            if (total > MaxZipExtractedBytes) throw new InvalidDataException("ZIP 解压大小超过安全上限（1 TiB）。");
        }
        string driveRoot = Path.GetPathRoot(Path.GetFullPath(root))!;
        if (new DriveInfo(driveRoot).AvailableFreeSpace < total + 16L * 1024 * 1024) throw new IOException("缓存所在磁盘空间不足。");
        byte[] buffer = new byte[1024 * 1024];
        long done = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string path = SafeChildPath(root, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            int n;
            long entryDone = 0;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                entryDone += n;
                if (entryDone > entry.Length) throw new InvalidDataException("ZIP 条目实际长度不符：" + entry.FullName);
                await output.WriteAsync(buffer.AsMemory(0, n), ct); done += n;
                progress.Report(new("解压 ZIP", done, total, 0, entry.FullName));
            }
            if (entryDone != entry.Length) throw new InvalidDataException("ZIP 条目不完整：" + entry.FullName);
        }
        return done;
    }

    private static void ValidateZipEntry(ZipArchiveEntry entry)
    {
        // Reject links and Windows device names/alternate streams instead of following arbitrary paths.
        int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("ZIP 不允许包含符号链接：" + entry.FullName);
        foreach (string part in entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part == ".." || part.Contains(':') || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem))
                throw new InvalidDataException("ZIP 包含不安全或不兼容的路径：" + entry.FullName);
        }
    }

    public static string SafeChildPath(string root, string relative)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出目标目录：" + relative);
        return path;
    }

    public void Clear()
    {
        string expected = Path.GetFullPath(Path.Combine(AppPaths.Base, "cache"));
        string actual = Path.GetFullPath(AppPaths.Cache);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new IOException("缓存目录验证失败。");
        if (Directory.Exists(actual))
        {
            EnsureNoReparse(actual);
            foreach (string path in Directory.EnumerateFileSystemEntries(actual))
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    EnsureNoReparseRecursive(path);
                    Directory.Delete(path, true);
                }
                else File.Delete(path);
            }
        }
        else Directory.CreateDirectory(actual);
    }

    private static void EnsureNoReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存目录包含目录链接，已阻止操作：" + path);
    }

    private static void EnsureNoReparseRecursive(string path)
    {
        EnsureNoReparse(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存内存在链接，已阻止清理：" + entry);
            if (Directory.Exists(entry)) EnsureNoReparseRecursive(entry);
        }
    }

    private static async Task<List<CacheEntry>> InventoryAsync(string root, string payload, bool zip, CancellationToken ct)
    {
        var files = zip ? Directory.GetFiles(payload, "*", SearchOption.AllDirectories) : [payload];
        var entries = new List<CacheEntry>(files.Length);
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            EnsureNoReparse(file);
            await using var stream = File.OpenRead(file);
            var sha = await SHA256.HashDataAsync(stream, ct);
            entries.Add(new(Path.GetRelativePath(root, file), stream.Length, Convert.ToHexString(sha)));
        }
        return entries;
    }

    private static async Task<bool> VerifyCacheAsync(string root, string payload, Manifest manifest, bool zip, CancellationToken ct)
    {
        if (manifest.Files is null || manifest.Files.Count == 0 || zip && manifest.Directories is null || !Directory.Exists(root) || zip && !Directory.Exists(payload) || !zip && !File.Exists(payload)) return false;
        EnsureNoReparseRecursive(root);
        if (zip)
        {
            var actualDirectories = Directory.GetDirectories(payload, "*", SearchOption.AllDirectories)
                .Select(d => Path.GetRelativePath(payload, d).Replace('\\', '/')).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            if (!actualDirectories.SequenceEqual(manifest.Directories!.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) return false;
        }
        var actual = await InventoryAsync(root, payload, zip, ct);
        var expected = manifest.Files.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var found = actual.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (expected.Length != found.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (!string.Equals(expected[i].RelativePath, found[i].RelativePath, StringComparison.OrdinalIgnoreCase) ||
                expected[i].Size != found[i].Size || !string.Equals(expected[i].Sha256, found[i].Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        return actual.Sum(x => x.Size) == manifest.ExtractedBytes;
    }
}
