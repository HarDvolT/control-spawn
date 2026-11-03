using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Prism.App.Common;
using Prism.Core;

namespace Prism.App.Controls;

/// <summary>
/// A looping 24 s demo of the chosen smoothing: a CPU temperature with one-second spikes and one real heat-up, the
/// temperature the fan actually follows (averaged, held), and the fan speed with its ramp limits, plus a fan that spins
/// at that speed. Same maths as the service's curve engine, so "Relaxed" here behaves like Relaxed on the real fans.
/// </summary>
public sealed class SmoothingPreview : FrameworkElement
{
    public static readonly DependencyProperty AveragingProperty = Register(nameof(Averaging), 4);
    public static readonly DependencyProperty HoldProperty = Register(nameof(Hold), 2);
    public static readonly DependencyProperty UpPerSecondProperty = Register(nameof(UpPerSecond), 30);
    public static readonly DependencyProperty DownPerSecondProperty = Register(nameof(DownPerSecond), 10);
    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(name, typeof(double), typeof(SmoothingPreview),
        new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SmoothingPreview)d).simulated = null));
    public double Averaging { get => (double)GetValue(AveragingProperty); set => SetValue(AveragingProperty, value); }
    public double Hold { get => (double)GetValue(HoldProperty); set => SetValue(HoldProperty, value); }
    public double UpPerSecond { get => (double)GetValue(UpPerSecondProperty); set => SetValue(UpPerSecondProperty, value); }
    public double DownPerSecond { get => (double)GetValue(DownPerSecondProperty); set => SetValue(DownPerSecondProperty, value); }

    private const double Length = 24, Step = 0.1, TempLow = 40, TempHigh = 72, Rotor = 46;
    private static readonly FanPoint[] Curve = [new(30, 26), new(50, 45), new(55, 52), new(60, 60), new(67, 80), new(70, 100), new(80, 100), new(100, 100)];
    private (double Raw, double Followed, double Duty)[]? simulated;
    // Starts mid-heat-up so a still render (and the preview tool) shows the interesting part.
    private double playhead = 13, angle;
    private TimeSpan? lastFrame;

    public SmoothingPreview()
    {
        MinHeight = 176;
        // Merely listening to CompositionTarget.Rendering keeps WPF redrawing the whole window every frame, so listen only
        // while this is on screen: a Fans page left open behind the tray kept the app rendering at 60 fps.
        Motion.WhileVisible(this, StartFrames, StopFrames);
    }
    private bool listening;
    /// <summary>Whether the demo is animating; with reduced motion it holds its mid-heat-up frame.</summary>
    public bool IsAnimating => listening;
    private void StartFrames()
    {
        if (listening || Motion.ReduceAnimations) return;
        CompositionTarget.Rendering += OnFrame; listening = true;
    }
    private void StopFrames()
    {
        if (listening) { CompositionTarget.Rendering -= OnFrame; listening = false; }
        lastFrame = null;
    }

    /// <summary>CPU temperature over the loop: idle with three 1 s spikes, then real load for 6 s, then idle with small wobbles.</summary>
    public static double Temperature(double t) => t switch
    {
        >= 2 and < 3 or >= 5 and < 6 or >= 8 and < 9 => 60,
        >= 11 and < 13 => 46 + (t - 11) * 11,
        >= 13 and < 18 => 68,
        >= 18 and < 19 => 68 - (t - 18) * 22,
        >= 20 => 46 + 2.5 * Math.Sin((t - 20) * 3),
        _ => 46
    };

    private (double Raw, double Followed, double Duty)[] Simulate()
    {
        var samples = new (double, double, double)[(int)(Length / Step) + 1];
        // Run the loop twice and keep the second pass, so the start already carries on from the end.
        double averaged = 46, accepted = 46, duty = Duty(46);
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < samples.Length; i++)
            {
                double raw = Temperature(i * Step);
                averaged = Averaging > 0 ? averaged + (raw - averaged) * (1 - Math.Exp(-Step / Averaging)) : raw;
                if (Math.Abs(averaged - accepted) >= Hold) accepted = averaged;
                double target = Duty(accepted);
                duty = target > duty ? Math.Min(target, duty + UpPerSecond * Step) : Math.Max(target, duty - DownPerSecond * Step);
                samples[i] = (raw, accepted, duty);
            }
        return samples;
    }
    private static double Duty(double t) => ViewModels.FansViewModel.DutyAt(Curve, t);

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible || e is not RenderingEventArgs args) return;
        // 30 fps is smooth enough for a demo and halves the redraws of the 60 Hz render loop.
        if (lastFrame is { } last && (args.RenderingTime - last).TotalMilliseconds < 30) return;
        if (lastFrame is { } previous)
        {
            double dt = Math.Clamp((args.RenderingTime - previous).TotalSeconds, 0, 0.1);
            playhead = (playhead + dt) % Length;
            angle = (angle + dt * (Sample(playhead).Duty * 9)) % 360;
        }
        lastFrame = args.RenderingTime;
        InvalidateVisual();
    }

    private (double Raw, double Followed, double Duty) Sample(double t)
    {
        simulated ??= Simulate();
        return simulated[Math.Clamp((int)(t / Step), 0, simulated.Length - 1)];
    }

    protected override void OnRender(DrawingContext dc)
    {
        simulated ??= Simulate();
        double width = ActualWidth - Rotor - 14, height = ActualHeight;
        if (width < 60 || height < 60) return;
        var muted = Color.FromRgb(124, 128, 138);
        var cyan = Color.FromRgb(54, 214, 255);
        var accent = Color.FromRgb(255, 77, 26);
        // Two strips: temperature on top, fan speed below, one time axis.
        var top = new Rect(0, 14, width, height * 0.52 - 14);
        var bottom = new Rect(0, top.Bottom + 18, width, height - top.Bottom - 22);
        foreach (var strip in new[] { top, bottom }) dc.DrawRoundedRectangle(ColorUtil.Brush(Color.FromRgb(13, 14, 18)), null, strip, 6, 6);

        double X(double t) => t / Length * width;
        Point Temp(int i, double v) => new(X(i * Step), top.Bottom - 3 - (Math.Clamp(v, TempLow, TempHigh) - TempLow) / (TempHigh - TempLow) * (top.Height - 6));
        Point Speed(int i, double v) => new(X(i * Step), bottom.Bottom - 3 - v / 100 * (bottom.Height - 6));
        void Line(Func<int, Point> at, Pen pen)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(at(0), false, false);
                for (int i = 1; i < simulated!.Length; i++) g.LineTo(at(i), true, false);
            }
            geometry.Freeze(); dc.DrawGeometry(null, pen, geometry);
        }
        Line(i => Temp(i, simulated[i].Raw), new Pen(ColorUtil.Brush(Color.FromArgb(170, muted.R, muted.G, muted.B)), 1) { DashStyle = new DashStyle([2, 2], 0) });
        Line(i => Temp(i, simulated[i].Followed), new Pen(ColorUtil.Brush(cyan), 2));
        Line(i => Speed(i, simulated[i].Duty), new Pen(ColorUtil.Brush(accent), 2));

        var labels = new[] { ("CPU", muted), ("what the fan follows", cyan) };
        double lx = 0;
        foreach (var (text, color) in labels)
        {
            var label = Text(text, color);
            dc.DrawText(label, new Point(lx, 0)); lx += label.Width + 12;
        }
        dc.DrawText(Text("fan speed", accent), new Point(0, top.Bottom + 3));
        dc.DrawText(Text("spikes", muted), new Point(X(2), top.Top + 2));
        var load = Text("real load", muted);
        dc.DrawText(load, new Point(X(15.5) - load.Width / 2, top.Bottom - load.Height - 2));

        // Playhead and the fan spinning at the speed under it.
        var now = Sample(playhead);
        var head = new Pen(ColorUtil.Brush(Color.FromArgb(120, 255, 255, 255)), 1) { DashStyle = DashStyles.Dash };
        dc.DrawLine(head, new Point(X(playhead), top.Top), new Point(X(playhead), bottom.Bottom));
        var centre = new Point(ActualWidth - Rotor / 2 - 2, top.Bottom + 9);
        dc.DrawEllipse(null, new Pen(ColorUtil.Brush(Color.FromArgb(60, accent.R, accent.G, accent.B)), 1.5), centre, Rotor / 2, Rotor / 2);
        dc.PushTransform(new RotateTransform(angle, centre.X, centre.Y));
        for (int blade = 0; blade < 5; blade++)
        {
            dc.PushTransform(new RotateTransform(blade * 72, centre.X, centre.Y));
            dc.DrawEllipse(ColorUtil.Brush(accent), null, new Point(centre.X, centre.Y - Rotor * 0.25), Rotor * 0.09, Rotor * 0.2);
            dc.Pop();
        }
        dc.Pop();
        dc.DrawEllipse(ColorUtil.Brush(Color.FromRgb(23, 24, 29)), null, centre, Rotor * 0.1, Rotor * 0.1);
        var readout = Text($"{now.Duty:0}%", Color.FromRgb(240, 240, 244), 12, true);
        dc.DrawText(readout, new Point(centre.X - readout.Width / 2, centre.Y + Rotor / 2 + 4));
        var temp = Text($"{now.Raw:0}°", muted);
        dc.DrawText(temp, new Point(centre.X - temp.Width / 2, centre.Y - Rotor / 2 - temp.Height - 3));
    }

    private FormattedText Text(string text, Color color, double size = 10, bool bold = false) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
        size, ColorUtil.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
