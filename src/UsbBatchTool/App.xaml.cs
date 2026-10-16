using System.IO;
using System.Windows;
using UsbBatchTool.Services;

namespace UsbBatchTool;

public partial class App : Application
{
    public static string[] LaunchArguments { get; set; } = [];
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(AppPaths.Logs, "crash.log"), $"{DateTime.Now:O} {args.Exception}\n"); } catch { }
            MessageBox.Show(args.Exception.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            AppPaths.EnsureDirectories();
            var window = new MainWindow(e.Args.Contains("--demo") || LaunchArguments.Contains("--demo"));
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法启动软件：" + ex.Message + "\n请将软件放在可写入的目录中。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
