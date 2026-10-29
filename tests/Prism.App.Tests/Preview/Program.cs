using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Prism.App;
using Prism.App.Common;
using Prism.App.Navigation;
using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.App.Views;
using Prism.App.Widgets;

// Manual, offscreen WPF QA. Does not start App, timers, pipes, registry, tray, or widget windows.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static int Run(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "tests/Prism.App.Tests/obj/preview");
        Directory.CreateDirectory(output);
        var errors = new StringBuilder();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(new StringWriter(errors)));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var file in new[] { "Themes/Prism.xaml", "Views/PageTemplates.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Prism.App;component/" + file, UriKind.Relative) });
        var client = new MockPrismClient();
        var settings = new AppSettings { ReduceAnimations = true };
        var vm = new MainViewModel(client, settings, new SettingsStore(Path.Combine(output,"settings.json")),
            new InMemoryStartupRegistry(), new WidgetLayoutStore(Path.Combine(output,"widgets.json")), new NullWidgetHost());
        Motion.ReduceAnimations = true;
        var window = new MainWindow(vm);
        var shell = (FrameworkElement)window.Content;
        Render(shell, Path.Combine(output,"Shell.png"),1320);
        foreach (var page in Enum.GetValues<PageKey>())
        {
            vm.Navigate(page);
            var template = (DataTemplate)app.Resources[new DataTemplateKey(vm.CurrentPage.GetType())];
            var view = (FrameworkElement)template.LoadContent();
            view.DataContext = vm.CurrentPage;
            Render(view, Path.Combine(output,page + ".png"), 1020);
        }
        foreach (var mode in vm.Modem.Views)
        {
            vm.Modem.SelectedView = mode;
            var template = (DataTemplate)app.Resources[new DataTemplateKey(typeof(ModemViewModel))];
            var view = (FrameworkElement)template.LoadContent(); view.DataContext = vm.Modem;
            Render(view, Path.Combine(output,"Modem-" + mode + ".png"),1020);
        }
        foreach (var kind in Enum.GetValues<WidgetKind>())
        {
            var widget = new WidgetViewModel(new WidgetLayout { Kind = kind, Metrics = WidgetMetrics.Defaults(kind) },client);
            widget.Apply(client.CurrentState);
            Render(new WidgetContent { DataContext = widget }, Path.Combine(output,"Widget-"+kind+".png"),widget.Width);
        }
        window.Close();
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        app.Shutdown();
        PresentationTraceSources.DataBindingSource.Flush();
        File.WriteAllText(Path.Combine(output,"bindings.log"),errors.ToString());
        Console.WriteLine("Rendered nine pages, four modem modes and ten widget types to " + output);
        if (errors.Length > 0) { Console.Error.WriteLine(errors); return 1; }
        return 0;
    }
    private static void Render(FrameworkElement view, string path, double width)
    {
        FrameworkElement frame = view;
        if (view.Parent is null)
        {
            frame = new Border { Background = (Brush)Application.Current.Resources["PageBrush"], Child = view, Width = width };
            TextElement.SetForeground(frame, (Brush)Application.Current.Resources["TextPrimaryBrush"]);
        }
        view.Width = width;
        frame.Measure(new Size(width,double.PositiveInfinity));
        frame.Arrange(new Rect(0,0,width,Math.Max(1,frame.DesiredSize.Height)));
        frame.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap((int)width,(int)Math.Ceiling(frame.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
