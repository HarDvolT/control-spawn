using System.Collections.ObjectModel;
using Prism.App.Services;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

public sealed class LedEditorViewModel(IPrismClient client, LedPresetStore? presets = null) : ObservableObject
{
    private readonly LedPresetStore presets = presets ?? new();
    private readonly Dictionary<string, string[]> drafts = new();
    private readonly Dictionary<string, int> versions = new();
    private readonly Dictionary<string, string> modes = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private RgbDeviceState? device;
    private string layer = "custom", status = "Brush across LEDs to paint. Shift-click selects a range.";
    public ObservableCollection<RgbDeviceState> Devices { get; } = [];
    public ObservableCollection<string> Recent { get; } = ["#FF4D1A", "#5AA7FF", "#B26BFF", "#3DDC84", "#F4F4F5"];
    public RgbDeviceState? Device
    {
        get => device;
        set
        {
            if (Equals(device, value)) return;
            device = value;
            layer = value is null ? "custom" : modes.GetValueOrDefault(value.Id) ?? (value.CustomColors is null && value.OverlayColors is not null ? "overlay" : "custom");
            OnPropertyChanged(nameof(Layer)); OnPropertyChanged(nameof(Device)); OnPropertyChanged(nameof(Colors));
        }
    }
    public string Layer
    {
        get => layer;
        set
        {
            if (!Set(ref layer, value)) return;
            if (Device is not null) modes[Device.Id] = value;
            OnPropertyChanged(nameof(Colors));
        }
    }
    public void ActivateLayer(string target)
    {
        if (Layer == target) return;
        Layer = target; Queue(Colors);
    }
    public string Status { get => status; private set => Set(ref status, value); }
    private string brushColor = "#FF4D1A";
    public string Color { get => brushColor; set => Set(ref brushColor, value); }
    public string SecondColor { get; set; } = "#5AA7FF";
    public double Brightness { get; set; } = 100;
    public string Tool { get; set; } = "Brush";
    public int RangeStart { get; set; }
    public int RangeEnd { get; set; }
    public bool HasCustomLayer => Device is not null && (drafts.ContainsKey(Device.Id + ":custom") || Device.CustomEnabled);
    public string[] Colors
    {
        get
        {
            if (Device is null) return [];
            var key = Device.Id + ":" + Layer;
            var source = drafts.GetValueOrDefault(key) ?? (Layer == "custom" ? Device.CustomColors : Device.OverlayColors)?.ToArray() ?? [];
            return Resize(source, Device.LedCount, Layer);
        }
    }
    public static string[] Resize(IReadOnlyList<string> source, int count, string layer) => Enumerable.Range(0, count)
        .Select(i => i < source.Count ? source[i] : layer == "custom" ? "#000000" : "#00000000").ToArray();
    public void Apply(StateMessage state)
    {
        string? id = Device?.Id;
        var incoming = state.Lighting.Devices;
        if (Devices.Count == incoming.Count && Devices.Zip(incoming).All(p => p.First.Id == p.Second.Id &&
            p.First.LedCount == p.Second.LedCount && p.First.Connected == p.Second.Connected &&
            (p.First.CustomColors ?? []).SequenceEqual(p.Second.CustomColors ?? []) &&
            (p.First.OverlayColors ?? []).SequenceEqual(p.Second.OverlayColors ?? []))) return;
        Devices.Clear(); foreach (var d in incoming) Devices.Add(d);
        Device = Devices.FirstOrDefault(d => d.Id == id) ?? Devices.FirstOrDefault();
    }
    public string PaintColor(bool erase = false)
    {
        if (erase) return Layer == "overlay" ? "#00000000" : "#000000";
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Color);
        var scale = Math.Clamp(Brightness / 100, 0, 1);
        var text = $"#{(byte)Math.Round(c.R * scale):X2}{(byte)Math.Round(c.G * scale):X2}{(byte)Math.Round(c.B * scale):X2}";
        return Layer == "overlay" ? text + "FF" : text;
    }
    public void Paint(IEnumerable<int> indices, bool erase = false, bool gradient = false)
    {
        if (Device is null) return;
        try
        {
            var colors = Colors; var range = indices.Where(i => i >= 0 && i < colors.Length).Distinct().ToArray();
            var a = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Color);
            var b = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(SecondColor);
            for (int n = 0; n < range.Length; n++)
            {
                if (!gradient) colors[range[n]] = PaintColor(erase);
                else
                {
                    double t = n / (double)Math.Max(1, range.Length - 1), scale = Math.Clamp(Brightness / 100, 0, 1);
                    colors[range[n]] = $"#{(byte)Math.Round((a.R + (b.R - a.R) * t) * scale):X2}{(byte)Math.Round((a.G + (b.G - a.G) * t) * scale):X2}{(byte)Math.Round((a.B + (b.B - a.B) * t) * scale):X2}" + (Layer == "overlay" ? "FF" : "");
                }
            }
            if (!Recent.Contains(Color)) { Recent.Insert(0, Color); while (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1); }
            Queue(colors);
        }
        catch (Exception e) when (e is FormatException or NotSupportedException or ArgumentException) { Status = "Enter colours as #RRGGBB."; }
    }
    private void Queue(string[] colors)
    {
        if (Device is null) return;
        string id = Device.Id, targetLayer = Layer, key = id + ":" + targetLayer;
        drafts[key] = colors; int version = versions.GetValueOrDefault(key) + 1; versions[key] = version;
        OnPropertyChanged(nameof(Colors));
        _ = SendAsync();
        async Task SendAsync()
        {
            await Task.Delay(60);
            await sendGate.WaitAsync();
            try
            {
                if (versions[key] != version) return;
                var result = await client.SendAsync(Commands.SetLedColors, new { device = id, layer = targetLayer, colors });
                Status = result.Ok ? "LED colours saved" : result.Error ?? "LED edit failed";
            }
            catch (Exception) { Status = "LED edit could not reach the service."; }
            finally { sendGate.Release(); }
        }
    }
    public async Task ResizeAsync(int zone, int size)
    {
        var target = Device;
        if (target is null) return;
        string targetLayer = Layer;
        await sendGate.WaitAsync();
        try
        {
            string id = target.Id;
            // Flush the latest draft at its old length before the controller changes shape.
            foreach (string l in targetLayer == "custom" ? new[] { "overlay", "custom" } : new[] { "custom", "overlay" })
            {
                string key = id + ":" + l;
                versions[key] = versions.GetValueOrDefault(key) + 1;
                if (!drafts.TryGetValue(key, out var draft)) continue;
                var flushed = await client.SendAsync(Commands.SetLedColors, new { device = id, layer = l, colors = Resize(draft, target.LedCount, l) });
                if (!flushed.Ok) { Status = flushed.Error ?? "Pending edit could not be saved."; return; }
            }
            var response = await client.SendAsync(Commands.ResizeZone, new { device = id, zone, size });
            Status = response.Ok ? "Zone resized" : response.Error ?? "Resize failed";
            if (response.Ok && await client.GetStateAsync() is { } state)
            {
                var resized = state.Lighting.Devices.FirstOrDefault(d => d.Id == id);
                if (resized is not null)
                    foreach (string l in new[] { "custom", "overlay" })
                        if (drafts.TryGetValue(id + ":" + l, out var draft)) drafts[id + ":" + l] = Resize(draft, resized.LedCount, l);
                Apply(state); OnPropertyChanged(nameof(Colors));
            }
        }
        catch (Exception) { Status = "Zone resize could not reach the service."; }
        finally { sendGate.Release(); }
    }
    public void SavePreset(string name)
    {
        try { if (Device is not null) { presets.Save(new(Device.Id, name, Layer, Colors)); Status = "Layout saved: " + name; } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { Status = "Layout could not be saved. Check its name and storage."; }
    }
    public void LoadPreset(string name)
    {
        try
        {
            var preset = presets.Load().FirstOrDefault(p => p.Device == Device?.Id && p.Name == name);
            if (preset is null) { Status = "No layout with that name for this device."; return; }
            Layer = preset.Layer; Queue(Resize(preset.Colors, Device!.LedCount, Layer));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Status = "Layout could not be loaded."; }
    }
    public async Task ClearAsync()
    {
        var target = Device;
        if (target is null) return;
        await sendGate.WaitAsync();
        try
        {
            string id = target.Id;
            foreach (string l in new[] { "custom", "overlay" }) { var key = id + ":" + l; versions[key] = versions.GetValueOrDefault(key) + 1; }
            var response = await client.SendAsync(Commands.ClearLedColors, new { device = id });
            if (response.Ok)
            {
                drafts.Remove(id + ":custom"); drafts.Remove(id + ":overlay");
                if (Device?.Id == id) Device = Device with { CustomColors = null, OverlayColors = null };
            }
            Status = response.Ok ? "LED layers cleared" : response.Error ?? "Clear failed";
        }
        catch (Exception) { Status = "LED layers could not be cleared."; }
        finally { sendGate.Release(); }
    }
}
