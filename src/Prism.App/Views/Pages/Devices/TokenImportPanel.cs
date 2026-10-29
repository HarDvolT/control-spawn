using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Prism.App.Services;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.App.Views;

/// <summary>Secret input lives only in this view and is cleared on submission/unload.</summary>
public sealed class TokenImportPanel : UserControl
{
    private readonly PasswordBox input = new() { MaxLength = 4_000_000, MinWidth = 200, Margin = new(0, 8, 0, 8), Padding = new(10),
        Background = new SolidColorBrush(Color.FromRgb(37, 37, 41)), Foreground = Brushes.WhiteSmoke, BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)) };
    private readonly StackPanel choices = new();
    private readonly TextBlock message = new() { Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap };
    private readonly List<(CheckBox Check, ExtractedToken Record)> records = [];
    private string? raw;
    public IPrismClient? Client { get; set; }
    public DeviceState? TargetDevice { get; set; }
    public IXiaomiTokenProvider? CloudProvider { get; set; } = new XiaomiTokenProvider();
    public TokenImportPanel()
    {
        Visibility = Visibility.Collapsed; AllowDrop = true;
        var root = new StackPanel { Margin = new(12) };
        root.Children.Add(new TextBlock { Text = "Import Tokens Extractor output", FontSize = 18, Foreground = Brushes.WhiteSmoke });
        root.Children.Add(new TextBlock { Text = "Paste or drop a text block, JSON or CSV. Only ticked devices are saved. Tokens stay masked.", Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(input);
        var actions = new WrapPanel();
        Add("Paste", () => { try { input.Password = Clipboard.GetText(); Parse(); } catch { message.Text = "Clipboard could not be read."; } });
        Add("Read input", Parse);
        Add("Import file…", () => { var dialog = new OpenFileDialog { Filter = "Extractor output|*.txt;*.json;*.csv|All files|*.*" }; if (dialog.ShowDialog() == true) ReadFile(dialog.FileName); });
        Add("Save selected", () => _ = SubmitAsync()); Add("Close", () => { Clear(); Visibility = Visibility.Collapsed; });
        root.Children.Add(actions); root.Children.Add(choices); root.Children.Add(message);
        var cloud = new Button { Content = "Sign in with Xiaomi (optional)", Margin = new(0, 12, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        cloud.Click += (_, _) => {
            var dialog = new XiaomiSignInWindow(CloudProvider) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true && dialog.Devices is { } devices)
            {
                raw = System.Text.Json.JsonSerializer.Serialize(devices.Select(d => new { name = d.Name, id = d.DeviceId, ip = d.Ip, model = d.Model, token = d.Token }));
                Populate(devices);
            }
        };
        root.Children.Add(cloud); Content = new Border { CornerRadius = new(10), Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)), Child = root, Margin = new(0, 8, 0, 16) };
        PreviewDragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1) ReadFile(paths[0]); else if (e.Data.GetData(DataFormats.UnicodeText) is string text) { input.Password = text; Parse(); } e.Handled = true; };
        Unloaded += (_, _) => Clear();
        void Add(string title, Action action) { var button = new Button { Content = title, Margin = new(0, 0, 8, 8) }; button.Click += (_, _) => action(); actions.Children.Add(button); }
    }
    public void FocusImport() => input.Focus();
    private void ReadFile(string path)
    {
        try { if (new FileInfo(path).Length > 4_000_000) throw new IOException(); input.Password = File.ReadAllText(path); Parse(); }
        catch { Clear(); message.Text = "Import file could not be read (maximum 4 MB)."; }
    }
    private void Parse()
    {
        raw = input.Password; input.Clear();
        if (raw.Trim() is { Length: 32 } single && single.All(Uri.IsHexDigit) && TargetDevice?.DeviceId is { } id)
            raw = System.Text.Json.JsonSerializer.Serialize(new[] { new { id, name = TargetDevice.Name, ip = TargetDevice.Ip, model = TargetDevice.Model ?? "", token = single } });
        try { Populate(TokenExtractorParser.Parse(raw)); }
        catch (ArgumentException) { Clear(); message.Text = "Could not parse extractor output. Check the format."; }
    }
    private void Populate(IReadOnlyList<ExtractedToken> devices)
    {
        records.Clear(); choices.Children.Clear();
        foreach (var record in devices)
        {
            var check = new CheckBox { Content = $"{record.Name} · {record.DeviceId} · {record.Model}\n{record.MaskedToken}", IsChecked = false, Foreground = Brushes.WhiteSmoke, Margin = new(0, 8, 0, 8) };
            records.Add((check, record)); choices.Children.Add(check);
        }
        message.Text = $"{devices.Count} records found. Select the devices to import.";
    }
    private async Task SubmitAsync()
    {
        if (Client is null || raw is null) return;
        var selected = records.Where(r => r.Check.IsChecked == true).Select(r => r.Record.DeviceId).ToArray();
        if (selected.Length == 0) { message.Text = "Select at least one device."; return; }
        string text = raw; var client = Client; Clear();
        try { var reply = await client.SendAsync(Commands.ImportTokens, new { text, selected }); message.Text = reply.Ok ? "Selected tokens saved securely." : "Import could not be saved."; if (reply.Ok) await client.SendAsync(Commands.FindDevices); }
        catch { message.Text = "Import could not be saved."; }
    }
    public void Clear() { input.Clear(); raw = null; TargetDevice = null; records.Clear(); choices.Children.Clear(); message.Text = ""; }
}

public sealed class XiaomiSignInWindow : Window
{
    public IReadOnlyList<ExtractedToken>? Devices { get; private set; }
    public XiaomiSignInWindow(IXiaomiTokenProvider? provider)
    {
        Title = "Optional Xiaomi token import"; Width = 480; Height = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(26, 26, 30)); Foreground = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new(24) }; var user = new TextBox(); var password = new PasswordBox();
        var region = new ComboBox { ItemsSource = new[] { "cn", "de", "us", "ru", "sg", "tw", "in", "i2" }, SelectedIndex = 4 };
        var message = new TextBlock { Text = "Optional. Credentials are used once and never stored. Only selected device tokens are saved.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        var button = new Button { Content = "Sign in", Margin = new(0, 12, 0, 0) };
        panel.Children.Add(message); panel.Children.Add(new TextBlock { Text = "Xiaomi account" }); panel.Children.Add(user); panel.Children.Add(new TextBlock { Text = "Password" }); panel.Children.Add(password); panel.Children.Add(new TextBlock { Text = "Region" }); panel.Children.Add(region); panel.Children.Add(button); Content = panel;
        button.Click += async (_, _) => {
            button.IsEnabled = false;
            try
            {
                if (provider is null) { message.Text = "Cloud sign-in provider is unavailable. Use the extractor file import."; return; }
                string secret = password.Password; password.Clear();
                var result = await provider.SignInAsync(user.Text, secret, (string)region.SelectedItem, CancellationToken.None);
                if (result.VerificationUrl is { Scheme: "https", Host: "account.xiaomi.com" } url)
                { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); message.Text = "Complete verification in the browser, then sign in again."; }
                else { Devices = result.Devices; DialogResult = true; }
            }
            catch { message.Text = "Sign-in failed. Check your credentials or use an extractor file."; }
            finally { password.Clear(); button.IsEnabled = true; }
        };
        Closed += (_, _) => { user.Clear(); password.Clear(); };
    }
}
