using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UsbBatchTool;

public partial class App
{
    public void InitializeComponent()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsbBatchTool.App.xaml") ?? throw new FileNotFoundException("缺少界面样式资源");
        Resources = (ResourceDictionary)XamlReader.Load(stream);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }
}

public static class EmbeddedAssets
{
    public static ImageSource Icon(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsbBatchTool.Assets." + name) ?? throw new FileNotFoundException(name);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames
            .Where(f => f.PixelWidth >= 48 && f.PixelHeight >= 48)
            .OrderBy(f => f.PixelWidth * f.PixelHeight)
            .FirstOrDefault() ?? decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
        frame.Freeze();
        return frame;
    }

    public static BitmapImage Image(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsbBatchTool.Assets." + name) ?? throw new FileNotFoundException(name);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
}

public static class EntryPoint
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    public static void Main(string[] args)
    {
        bool smoke = args.Contains("--native-smoke");
        try
        {
            SetCurrentProcessExplicitAppUserModelID("UsbBatchTool.Desktop.1");
            App.LaunchArguments = args;
            var app = new App();
            app.InitializeComponent();
            if (smoke)
            {
                var window = new MainWindow(true);
                window.Measure(new Size(1300, 960));
                window.Arrange(new Rect(0, 0, 1300, 960));
                window.UpdateLayout();
                File.WriteAllText(Path.Combine(Services.AppPaths.Base, "native-smoke.ok"), $"{DateTime.Now:O}\nWPF resources and 7 port window loaded.\n");
                return;
            }
            app.Run();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Services.AppPaths.Base, smoke ? "native-smoke.error" : "startup-error.log"), ex.ToString()); } catch { }
            if (!smoke) MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 1;
        }
    }
}
