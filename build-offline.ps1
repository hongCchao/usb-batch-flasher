[CmdletBinding()]
param(
    [string]$RuntimeDirectory = (Split-Path -Parent (Get-Process -Id $PID).Path),
    [switch]$PackageRuntime,
    [switch]$WindowsApplication,
    [string]$OutputDirectory = ''
)

# Offline fallback: use the Roslyn compiler and Windows desktop runtime already
# bundled with PowerShell. No SDK download or global installation is performed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = $PSScriptRoot
$sourceDirectory = Join-Path $projectRoot 'src\UsbBatchTool'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot 'dist\offline' }
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$assemblyPath = Join-Path $outputDirectory 'UsbBatchTool.Offline.dll'
$stagedAssemblyPath = Join-Path $outputDirectory 'UsbBatchTool.Offline.next.dll'
$runtimeDirectoryPath = [IO.Path]::GetFullPath($RuntimeDirectory)

foreach ($requiredName in @('pwsh.exe', 'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll',
    'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll', 'System.Management.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $runtimeDirectoryPath $requiredName) -PathType Leaf)) {
        throw "离线运行时缺少 $requiredName：$runtimeDirectoryPath"
    }
}

Add-Type -Path (Join-Path $runtimeDirectoryPath 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $runtimeDirectoryPath 'Microsoft.CodeAnalysis.CSharp.dll')

$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new(
    [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
$syntaxTrees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$globalUsings = @'
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
'@
$syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
    $globalUsings, $parseOptions, 'Offline.GlobalUsings.g.cs', [Text.Encoding]::UTF8))

$sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | Sort-Object FullName)
if ($sourceFiles.Count -eq 0) { throw "没有找到 C# 源文件：$sourceDirectory" }
foreach ($sourceFile in $sourceFiles) {
    $syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
        [IO.File]::ReadAllText($sourceFile.FullName), $parseOptions,
        $sourceFile.FullName, [Text.Encoding]::UTF8))
}

$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($runtimeAssembly in (Get-ChildItem -LiteralPath $runtimeDirectoryPath -Filter '*.dll' -File)) {
    try {
        [Reflection.AssemblyName]::GetAssemblyName($runtimeAssembly.FullName) | Out-Null
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($runtimeAssembly.FullName))
    }
    catch [BadImageFormatException] {
        # Native DLLs are runtime payload, not compiler references.
    }
}

$outputKind = if ($WindowsApplication) { [Microsoft.CodeAnalysis.OutputKind]::WindowsApplication } else { [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary }
$compilationOptions = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new($outputKind)
if ($WindowsApplication) { $compilationOptions = $compilationOptions.WithMainTypeName('UsbBatchTool.EntryPoint') }
$compilationOptions = $compilationOptions.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compilationOptions = $compilationOptions.WithPlatform([Microsoft.CodeAnalysis.Platform]::X64)
$compilationOptions = $compilationOptions.WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    'UsbBatchTool.Offline', $syntaxTrees, $references, $compilationOptions)

$resourceFiles = [ordered]@{
    'UsbBatchTool.App.xaml' = (Join-Path $sourceDirectory 'App.xaml')
    'UsbBatchTool.MainWindow.xaml' = (Join-Path $sourceDirectory 'MainWindow.xaml')
    'UsbBatchTool.Assets.connected.png' = (Join-Path $projectRoot '已插入.png')
    'UsbBatchTool.Assets.disconnected.png' = (Join-Path $projectRoot '未插入.png')
    'UsbBatchTool.Assets.failed.png' = (Join-Path $projectRoot '烧录失败.png')
    'UsbBatchTool.Assets.app.ico' = (Join-Path $projectRoot 'app.ico')
}
$resources = [Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
foreach ($resourceName in $resourceFiles.Keys) {
    $resourceFilePath = $resourceFiles[$resourceName]
    if (-not (Test-Path -LiteralPath $resourceFilePath -PathType Leaf)) {
        throw "缺少待嵌入资源：$resourceFilePath"
    }
    $providerScript = { [IO.File]::OpenRead($resourceFilePath) }.GetNewClosure()
    $streamProvider = [Func[IO.Stream]]$providerScript
    $resources.Add([Microsoft.CodeAnalysis.ResourceDescription]::new($resourceName, $streamProvider, $true))
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$peStream = [IO.MemoryStream]::new()
try {
    $emitResult = $compilation.Emit($peStream, $null, $null, $null, $resources)
    $diagnostics = @($emitResult.Diagnostics | Where-Object { $_.Severity -ne 'Hidden' })
    foreach ($diagnostic in $diagnostics) {
        if ($diagnostic.Severity -eq 'Error') {
            Write-Host $diagnostic.ToString() -ForegroundColor Red
        }
        elseif ($diagnostic.Severity -eq 'Warning') {
            Write-Host $diagnostic.ToString() -ForegroundColor Yellow
        }
    }
    if (-not $emitResult.Success) {
        throw '离线 Roslyn 编译失败。尚未复制运行时，请先修复以上编译错误。'
    }
    try {
        [IO.File]::WriteAllBytes($assemblyPath, $peStream.ToArray())
        if (Test-Path -LiteralPath $stagedAssemblyPath) { Remove-Item -LiteralPath $stagedAssemblyPath -Force }
    }
    catch [System.IO.IOException] {
        # The running WPF process locks its loaded assembly. Stage the new build
        # beside it; launch.ps1 installs it on the next start after the lock ends.
        [IO.File]::WriteAllBytes($stagedAssemblyPath, $peStream.ToArray())
        Write-Host "程序正在运行，更新已暂存：$stagedAssemblyPath" -ForegroundColor Yellow
    }
}
finally {
    $peStream.Dispose()
}

Write-Host "编译通过：$assemblyPath" -ForegroundColor Green
Write-Host "C# 源文件：$($sourceFiles.Count)；托管程序集引用：$($references.Count)"
if (-not $PackageRuntime) {
    Write-Host '如需生成可运行离线包，请执行：.\build-offline.ps1 -PackageRuntime'
    return
}

$packagedRuntime = Join-Path $outputDirectory 'runtime'
New-Item -ItemType Directory -Path $packagedRuntime -Force | Out-Null
Write-Host '正在复制本机已有 Windows 桌面运行时（约 250 MB）……'
Copy-Item -Path (Join-Path $runtimeDirectoryPath '*') -Destination $packagedRuntime -Recurse -Force

$launchScript = @'
$ErrorActionPreference = 'Stop'
try {
    $assemblyFile = Join-Path $PSScriptRoot 'UsbBatchTool.Offline.dll'
    $stagedAssemblyFile = Join-Path $PSScriptRoot 'UsbBatchTool.Offline.next.dll'
    if (Test-Path -LiteralPath $stagedAssemblyFile) {
        try {
            Copy-Item -LiteralPath $stagedAssemblyFile -Destination $assemblyFile -Force -ErrorAction Stop
            Remove-Item -LiteralPath $stagedAssemblyFile -Force -ErrorAction Stop
        }
        catch {
            # Another instance may still hold the original assembly open.
            $assemblyFile = $stagedAssemblyFile
        }
    }
    [Reflection.Assembly]::LoadFrom($assemblyFile) | Out-Null
    [UsbBatchTool.EntryPoint]::Main([string[]]$args)
}
catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.ToString(), 'USB 批量烧录工具启动失败') | Out-Null
    exit 1
}
'@
$launchPath = Join-Path $outputDirectory 'launch.ps1'
[IO.File]::WriteAllText($launchPath, $launchScript, [Text.UTF8Encoding]::new($false))

$launcherSource = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class UsbBatchLauncher
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string runtime = Path.Combine(root, "runtime", "pwsh.exe");
            string script = Path.Combine(root, "launch.ps1");
            if (!File.Exists(runtime) || !File.Exists(script))
                throw new FileNotFoundException("请保留软件所在目录内的 runtime 文件夹和 launch.ps1。");
            string arguments = "-NoLogo -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File " + Quote(script);
            foreach (string argument in args) arguments += " " + Quote(argument);
            ProcessStartInfo start = new ProcessStartInfo(runtime, arguments);
            start.WorkingDirectory = root;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process process = Process.Start(start))
            {
                process.WaitForExit();
                Environment.ExitCode = process.ExitCode;
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "USB 批量烧录工具启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }

    private static string Quote(string value)
    {
        StringBuilder escaped = new StringBuilder("\"");
        int slashCount = 0;
        foreach (char character in value ?? "")
        {
            if (character == '\\') { slashCount++; continue; }
            if (character == '"')
            {
                escaped.Append('\\', slashCount * 2 + 1);
                escaped.Append(character);
            }
            else
            {
                escaped.Append('\\', slashCount);
                escaped.Append(character);
            }
            slashCount = 0;
        }
        escaped.Append('\\', slashCount * 2);
        escaped.Append('"');
        return escaped.ToString();
    }
}
'@
$launcherSourcePath = Join-Path $outputDirectory 'Launcher.cs'
[IO.File]::WriteAllText($launcherSourcePath, $launcherSource, [Text.UTF8Encoding]::new($true))
$frameworkCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $frameworkCompiler -PathType Leaf)) {
    throw "离线启动器需要系统 .NET Framework C# 编译器：$frameworkCompiler"
}
$launcherExecutable = Join-Path $outputDirectory 'USB批量烧录工具.exe'
& $frameworkCompiler /nologo /target:winexe /platform:x64 /optimize+ `
    "/out:$launcherExecutable" `
    "/win32icon:$(Join-Path $projectRoot 'app.ico')" `
    "/win32manifest:$(Join-Path $sourceDirectory 'app.manifest')" `
    /reference:System.Windows.Forms.dll $launcherSourcePath
if ($LASTEXITCODE -ne 0) { throw "离线启动器编译失败：$LASTEXITCODE" }
Write-Host "离线包完成：$launcherExecutable" -ForegroundColor Green
Write-Host '请整体复制 offline 目录；标准 .NET SDK 自包含单文件仍由 build.ps1 生成。'
