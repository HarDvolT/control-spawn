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
using Prism.App.Controls;
using System.Windows.Controls.Primitives;
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
        foreach (var file in new[] { "Themes/Prism.xaml", "Views/Pages/Devices/Devices.xaml", "Views/Pages/AirFryer/AirFryer.xaml", "Views/Pages/Dashboard/Dashboard.xaml", "Views/Pages/Monitor/Monitor.xaml", "Views/Pages/Lighting/Lighting.xaml", "Views/Pages/Fans/Fans.xaml", "Views/Pages/Lamp/Lamp.xaml", "Views/Pages/Scenes/Scenes.xaml", "Views/Pages/Modem/Modem.xaml", "Views/Pages/Settings/Settings.xaml", "Views/Pages/Widgets/Widgets.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Prism.App;component/" + file, UriKind.Relative) });
        if (args.Contains("--sensor"))
        {
            var sensorClient = new MockPrismClient();
            sensorClient.SendAsync(Prism.Core.Ipc.Commands.SetSensorBinding, new { target = "gpu", source = "gpu.temperature", min = 35, max = 85, smoothing = 0.5 }).GetAwaiter().GetResult();
            sensorClient.SendAsync(Prism.Core.Ipc.Commands.SetSensorBinding, new { target = "ram", source = "cpu.load", min = 0, max = 100, style = "FillBar" }).GetAwaiter().GetResult();
            var sensorModel = new LightingViewModel(sensorClient); sensorModel.Apply(sensorClient.CurrentState);
            Motion.ReduceAnimations = true;
            Render(new SensorBindingPanel { DataContext = sensorModel }, Path.Combine(output, "Sensor-panel.png"), 1180);
            Render(new SensorBindingPanel { DataContext = sensorModel }, Path.Combine(output, "Sensor-panel-narrow.png"), 640);
            foreach (var (name, width) in new[] { ("Sensor-wide.png", 1100d), ("Sensor-narrow.png", 700d) })
            {
                var workspace = new LightingWorkspace { DataContext = sensorModel };
                // Switch before the first layout: the effect gallery this mode hides can't lay out at 700 px.
                LogicalDescendants(workspace).OfType<SegmentedControl>().First(c => c.Items.Contains("Sensor")).SelectedIndex = 2;
                Render(workspace, Path.Combine(output, name), width);
            }
            Render(new CaseMapPreview { DataContext = sensorModel, Height = 500, PreviewTime = 1 }, Path.Combine(output, "Sensor-case-map.png"), 700);
            sensorModel.Apply(sensorClient.CurrentState with { System = sensorClient.CurrentState.System with { CpuLoad = null } });
            Render(new SensorBindingPanel { DataContext = sensorModel }, Path.Combine(output, "Sensor-missing.png"), 1180);
            Render(new CaseMapPreview { DataContext = sensorModel, Height = 500, PreviewTime = 1 }, Path.Combine(output, "Sensor-fallback.png"), 700);
            sensorClient.SendAsync(Prism.Core.Ipc.Commands.SetSensorBinding, new { target = "gpu", source = "gpu.temperature", min = 35, max = 85, alert = new { threshold = 40 } }).GetAwaiter().GetResult();
            sensorModel.Apply(sensorClient.CurrentState);
            Render(new CaseMapPreview { DataContext = sensorModel, Height = 500, PreviewTime = 1.25 }, Path.Combine(output, "Sensor-alert.png"), 700);
            File.WriteAllText(Path.Combine(output, "bindings.log"), errors.ToString());
            sensorClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return errors.Length == 0 ? 0 : 2;
        }
        if (args.Contains("--widgets"))
        {
            var widgetClient = new MockPrismClient();
            var sheet = new WrapPanel { Width = 1120, Background = new LinearGradientBrush(Color.FromRgb(42, 22, 40), Color.FromRgb(12, 32, 48), 45) };
            foreach (var kind in Enum.GetValues<WidgetKind>())
            {
                var widget = new WidgetViewModel(new WidgetLayout { Kind = kind, Metrics = WidgetMetrics.Defaults(kind), Background = "Glass" }, widgetClient);
                widget.Apply(widgetClient.CurrentState);
                sheet.Children.Add(new Border { Margin = new(20), Child = new Prism.App.Widgets.WidgetCard(widget), VerticalAlignment = VerticalAlignment.Top });
            }
            Render(sheet, Path.Combine(output, "Widgets-sheet.png"), 1120);
            widgetClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--v2"))
        {
            var previewClient = new MockPrismClient();
            // --lighting-mode=sensor (or device) opens the Lighting page on that tab; sensor mode seeds a few bindings first.
            var lightingMode = args.FirstOrDefault(a => a.StartsWith("--lighting-mode="))?[16..] switch { "device" => 1, "sensor" => 2, _ => 0 };
            if (lightingMode == 2) SeedSensorBindings(previewClient);
            var previewModel = new MainViewModel(previewClient, new AppSettings(),
                new SettingsStore(Path.Combine(output, "settings.json")), new InMemoryStartupRegistry(),
                new WidgetLayoutStore(Path.Combine(output, "widgets.json")), new NullWidgetHost());
            Motion.ReduceAnimations = true;
            var previewWindow = new MainWindow(previewModel);
            var previewShell = (FrameworkElement)previewWindow.Content;
            foreach (var page in Enum.GetValues<PageKey>().Where(p => !args.Any(a => a.StartsWith("--page=")) || args.Contains("--page=" + p)))
            {
                previewModel.Navigate(page);
                previewWindow.Close();
                previewWindow = new MainWindow(previewModel);
                previewShell = (FrameworkElement)previewWindow.Content;
                if (page == PageKey.Lighting && lightingMode > 0) SelectLightingMode(previewShell, lightingMode);
                var designName = page switch { PageKey.Lamp => "Lamps", PageKey.Appliances => "Fryer", _ => page.ToString() };
                var reference = BitmapFrame.Create(new Uri(Path.GetFullPath($"docs/design-v2/V2{designName}.png")));
                var height = args.FirstOrDefault(a => a.StartsWith("--height=")) is { } h && int.TryParse(h[9..], out var forced) ? forced : reference.PixelHeight;
                previewShell.Measure(new Size(1440, height)); previewShell.Arrange(new Rect(0, 0, 1440, height)); previewShell.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
                previewShell.InvalidateMeasure(); previewShell.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1440, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(previewShell);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, "V2-" + page + ".png")); encoder.Save(stream);
                Console.WriteLine(page);
            }
            PresentationTraceSources.DataBindingSource.Flush();
            File.WriteAllText(Path.Combine(output, "bindings.log"), errors.ToString());
            previewWindow.Close();
            previewClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return errors.Length == 0 ? 0 : 2;
        }
        VerifyControls(output);
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
        var foundation = new CardGrid();
        foundation.Children.Add(new GlassCard { Size = CardSize.M, Span = 4, Content = new RadialGauge { Value = 78, SensorKind = SensorKind.CpuTctl, Suffix = "\u00b0C" } });
        foundation.Children.Add(new GlassCard { Size = CardSize.M, Span = 4, Content = new ArcSpeedometer { Value = 2650, Maximum = 4000, Suffix = " RPM" } });
        var meters = new StackPanel();
        meters.Children.Add(new ThermalBar { Value = 92, SensorKind = SensorKind.GpuHotspot, Maximum = 110, Suffix = "\u00b0C" });
        meters.Children.Add(new SegmentedCapsule { Value = 44, Suffix = "%", Margin = new Thickness(0, 12, 0, 12) });
        meters.Children.Add(new Sparkline { Values = new double[] { 10, 25, 18, 42, 34, 65, 50, 78 } });
        meters.Children.Add(new StatusChip { Text = "GPU hotspot hot", Level = StatusLevel.Hot });
        foundation.Children.Add(new GlassCard { Size = CardSize.M, Span = 4, Content = meters });
        foundation.Children.Add(new GlassCard { Size = CardSize.L, Span = 4, Content = new ColorWheel() });
        foundation.Children.Add(new GlassCard { Size = CardSize.L, Span = 8, Content = new HeatGrid { Values = new double?[] { 15, 24, 36, 78, 95, 82, 52, 12, 23, 44, 69, 88, 99, 76, 18, null }, Height = 150, VerticalAlignment = VerticalAlignment.Top } });
        Render(foundation, Path.Combine(output, "Foundation-controls.png"), 1100);
        Render(foundation, Path.Combine(output, "Foundation-controls-narrow.png"), 560);
        var icons = new WrapPanel();
        foreach (var icon in Enum.GetValues<DeviceIconKind>()) icons.Children.Add(new DeviceIcon { Kind = icon, Width = 48, Height = 48, Margin = new Thickness(16) });
        Render(icons, Path.Combine(output, "Foundation-icons.png"), 800);
        var choices = new IconSegmented { ItemsSource = new[] { new IconChoice("own", "Own colour", DeviceIconKind.Lamp), new IconChoice("pc", "Same as PC", DeviceIconKind.Board), new IconChoice("average", "Average", DeviceIconKind.Network) }, SelectedValue = "pc" };
        Render(choices, Path.Combine(output, "Foundation-segmented.png"), 640);
        var setup = new CaseLightingSetup { DataContext = vm.Lighting, IsExpanded = true };
        Render(setup, Path.Combine(output, "Foundation-ARGB-setup.png"), 800);
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
        vm.Fans.SelectFanCommand.Execute(vm.Fans.Fans.First(f => f.Name == "Case fans"));
        RenderPage("Fans-Case", vm.Fans);
        vm.Fans.SelectFanCommand.Execute(vm.Fans.Fans.First(f => f.IsPump));
        RenderPage("Fans-Pump", vm.Fans);
        void RenderPage(string name, object model)
        {
            var template = (DataTemplate)app.Resources[new DataTemplateKey(model.GetType())];
            var view = (FrameworkElement)template.LoadContent(); view.DataContext = model;
            Render(view, Path.Combine(output, name + ".png"), 1020);
        }
        // Round 8: offline mock device setup, import and appliance variants.
        RenderPage("Devices-registry", vm.Devices);
        vm.Devices.RequestImport();
        RenderPage("Devices-import", vm.Devices);
        var deviceTemplate = (DataTemplate)app.Resources[new DataTemplateKey(typeof(DevicesViewModel))];
        var narrowDevices = (FrameworkElement)deviceTemplate.LoadContent(); narrowDevices.DataContext = vm.Devices;
        Render(narrowDevices, Path.Combine(output, "Devices-narrow.png"), 640);
        RenderPage("Appliance-idle", vm.Appliances);
        var mockFryer = client.CurrentState.Appliances![0];
        vm.Appliances.Apply(client.CurrentState with { Appliances = [mockFryer with { Status = "cooking", RemainingMinutes = 12 }] });
        RenderPage("Appliance-cooking", vm.Appliances);
        vm.Appliances.Apply(client.CurrentState with { Appliances = [mockFryer with { Status = "cooking", Connected = false, Warning = "Contact lost while cooking. The fryer's firmware remains in control." }] });
        RenderPage("Appliance-offline", vm.Appliances);
        vm.Appliances.Apply(client.CurrentState);
        // Round 7: discovered/selected second lamp, and a legacy unresolved fixture. No network or DPAPI.
        vm.Lamp.FindCommand.Execute(null);
        vm.Lamp.SelectedLamp = vm.Lamp.Lamps[1];
        RenderPage("Lamp-second-discovered", vm.Lamp);
        var lampTemplate = (DataTemplate)app.Resources[new DataTemplateKey(typeof(LampViewModel))];
        var lampNarrow = (FrameworkElement)lampTemplate.LoadContent(); lampNarrow.DataContext = vm.Lamp;
        Render(lampNarrow, Path.Combine(output, "Lamp-narrow.png"), 640);
        vm.Lamp.Apply(client.CurrentState with { Lamps = [client.CurrentState.Lamps![0] with { Id = "unresolved", DeviceId = null, Connected = false, Error = "Lamp unavailable; add its device ID and token." }] });
        RenderPage("Lamp-unresolved", vm.Lamp);
        vm.Lamp.Apply(client.CurrentState);
        // Round 6: same pure renderer at fixed times, with no audio/screen capture.
        client.SendAsync(Prism.Core.Ipc.Commands.ResizeZone, new { device = "board", zone = 1, size = 30 }).GetAwaiter().GetResult();
        foreach (var effect in Enum.GetValues<Prism.Core.LightingEffect>())
        {
            client.SetLightingAsync(effect, "#FF4D1A", 0.85, 1).GetAwaiter().GetResult();
            var mapPreview = new CaseMapPreview(effect) { DataContext = vm.Lighting, Height = 240, PreviewTime = 1.25 };
            Render(mapPreview, Path.Combine(output, "Effect-" + effect + "-t1.png"), 360);
            mapPreview = new CaseMapPreview(effect) { DataContext = vm.Lighting, Height = 240, PreviewTime = 2.75 };
            Render(mapPreview, Path.Combine(output, "Effect-" + effect + "-t2.png"), 360);
        }
        client.SetLightingAsync(Prism.Core.LightingEffect.Aurora, "#FF4D1A", 0.85, 1).GetAwaiter().GetResult();
        RenderPage("Lighting-Gallery-wide", vm.Lighting);
        var narrowTemplate = (DataTemplate)app.Resources[new DataTemplateKey(typeof(LightingViewModel))];
        var narrowView = (FrameworkElement)narrowTemplate.LoadContent(); narrowView.DataContext = vm.Lighting;
        Render(narrowView, Path.Combine(output, "Lighting-Gallery-narrow.png"), 640);
        var mapEditor = new CaseMapPreview { DataContext = vm.Lighting, Height = 520, MapEditing = true, PreviewTime = 1.25 };
        Render(mapEditor, Path.Combine(output, "Lighting-Map-editor.png"), 640);
        foreach (var device in client.CurrentState.Lighting.Devices.ToArray())
        {
            var colors = Enumerable.Range(0, device.LedCount).Select(i => i % 3 == 0 ? "#FF4D1A" : i % 3 == 1 ? "#5AA7FF" : "#B26BFF").ToArray();
            client.SendAsync(Prism.Core.Ipc.Commands.SetLedColors, new { device = device.Id, layer = "custom", colors }).GetAwaiter().GetResult();
            vm.Lighting.Editor.Device = vm.Lighting.Editor.Devices.First(d => d.Id == device.Id);
            RenderPage("Lighting-LED-" + device.Id, vm.Lighting);
        }
        client.SendAsync(Prism.Core.Ipc.Commands.ResizeZone, new { device = "board", zone = 1, size = 30 }).GetAwaiter().GetResult();
        vm.Lighting.Editor.Device = vm.Lighting.Editor.Devices.First(d => d.Id == "board");
        RenderPage("Lighting-LED-strip30", vm.Lighting);
        client.SendAsync(Prism.Core.Ipc.Commands.SetLedColors, new { device = "board", layer = "overlay",
            colors = Enumerable.Range(0, 32).Select(i => i % 3 == 0 ? "#5AA7FF80" : "#00000000").ToArray() }).GetAwaiter().GetResult();
        vm.Lighting.Editor.Layer = "overlay";
        RenderPage("Lighting-LED-overlay", vm.Lighting);
        var fixtureState = client.CurrentState;
        var matrixFixture = new Prism.Core.Ipc.RgbDeviceState("matrix-fixture", "Matrix fixture", "Keyboard", true, true, 5,
            [new("Grid with an empty cell", "Matrix", 0, 5, 5, 5, ["A", "B", "C", "D", "E"], 3, 2, [0, 1, -1, 2, 3, 4])],
            ["#FF4D1A", "#5AA7FF", "#B26BFF", "#3DDC84", "#F4F4F5"]);
        vm.Lighting.Apply(fixtureState with { Lighting = fixtureState.Lighting with { Devices = [matrixFixture] } });
        RenderPage("Lighting-LED-matrix", vm.Lighting);
        window.Close();
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        app.Shutdown();
        PresentationTraceSources.DataBindingSource.Flush();
        File.WriteAllText(Path.Combine(output,"bindings.log"),errors.ToString());
        Console.WriteLine("Rendered pages, modem modes, widgets and device/appliance variants to " + output);
        if (errors.Length > 0) { Console.Error.WriteLine(errors); return 1; }
        return 0;
    }
    private static void VerifyControls(string output)
    {
        // Real drawing checks: a one-pixel fill must not become a thumb-sized capsule.
        foreach (var (minimum, maximum, value, edge) in new[] {
            (0d, 60d, 33d, 110), (20d, 80d, 35d, 50), (0d, 100d, 0.5d, 1),
            (1d, 100d, 1d, 0), (0d, 100d, 100d, 200), (60d, 60d, 60d, 0) })
        {
            var slider = new ThickSlider { Minimum = minimum, Maximum = maximum, Value = value,
                Glow = false, TrackColor = Colors.Black, AccentColor = Colors.Red };
            slider.Measure(new Size(200, 14)); slider.Arrange(new Rect(0, 0, 200, 14));
            var bitmap = new RenderTargetBitmap(200, 14, 96, 96, PixelFormats.Pbgra32); bitmap.Render(slider);
            var pixels = new byte[200 * 14 * 4]; bitmap.CopyPixels(pixels, 800, 0);
            var last = -1;
            for (var x = 0; x < 200; x++) if (pixels[(7 * 200 + x) * 4 + 2] > 127) last = x;
            if (Math.Abs(last + 1 - edge) > 1) throw new InvalidOperationException($"Slider {minimum}..{maximum} at {value}: edge {last + 1}, expected {edge}");
        }
        var curve = new FanCurveEditor(); curve.Measure(new Size(400, 200)); curve.Arrange(new Rect(0, 0, 400, 200));
        var drawing = VisualTreeHelper.GetDrawing(curve);
        var paths = drawing.Children.OfType<GeometryDrawing>().Where(d => d.Geometry is StreamGeometry).ToArray();
        if (paths.Length != 3 || paths.Any(p => p.Geometry.ToString().Contains('C')))
            throw new InvalidOperationException("Curve fill, glow and stroke must all use straight segments.");
        var panel = new StackPanel();
        var combo = new ComboBox { ItemsSource = new[] { "CPU", "Motherboard", "GPU", "Highest" }, SelectedIndex = 0, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(combo);
        panel.Children.Add(new TextBox { Text = "Graphite text selection", Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(new PasswordBox { Password = "preview only", Background = (Brush)Application.Current.Resources["PanelAltBrush"], Foreground = Brushes.White, Padding = new Thickness(10) });
        panel.Children.Add(new Button { Content = "Implicit button", Margin = new Thickness(0, 12, 0, 12) });
        var scroll = new ScrollViewer { Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = new TextBlock { Text = string.Join("\n", Enumerable.Repeat("Scroll preview", 20)) } };
        panel.Children.Add(scroll);
        panel.Children.Add(new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, Value = 35, ViewportSize = 20, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, Value = 35, ViewportSize = 20, Height = 80, HorizontalAlignment = HorizontalAlignment.Right });
        Render(panel, Path.Combine(output, "Controls.png"), 500);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        Render((FrameworkElement)popup.Child, Path.Combine(output, "ComboPopup.png"), 300);
        var tooltip = new ToolTip { Content = "Graphite tooltip" };
        Render(tooltip, Path.Combine(output, "Tooltip.png"), 300);
        var menu = new ContextMenu(); menu.Items.Add(new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" }); menu.Items.Add(new MenuItem { Header = "Paste", IsEnabled = false });
        Render(menu, Path.Combine(output, "ContextMenu.png"), 300);
        using var tray = new System.Windows.Forms.ContextMenuStrip();
        tray.Items.Add("Open");
        var sub = new System.Windows.Forms.ToolStripMenuItem("Scenes"); sub.DropDownItems.Add("Gaming"); tray.Items.Add(sub);
        GraphiteTrayTheme.Apply(tray);
        tray.PerformLayout();
        using var image = new System.Drawing.Bitmap(tray.Width, tray.Height);
        tray.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, image.Width, image.Height));
        image.Save(Path.Combine(output, "TrayMenu.png"));
        using var submenu = new System.Drawing.Bitmap(sub.DropDown.Width, sub.DropDown.Height);
        sub.DropDown.DrawToBitmap(submenu, new System.Drawing.Rectangle(0, 0, submenu.Width, submenu.Height));
        submenu.Save(Path.Combine(output, "TraySubmenu.png"));
        Console.WriteLine("Verified linear curve geometry and six slider fill cases; rendered detached controls and menus.");
    }
    private static void SeedSensorBindings(MockPrismClient client)
    {
        // The owner's layout: header 1 carries the pump and radiator fans (48 LEDs).
        client.SendAsync(Prism.Core.Ipc.Commands.ResizeZone, new { device = "board", zone = 1, size = 48 }).GetAwaiter().GetResult();
        client.SendAsync(Prism.Core.Ipc.Commands.SetSensorBinding, new { target = "ram", source = "gpu.temperature", min = 35, max = 85, smoothing = 0.5 }).GetAwaiter().GetResult();
        client.SendAsync(Prism.Core.Ipc.Commands.SetSensorBinding, new { target = "board/zone/1", source = "cpu.temperature", min = 30, max = 95, style = "FillBar", alert = new { threshold = 90 } }).GetAwaiter().GetResult();
    }
    private static void SelectLightingMode(FrameworkElement shell, int mode)
    {
        shell.Measure(new Size(1440, 1200)); shell.Arrange(new Rect(0, 0, 1440, 1200)); shell.UpdateLayout();
        var workspace = Descendants(shell).OfType<LightingWorkspace>().FirstOrDefault() ?? throw new InvalidOperationException("Lighting page not found");
        Descendants(workspace).OfType<SegmentedControl>().First(s => s.Items.Contains("Sensor")).SelectedIndex = mode;
    }
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(LogicalDescendants).Prepend(root);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Render(FrameworkElement view, string path, double width)
    {
        FrameworkElement frame = view;
        if (view.Parent is null && view is not ToolTip && view is not ContextMenu)
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
        if (frame is Border parent && ReferenceEquals(parent.Child, view)) parent.Child = null;
    }
}
