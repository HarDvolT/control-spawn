using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Prism.App.ViewModels;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;
using System.Diagnostics;
using System.Windows.Threading;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>The v2 whole-case illustration. Editing still uses the spatial canvas.</summary>
public sealed class CaseIllustration : FrameworkElement
{
    private LightingViewModel? model;
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SensorLighting sensorLighting = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1d / 20) };
    public CaseIllustration()
    {
        Height = 400;
        timer.Tick += (_, _) => { if (!Motion.ReduceAnimations) InvalidateVisual(); };
        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = DataContext as LightingViewModel; if (model is not null) model.PropertyChanged += Changed; InvalidateVisual(); };
        Motion.WhileVisible(this, timer.Start, timer.Stop);
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; };
        Loaded += (_, _) => { if (model is not null) { model.PropertyChanged -= Changed; model.PropertyChanged += Changed; } };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
    private static Brush B(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth / 640, ActualHeight / 400));
        dc.DrawRoundedRectangle(B("#111216"), new Pen(B("#2A2C33"), 1.5), new Rect(18, 20, 492, 360), 22, 22);
        dc.DrawRoundedRectangle(null, new Pen(B("#1E2026"), 1), new Rect(31, 33, 466, 334), 16, 16);
        Label("RADIATOR", 41, 45, 10);
        dc.DrawRoundedRectangle(B("#101115"), new Pen(B("#1E2026"), 1), new Rect(122, 132, 255, 142), 12, 12);
        dc.DrawRoundedRectangle(B("#121318"), new Pen(B("#1E2026"), 1), new Rect(112, 291, 284, 32), 8, 8);
        var devices = model?.State?.Lighting.Devices ?? [];
        var rendered = new Dictionary<string, RgbColor[]>();
        double time = Motion.ReduceAnimations ? 1.25 : clock.Elapsed.TotalSeconds;
        var ram = devices.Where(d => d.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase)).ToArray();
        var gpu = devices.FirstOrDefault(d => d.Kind.Contains("GPU", StringComparison.OrdinalIgnoreCase));
        var board = devices.FirstOrDefault(d => d.Kind.Contains("Motherboard", StringComparison.OrdinalIgnoreCase));
        var argb = devices.FirstOrDefault(d => d.Kind.Contains("strip", StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < 3; i++) Ring(179 + i * 76, 86, 27, argb);
        Ring(72, 151, 29, argb);
        for (int i = 0; i < 3; i++) Ring(445, 143 + i * 82, 33, argb);
        for (int i = 0; i < ram.Length && i < 2; i++)
            for (int j = 0; j < ram[i].LedCount; j++) Led(new Rect(283 + i * 19, 141 + j * 10, 10, 9), ram[i], j);
        if (gpu is not null) for (int i = 0; i < gpu.LedCount; i++) Led(new Rect(124 + i * 25, 303, 21, 7), gpu, i);
        if (board is not null)
        {
            dc.PushTransform(new TranslateTransform(184, 168));
            dc.DrawGeometry(Colour(board, 0), null, Geometry.Parse("M0,0 L20,-8 12,12Z")); dc.Pop();
            Label(board.Name, 141, 255, 9);
        }
        Label(gpu?.Name ?? "GRAPHICS", 282, 300, 9);
        for (int i = 0; i < 12; i++)
        {
            var color = argb is null ? B("#252731") : Colour(argb, Math.Min(i, argb.LedCount - 1));
            dc.DrawEllipse(B("#17181D"), null, new Point(56 + i * 32, 351), 9, 9);
            dc.DrawEllipse(color, null, new Point(56 + i * 32, 351), 4, 4);
        }
        Label("LED STRIP \u00B7 FRONT FANS \u00B7 REAR FAN (ARGB)", 52, 369, 9);
        int lampIndex = 0;
        foreach (var lamp in model?.State?.Lamps ?? [])
        {
            if (lampIndex >= 2) break;
            double y = 139 + lampIndex++ * 100;
            var color = (Color)ColorConverter.ConvertFromString(lamp.Connected && lamp.On ? lamp.Color : "#24262D");
            var glow = new RadialGradientBrush(Color.FromArgb(110, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B));
            dc.DrawEllipse(glow, null, new Point(589, y), 52, 52);
            var orb = new RadialGradientBrush { GradientOrigin = new(.3, .2), Center = new(.4, .35), RadiusX = .75, RadiusY = .75 };
            orb.GradientStops.Add(new(Colors.White, 0)); orb.GradientStops.Add(new(color, .3)); orb.GradientStops.Add(new(Color.FromRgb((byte)(color.R * .3), (byte)(color.G * .3), (byte)(color.B * .3)), 1));
            dc.DrawEllipse(orb, null, new Point(589, y), 28, 28);
            Label(lamp.Name ?? "Lamp", 550, y + 39, 12);
        }
        dc.Pop();
        void Label(string text, double x, double y, double size)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typefaces.Body(FontWeights.Normal), size, B("#7C808A"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(x, y));
        }
        void Ring(double x, double y, double r, RgbDeviceState? device)
        {
            dc.DrawEllipse(null, new Pen(B("#16171D"), 15), new Point(x, y), r, r);
            dc.DrawEllipse(null, new Pen(device is null ? B("#24262F") : Colour(device, 0), 6), new Point(x, y), r, r);
        }
        void Led(Rect rect, RgbDeviceState device, int index) => dc.DrawRoundedRectangle(Colour(device, index), null, rect, 3, 3);
        Brush Colour(RgbDeviceState device, int index)
        {
            if (model?.State is not { } state || !device.Connected || device.LedCount == 0 || !device.Synced && !device.CustomEnabled) return B("#252731");
            if (rendered.TryGetValue(device.Id, out var cached))
            { var known = cached[Math.Clamp(index, 0, cached.Length - 1)]; return new SolidColorBrush(Color.FromRgb(known.R, known.G, known.B)); }
            var scene = new Scene("Preview", state.Lighting.Effect, Palettes.Parse(state.Lighting.Color), state.Lighting.Speed, state.Lighting.Brightness);
            var positions = CaseMap.Positions(device, CaseMap.Placement(model.Map, device, devices), model.Map);
            var input = state.Lighting.Inputs;
            var colors = SpatialEffects.Render(scene, model.Parameters, time, positions, device.Id, device.Kind,
                new ReactiveFrame(state.System.CpuLoad ?? 0, state.System.GpuLoad ?? 0,
                    state.System.RamTotalGb > 0 ? (state.System.RamUsedGb ?? 0) / state.System.RamTotalGb.Value : 0,
                    state.System.CpuTemp ?? double.NaN, state.System.GpuTemp ?? double.NaN, input?.AudioBands ?? [], input?.Beat ?? 0,
                    input is null ? RgbColor.Black : Palettes.Parse(input.ScreenLeft), input is null ? RgbColor.Black : Palettes.Parse(input.ScreenCentre), input is null ? RgbColor.Black : Palettes.Parse(input.ScreenRight)));
            colors = LedLayers.Composite(colors, device.CustomColors, device.OverlayColors, device.CustomEnabled).ToArray();
            colors = sensorLighting.Composite(device, state.Lighting.Bindings, state, time, colors, scene.Brightness).ToArray();
            if (colors.Length == 0) return B("#252731");
            rendered[device.Id] = colors;
            var c = colors[Math.Clamp(index, 0, colors.Length - 1)]; return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        }
    }
}
