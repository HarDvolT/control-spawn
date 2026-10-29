using Prism.App.Widgets;

namespace Prism.App.Tests;

public class WidgetLayoutTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "prism-app-tests", Guid.NewGuid().ToString("N"), "widgets.json");

    [Fact]
    public void Layout_round_trips_through_the_store()
    {
        var path = TempFile();
        try
        {
            var store = new WidgetLayoutStore(path);
            var file = new WidgetLayoutFile
            {
                Widgets =
                {
                    new WidgetLayout
                    {
                        Id = "system", Kind = WidgetKind.SystemMonitor, Size = WidgetSize.Large,
                        X = 1216, Y = 48, Opacity = 0.55, AlwaysOnTop = true, ClickThrough = true, Enabled = false, Background = "Clear",
                        Locked = true, SnapToGrid = false, Monitor = @"\\.\DISPLAY2",
                        Metrics = { WidgetMetrics.Cpu, WidgetMetrics.Gpu, WidgetMetrics.Net }
                    },
                    new WidgetLayout
                    {
                        Id = "lamp", Kind = WidgetKind.Lamp, Size = WidgetSize.Small,
                        X = 400, Y = 500, Opacity = 0.9, Monitor = @"\\.\DISPLAY1"
                    }
                }
            };

            store.Save(file);
            var loaded = store.Load();

            Assert.Equal(2, loaded.Widgets.Count);
            var system = loaded.Widgets[0];
            Assert.Equal("system", system.Id);
            Assert.Equal(WidgetKind.SystemMonitor, system.Kind);
            Assert.Equal(WidgetSize.Large, system.Size);
            Assert.Equal(1216, system.X);
            Assert.Equal(48, system.Y);
            Assert.Equal(0.55, system.Opacity, 3);
            Assert.True(system.AlwaysOnTop);
            Assert.True(system.ClickThrough);
            Assert.True(system.Locked);
            Assert.False(system.SnapToGrid);
            Assert.False(system.Enabled);
            Assert.Equal("Clear", system.Background);
            Assert.False(system.Clone().Enabled);
            Assert.Equal("Clear", system.Clone().Background);
            Assert.Equal(@"\\.\DISPLAY2", system.Monitor);
            Assert.Equal(new[] { WidgetMetrics.Cpu, WidgetMetrics.Gpu, WidgetMetrics.Net }, system.Metrics);

            Assert.Equal(WidgetKind.Lamp, loaded.Widgets[1].Kind);
            Assert.Equal(WidgetSize.Small, loaded.Widgets[1].Size);
        }
        finally
        {
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_an_empty_layout()
    {
        var loaded = new WidgetLayoutStore(TempFile()).Load();
        Assert.Empty(loaded.Widgets);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public void Corrupt_file_gives_an_empty_layout_instead_of_throwing()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json ");
            Assert.Empty(new WidgetLayoutStore(path).Load().Widgets);
        }
        finally
        {
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Opacity_is_clamped_on_load()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"version\":1,\"widgets\":[{\"id\":\"a\",\"kind\":\"Cpu\",\"opacity\":0.01}]}");
            var loaded = new WidgetLayoutStore(path).Load();
            Assert.Equal(0.2, loaded.Widgets[0].Opacity, 3);
        }
        finally
        {
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Defaults_fit_inside_the_work_area()
    {
        var area = new PixelRect(0, 0, 1920, 1040);
        var layout = WidgetDefaults.Create(WidgetKind.Fans, area, 3);
        var width = WidgetSizes.Width(layout.Size);
        var height = WidgetSizes.Height(layout.Kind, layout.Size);

        Assert.InRange(layout.X, area.X, area.X + area.Width - width);
        Assert.InRange(layout.Y, area.Y, area.Y + area.Height - height);
        Assert.Equal(0, layout.X % WidgetGrid.Size, 3);
        Assert.Equal(0, layout.Y % WidgetGrid.Size, 3);
    }

    [Fact]
    public void Sizes_grow_from_small_to_large()
    {
        Assert.True(WidgetSizes.Width(WidgetSize.Small) < WidgetSizes.Width(WidgetSize.Medium));
        Assert.True(WidgetSizes.Width(WidgetSize.Medium) < WidgetSizes.Width(WidgetSize.Large));
        Assert.True(WidgetSizes.Height(WidgetKind.SystemMonitor, WidgetSize.Small) <
                    WidgetSizes.Height(WidgetKind.SystemMonitor, WidgetSize.Large));
    }
}

public class SnapTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 8)]
    [InlineData(5, 8)]
    [InlineData(12, 16)]
    [InlineData(-5, -8)]
    [InlineData(-12, -16)]
    [InlineData(1216, 1216)]
    public void Snap_rounds_to_the_eight_pixel_grid(double input, double expected) =>
        Assert.Equal(expected, WidgetGrid.Snap(input));

    [Fact]
    public void SnapPoint_snaps_both_axes()
    {
        var (x, y) = WidgetGrid.SnapPoint(101, 207);
        Assert.Equal(104, x);
        Assert.Equal(208, y);
    }

    [Fact]
    public void Clamp_keeps_a_widget_on_screen()
    {
        var area = new PixelRect(0, 0, 1920, 1080);
        var (x, y) = WidgetGrid.Clamp(5000, -20, 250, 200, area);
        Assert.Equal(1672, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void Apply_only_snaps_when_snap_is_on()
    {
        var layout = new WidgetLayout { X = 101, Y = 205, SnapToGrid = true };
        WidgetGrid.Apply(layout);
        Assert.Equal(104, layout.X);
        Assert.Equal(208, layout.Y);

        var free = new WidgetLayout { X = 101, Y = 205, SnapToGrid = false };
        WidgetGrid.Apply(free);
        Assert.Equal(101, free.X);
        Assert.Equal(205, free.Y);
    }
}
