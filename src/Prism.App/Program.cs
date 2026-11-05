namespace Prism.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // Set the same DPI awareness as the old manifest before WPF creates any HWNDs.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
