using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prism.App.Widgets;

public enum WidgetKind
{
    SystemMonitor,
    Cpu,
    Gpu,
    Fans,
    Lighting,
    Lamp,
    Modem,
    Ping,
    Network,
    Scenes,
    AirFryer
}

public enum WidgetSize { Small, Medium, Large }

public sealed class WidgetLayout
{
    public bool Enabled { get; set; } = true;
    public string Background { get; set; } = "Glass";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public WidgetKind Kind { get; set; } = WidgetKind.SystemMonitor;
    public WidgetSize Size { get; set; } = WidgetSize.Medium;
    public double X { get; set; }
    public double Y { get; set; }
    public double Opacity { get; set; } = 0.72;
    public bool AlwaysOnTop { get; set; }
    public bool ClickThrough { get; set; }
    public bool Locked { get; set; }
    public bool SnapToGrid { get; set; } = true;
    public List<string> Metrics { get; set; } = new();
    public string? Monitor { get; set; }

    public WidgetLayout Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Background = Background,
        Kind = Kind,
        Size = Size,
        X = X,
        Y = Y,
        Opacity = Opacity,
        AlwaysOnTop = AlwaysOnTop,
        ClickThrough = ClickThrough,
        Locked = Locked,
        SnapToGrid = SnapToGrid,
        Metrics = new List<string>(Metrics),
        Monitor = Monitor
    };
}

public sealed class WidgetLayoutFile
{
    public int Version { get; set; } = 1;
    public List<WidgetLayout> Widgets { get; set; } = new();
}

public static class WidgetMetrics
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Temps = "temps";
    public const string Ram = "ram";
    public const string Net = "net";

    public static IReadOnlyList<(string Key, string Name)> All { get; } =
    [
        (Cpu, "CPU usage"),
        (Gpu, "GPU usage"),
        (Temps, "Temperatures"),
        (Ram, "Memory"),
        (Net, "Network speed")
    ];

    public static List<string> Defaults(WidgetKind kind) => kind switch
    {
        WidgetKind.SystemMonitor => [Cpu, Gpu, Temps],
        WidgetKind.Cpu => [Cpu, Temps],
        WidgetKind.Gpu => [Gpu, Temps],
        WidgetKind.Network => [Net],
        _ => new List<string>()
    };
}

public static class WidgetSizes
{
    public static double Width(WidgetSize size) => size switch
    {
        WidgetSize.Small => 210,
        WidgetSize.Medium => 250,
        WidgetSize.Large => 300,
        _ => 250
    };

    public static double Height(WidgetKind kind, WidgetSize size)
    {
        var scale = size switch { WidgetSize.Small => 0.9, WidgetSize.Large => 1.15, _ => 1.0 };
        var baseHeight = kind switch
        {
            WidgetKind.SystemMonitor => 205,
            WidgetKind.Cpu => 150,
            WidgetKind.Gpu => 150,
            WidgetKind.Fans => 185,
            WidgetKind.Lighting => 160,
            WidgetKind.Lamp => 150,
            WidgetKind.Modem => 150,
            WidgetKind.Ping => 150,
            WidgetKind.Network => 140,
            WidgetKind.Scenes => 170,
            _ => 160
        };
        return Math.Round(baseHeight * scale);
    }

    public static string Name(WidgetSize size) => size switch
    {
        WidgetSize.Small => "Small",
        WidgetSize.Medium => "Medium",
        WidgetSize.Large => "Large",
        _ => size.ToString()
    };
}

public static class WidgetNames
{
    public static string Name(WidgetKind kind) => kind switch
    {
        WidgetKind.SystemMonitor => "System monitor",
        WidgetKind.Cpu => "CPU",
        WidgetKind.Gpu => "GPU",
        WidgetKind.Fans => "Fans",
        WidgetKind.Lighting => "Lighting",
        WidgetKind.Lamp => "Lamp",
        WidgetKind.Modem => "Modem",
        WidgetKind.Ping => "Ping",
        WidgetKind.Network => "Network",
        WidgetKind.Scenes => "Scenes",
        WidgetKind.AirFryer => "Air fryer",
        _ => kind.ToString()
    };

    public static string Subtitle(WidgetKind kind) => kind switch
    {
        WidgetKind.SystemMonitor => "CPU, GPU, temps",
        WidgetKind.Cpu => "Load, clock, temp",
        WidgetKind.Gpu => "Load, temp, VRAM",
        WidgetKind.Fans => "Modes + RPM",
        WidgetKind.Lighting => "Colour + effect",
        WidgetKind.Lamp => "On/off + brightness",
        WidgetKind.Modem => "Signal + ping",
        WidgetKind.Ping => "Live latency graph",
        WidgetKind.Network => "Down / up speed",
        WidgetKind.Scenes => "One-tap switcher",
        WidgetKind.AirFryer => "Status and one-click cancel",
        _ => string.Empty
    };
}

/// <summary>8 px grid maths shared by the desktop windows and the preview in the Widgets page.</summary>
public static class WidgetGrid
{
    public const int Size = 8;

    public static double Snap(double value) =>
        !double.IsFinite(value) ? 0 : Math.Round(value / Size, MidpointRounding.AwayFromZero) * Size;

    public static (double X, double Y) SnapPoint(double x, double y) => (Snap(x), Snap(y));

    public static (double X, double Y) Clamp(double x, double y, double width, double height, PixelRect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return (Snap(x), Snap(y));
        var maxX = Math.Max(area.X, area.X + area.Width - width);
        var maxY = Math.Max(area.Y, area.Y + area.Height - height);
        return (Snap(Math.Clamp(x, area.X, maxX)), Snap(Math.Clamp(y, area.Y, maxY)));
    }

    public static WidgetLayout Apply(WidgetLayout layout)
    {
        if (!layout.SnapToGrid) return layout;
        var (x, y) = SnapPoint(layout.X, layout.Y);
        layout.X = x;
        layout.Y = y;
        return layout;
    }
}

public readonly record struct PixelRect(double X, double Y, double Width, double Height);

public static class WidgetDefaults
{
    /// <summary>Sensible first-run placement: a tidy column on the right edge of the primary work area.</summary>
    public static WidgetLayout Create(WidgetKind kind, PixelRect workArea, int index = 0)
    {
        var width = WidgetSizes.Width(WidgetSize.Medium);
        var height = WidgetSizes.Height(kind, WidgetSize.Medium);
        // Fixed-height slots avoid overlap when adjacent widgets have different heights.
        const double slot = 232;
        var rows = Math.Max(1,(int)((workArea.Height - 64) / slot));
        var x = workArea.X + workArea.Width - width - 32 - (index / rows) * (width + 16);
        var y = workArea.Y + 32 + (index % rows) * slot;
        if (y + height > workArea.Y + workArea.Height)
            y = workArea.Y + Math.Max(32, workArea.Height - height - 32);
        return new WidgetLayout
        {
            Kind = kind,
            Size = WidgetSize.Medium,
            X = WidgetGrid.Snap(x),
            Y = WidgetGrid.Snap(y),
            Metrics = WidgetMetrics.Defaults(kind)
        };
    }

    /// <summary>Placement used by the preview when a layout has no real screen position yet.</summary>
    public static (double X, double Y) PreviewSlot(WidgetKind kind) => kind switch
    {
        WidgetKind.SystemMonitor => (24, 30),
        WidgetKind.Fans => (24, 300),
        WidgetKind.Lighting => (420, 34),
        WidgetKind.Modem => (760, 34),
        WidgetKind.Lamp => (420, 330),
        WidgetKind.Cpu => (420, 34),
        WidgetKind.Gpu => (760, 34),
        WidgetKind.Ping => (420, 330),
        WidgetKind.Network => (760, 330),
        WidgetKind.Scenes => (24, 300),
        _ => (24, 30)
    };
}

public sealed class WidgetLayoutStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WidgetLayoutStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? DefaultPath : filePath!;
    }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Prism.Core.ProductNames.DataFolder, "widgets.json");

    public string FilePath { get; }

    /// <summary>The file, else its last good copy (a crash can leave the main file empty), else no widgets.</summary>
    public WidgetLayoutFile Load() => !File.Exists(FilePath) ? new WidgetLayoutFile() : Read(FilePath) ?? Read(Prism.Core.AtomicFile.Backup(FilePath)) ?? new WidgetLayoutFile();
    private static WidgetLayoutFile? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var file = JsonSerializer.Deserialize<WidgetLayoutFile>(json, Options);
            if (file is null) return null;
            file.Widgets ??= new List<WidgetLayout>();
            file.Widgets = file.Widgets.Where(w => w is not null).ToList();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var widget in file.Widgets)
            {
                widget.Id = string.IsNullOrWhiteSpace(widget.Id) ? Guid.NewGuid().ToString("N") : widget.Id;
                if (!ids.Add(widget.Id)) { widget.Id = Guid.NewGuid().ToString("N"); ids.Add(widget.Id); }
                if (!Enum.IsDefined(widget.Kind)) widget.Kind = WidgetKind.SystemMonitor;
                if (!Enum.IsDefined(widget.Size)) widget.Size = WidgetSize.Medium;
                if (!double.IsFinite(widget.X)) widget.X = 0;
                if (!double.IsFinite(widget.Y)) widget.Y = 0;
                if (widget.Background is not ("Glass" or "Solid" or "Clear")) widget.Background = "Glass";
                widget.Metrics ??= new List<string>();
                widget.Opacity = double.IsFinite(widget.Opacity) ? Math.Clamp(widget.Opacity, 0.2, 1.0) : 0.72;
            }
            return file;
        }
        catch
        {
            // A broken layout file must never stop Prism from starting.
            return null;
        }
    }

    public void Save(WidgetLayoutFile file)
    {
        Prism.Core.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(file, Options));
    }
}
