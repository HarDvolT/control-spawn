using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ControlSpawn.App.Services;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Lighting;
using static ControlSpawn.App.Views.AddDevicePanel;

namespace ControlSpawn.App.Views;

/// <summary>
/// Pick a device, paste its token, Add. The token is tried on the device before it is saved (live, 2026-11-03: the
/// fryer, dismissed once, could only be reached through a five-step import that refused a bare token). Exports from
/// token tools still import below. Secret input lives only in this view and is cleared on submission and unload.
/// </summary>
public sealed class TokenImportPanel : UserControl
{
    private const string Other = "Another device: type its IP";
    private readonly ComboBox device = new() { MinWidth = 440, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 4, 0, 12) };
    private readonly TextBox ip = Field(new TextBox());
    private readonly TextBlock ipLabel = Label("IP address, from your router or Mi Home");
    private readonly PasswordBox single = Field(new PasswordBox());
    private readonly TextBlock count = Note("");
    private readonly TextBlock status = Status();
    private readonly Button add = Primary("Add");
    private readonly PasswordBox input = Field(new PasswordBox { MaxLength = 4_000_000 });
    private readonly StackPanel choices = new();
    private readonly TextBlock message = Note("");
    private readonly List<(CheckBox Check, ExtractedToken Record)> records = [];
    private IReadOnlyList<DeviceState> candidates = [];
    private string? raw;
    public IControlSpawnClient? Client { get; set; }
    public Func<IReadOnlyList<DeviceState>> KnownDevices { get; set; } = () => [];

    public TokenImportPanel()
    {
        Visibility = Visibility.Collapsed; AllowDrop = true;
        var root = Step("Add a device with its token", "Pick the device and paste its 32-character token. Control Spawn tries it on the device before saving, so you know straight away if it fits.");
        AutomationProperties.SetName(device, "Device"); AutomationProperties.SetName(ip, "IP address"); AutomationProperties.SetName(single, "Token");
        root.Children.Add(Label("Device")); root.Children.Add(device);
        root.Children.Add(ipLabel); root.Children.Add(ip);
        root.Children.Add(Label("Token")); root.Children.Add(single); root.Children.Add(count);
        root.Children.Add(status);
        device.SelectionChanged += (_, _) => { bool other = device.SelectedItem as string == Other; ipLabel.Visibility = ip.Visibility = other ? Visibility.Visible : Visibility.Collapsed; };
        single.PasswordChanged += (_, _) => Count();
        single.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) _ = AddAsync(); };
        add.Click += (_, _) => _ = AddAsync();
        root.Children.Add(Actions(add, Secondary("Close", () => { Clear(); Visibility = Visibility.Collapsed; })));

        // The old multi-device import, for the text, JSON or CSV that token extractor tools write.
        var file = new StackPanel { Margin = new(0, 8, 0, 0) };
        file.Children.Add(Note("Paste or drop what a token extractor tool wrote (text, JSON or CSV), then tick the devices to save."));
        file.Children.Add(input);
        var actions = new WrapPanel();
        void Act(string title, Action action) { var button = new Button { Content = title, Margin = new(0, 0, 8, 8) }; button.Click += (_, _) => action(); actions.Children.Add(button); }
        Act("Paste", () => { try { input.Password = Clipboard.GetText(); Parse(); } catch { message.Text = "Clipboard could not be read."; } });
        Act("Read input", Parse);
        Act("Open file…", () => { var dialog = new OpenFileDialog { Filter = "Extractor output|*.txt;*.json;*.csv|All files|*.*" }; if (dialog.ShowDialog() == true) ReadFile(dialog.FileName); });
        Act("Save ticked", () => _ = SubmitAsync());
        file.Children.Add(actions); file.Children.Add(choices); file.Children.Add(message);
        root.Children.Add(new Expander { Header = "Import a file from a token tool", Content = file, Foreground = Brushes.Silver, Margin = new(0, 18, 0, 0) });
        Content = new Border { Child = root, CornerRadius = new(20), Padding = new(24), Margin = new(0, 8, 0, 16), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 77, 26)), Background = new SolidColorBrush(Color.FromRgb(23, 24, 29)) };
        PreviewDragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1) ReadFile(paths[0]); else if (e.Data.GetData(DataFormats.UnicodeText) is string text) { input.Password = text; Parse(); } e.Handled = true; };
        Unloaded += (_, _) => Clear();
        Count();
    }

    /// <summary>Opens on the single-token form, with <paramref name="deviceId"/> picked when it is listed.</summary>
    public void Open(uint? deviceId = null)
    {
        Clear();
        // Mi Home devices only; one still waiting for a token comes first, dismissed or not.
        candidates = KnownDevices().Where(d => d.Source == "miio").OrderBy(d => d.Configured).ThenBy(d => d.Name).ToArray();
        device.ItemsSource = candidates.Select(Describe).Append(Other).ToArray();
        int index = Array.FindIndex(candidates.ToArray(), d => d.DeviceId == deviceId);
        device.SelectedIndex = index >= 0 ? index : candidates.Count > 0 && !candidates[0].Configured ? 0 : candidates.Count;
        ipLabel.Visibility = ip.Visibility = device.SelectedItem as string == Other ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => single.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
    public void FocusImport() => single.Focus();
    private static string Describe(DeviceState d) => $"{d.Name}{(d.Ip is { } at ? " · " + at : "")} · {(d.Configured ? "added; a new token replaces its key" : "needs a token")}";

    private static string Clean(string token) => new(token.Where(c => !char.IsWhiteSpace(c) && c is not '-' and not ':').ToArray());
    private void Count()
    {
        var token = Clean(single.Password);
        bool ok = token.Length == 32 && token.All(Uri.IsHexDigit);
        count.Text = token.Length == 0 ? "32 characters of 0-9 and a-f. Spaces are ignored." : ok ? "32 of 32 characters ✓" : !token.All(Uri.IsHexDigit) ? "Only 0-9 and a-f belong in a token." : $"{token.Length} of 32 characters";
        add.IsEnabled = ok;
    }

    private async Task AddAsync()
    {
        if (Client is null || !add.IsEnabled) return;
        int index = device.SelectedIndex; var picked = index >= 0 && index < candidates.Count ? candidates[index] : null;
        if (picked is null && string.IsNullOrWhiteSpace(ip.Text)) { status.Text = "Type the device's IP address."; return; }
        string token = Clean(single.Password); single.Clear();
        add.IsEnabled = false; status.Text = "Checking the token on the device…";
        try
        {
            var reply = await Client.SendAsync(Commands.AddToken, new { token, id = picked?.Id, ip = picked is null ? ip.Text.Trim() : null });
            if (!reply.Ok) { status.Text = reply.Error ?? "The token couldn't be saved."; return; }
            status.Text = $"Saved. {picked?.Name ?? "The device"} connects within a few seconds.";
            await Client.SendAsync(Commands.FindDevices);
        }
        catch (Exception) { status.Text = "The token couldn't be saved. Is the Control Spawn service running?"; }
        finally { Count(); }
    }

    private void ReadFile(string path)
    {
        try { if (new FileInfo(path).Length > 4_000_000) throw new IOException(); input.Password = File.ReadAllText(path); Parse(); }
        catch { ClearImport(); message.Text = "Import file could not be read (maximum 4 MB)."; }
    }
    private void Parse()
    {
        raw = input.Password; input.Clear();
        try { Populate(TokenExtractorParser.Parse(raw)); }
        catch (ArgumentException) { ClearImport(); message.Text = "That isn't a format Control Spawn reads. For one token, use the form above."; }
    }
    private void Populate(IReadOnlyList<ExtractedToken> devices)
    {
        records.Clear(); choices.Children.Clear();
        foreach (var record in devices)
        {
            // Ticked unless Control Spawn can't control it: the list is what the user chose to import.
            var check = new CheckBox { Content = $"{record.Name} · {MiHomeSupport.Describe(record.Model)}", IsChecked = MiHomeSupport.Kind(record.Model) is not null || record.Model.Length == 0, Foreground = Brushes.WhiteSmoke, Margin = new(0, 6, 0, 6) };
            records.Add((check, record)); choices.Children.Add(check);
        }
        message.Text = $"{devices.Count} device{(devices.Count == 1 ? "" : "s")} found. Untick any you don't want, then Save ticked.";
    }
    private async Task SubmitAsync()
    {
        if (Client is null || raw is null) return;
        var selected = records.Where(r => r.Check.IsChecked == true).Select(r => r.Record.DeviceId).ToArray();
        if (selected.Length == 0) { message.Text = "Tick at least one device."; return; }
        string text = raw; var client = Client; ClearImport();
        try { var reply = await client.SendAsync(Commands.ImportTokens, new { text, selected }); message.Text = reply.Ok ? "Saved. The devices connect within a few seconds." : "Import could not be saved."; if (reply.Ok) await client.SendAsync(Commands.FindDevices); }
        catch { message.Text = "Import could not be saved."; }
    }
    private void ClearImport() { input.Clear(); raw = null; records.Clear(); choices.Children.Clear(); message.Text = ""; }
    public void Clear() { single.Clear(); ip.Clear(); status.Text = ""; ClearImport(); }
}
