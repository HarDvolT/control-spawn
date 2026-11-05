using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ControlSpawn.App.Common;
using ControlSpawn.App.Controls;
using ControlSpawn.App.Navigation;
using ControlSpawn.App.Services;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Lighting;

namespace ControlSpawn.App.Views;

/// <summary>
/// Adds Mi Home devices in three steps: sign in, tick, done. Adding the air fryer used to mean finding its token with
/// a separate extractor tool and pasting it here. The password lives only in this view and goes straight to Xiaomi.
/// </summary>
public sealed class AddDevicePanel : UserControl
{
    private static readonly (string Label, string Region)[] Regions =
        [("Find it for me", XiaomiTokenProvider.AutoRegion), ("Europe", "de"), ("Singapore and Asia", "sg"), ("United States", "us"), ("China mainland", "cn"), ("Russia", "ru"), ("India", "in"), ("Taiwan", "tw")];
    private readonly ContentControl body = new();
    private IReadOnlyList<ExtractedToken> found = [];
    private string account = "";
    private int region;
    private uint? highlight;
    public IControlSpawnClient? Client { get; set; }
    public IXiaomiTokenProvider Provider { get; set; } = new XiaomiTokenProvider();
    /// <summary>The registry as the page last saw it, to tell what is added already and what answers on the Wi-Fi.</summary>
    public Func<IReadOnlyList<DeviceState>> KnownDevices { get; set; } = () => [];
    /// <summary>"I have the token": the device the steps were opened for, if any.</summary>
    public event EventHandler<uint?>? TokenEntryRequested;
    public event EventHandler<PageKey>? OpenPageRequested;

    public AddDevicePanel()
    {
        Visibility = Visibility.Collapsed;
        Content = new Border { Child = body, CornerRadius = new(20), Padding = new(24), Margin = new(0, 8, 0, 16), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 77, 26)), Background = new SolidColorBrush(Color.FromRgb(23, 24, 29)) };
        Unloaded += (_, _) => Close();
    }

    /// <summary>Opens on the sign-in step; <paramref name="device"/> is ticked when the account has it.</summary>
    public void Open(uint? device = null) { highlight = device; Visibility = Visibility.Visible; ShowSignIn(); }
    // Closing drops the found keys and abandons a sign-in still running, so its late reply can't reopen the steps.
    private CancellationTokenSource? signingIn;
    public void Close() { signingIn?.Cancel(); found = []; body.Content = null; Visibility = Visibility.Collapsed; }

    private void ShowSignIn()
    {
        var root = Step("Add Mi Home devices", "Sign in with the Xiaomi account your Mi Home app uses. Control Spawn reads your devices and their keys once. Your password goes only to Xiaomi and is never saved.");
        var user = Field(new TextBox { Text = account }); AutomationProperties.SetName(user, "Xiaomi account");
        var password = Field(new PasswordBox()); AutomationProperties.SetName(password, "Password");
        var where = new ComboBox { ItemsSource = Regions.Select(r => r.Label).ToArray(), SelectedIndex = region, Margin = new(0, 4, 0, 4), MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(where, "Mi Home region");
        root.Children.Add(Label("Xiaomi account: email, phone or account ID")); root.Children.Add(user);
        root.Children.Add(Label("Password")); root.Children.Add(password);
        root.Children.Add(Label("Mi Home region")); root.Children.Add(where);
        root.Children.Add(Note("Leave it on “Find it for me” unless you know it: Mi Home shows it under Profile → Settings → Region."));
        var status = Status(); root.Children.Add(status);
        var signIn = Primary("Sign in");
        async void SignIn()
        {
            if (string.IsNullOrWhiteSpace(user.Text) || password.Password.Length == 0) { status.Text = "Enter the account and password first."; return; }
            account = user.Text.Trim(); region = Math.Max(0, where.SelectedIndex);
            string secret = password.Password; password.Clear();
            signIn.IsEnabled = false; status.Text = region == 0 ? "Signing in and looking for your devices on every Mi Home server…" : "Signing in…";
            signingIn?.Cancel(); var cancel = signingIn = new CancellationTokenSource();
            try
            {
                var result = await Provider.SignInAsync(account, secret, Regions[region].Region, cancel.Token);
                if (cancel.IsCancellationRequested) return;
                if (result.VerificationUrl is { Scheme: "https", Host: "account.xiaomi.com" } url)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                    status.Text = "Xiaomi wants to confirm it's you. Finish in the browser window that opened, then press Sign in again.";
                }
                else if (result.Devices.Count == 0) status.Text = region == 0 ? "Signed in, but this account has no Wi-Fi devices." : "Signed in, but there are no devices on that server. Try “Find it for me”.";
                else { found = result.Devices; ShowPick(); }
            }
            catch (Exception) when (cancel.IsCancellationRequested) { }
            catch (Exception) { status.Text = "Sign-in didn't work. Check the account and password, or use a token file instead."; }
            finally { signIn.IsEnabled = true; if (signingIn == cancel) signingIn = null; cancel.Dispose(); }
        }
        signIn.Click += (_, _) => SignIn();
        password.KeyDown += (_, e) => { if (e.Key == Key.Enter) SignIn(); };
        var file = Link("I have the token"); file.Click += (_, _) => { var device = highlight; Close(); TokenEntryRequested?.Invoke(this, device); };
        root.Children.Add(Actions(signIn, Secondary("Cancel", Close), file));
        body.Content = root;
        Dispatcher.BeginInvoke(() => (account.Length == 0 ? (Control)user : password).Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowPick()
    {
        var known = KnownDevices();
        bool Added(ExtractedToken r) => known.Any(d => d.DeviceId == r.DeviceId && d.Configured && d.Status == DeviceStatus.Ready);
        var rows = found.OrderBy(r => MiHomeSupport.Kind(r.Model) is null).ThenBy(Added).ThenBy(r => r.Name).ToArray();
        int supported = rows.Count(r => MiHomeSupport.Kind(r.Model) is not null);
        var root = Step($"{found.Count} device{(found.Count == 1 ? "" : "s")} in your Mi Home account",
            supported == 0 ? "Control Spawn can't control any of these yet." : "Ticked devices are added. From then on Control Spawn talks to them over your Wi-Fi, without the cloud.");
        var checks = new List<(CheckBox Box, ExtractedToken Record)>();
        var add = Primary("Add");
        void Count() { int n = checks.Count(c => c.Box.IsChecked == true); add.Content = n == 0 ? "Add" : $"Add {n} device{(n == 1 ? "" : "s")}"; add.IsEnabled = n > 0; }
        foreach (var record in rows)
        {
            var kind = MiHomeSupport.Kind(record.Model);
            bool added = Added(record), seen = known.Any(d => d.DeviceId == record.DeviceId && d.Status != DeviceStatus.Offline) || (record.Ip is { Length: > 0 } ip && known.Any(d => d.Ip == ip));
            var copy = new StackPanel();
            copy.Children.Add(new TextBlock { Text = record.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.WhiteSmoke });
            copy.Children.Add(new TextBlock { Text = MiHomeSupport.Describe(record.Model) + " · " + (kind is null ? "not supported yet" : added ? "added already; tick to refresh its key" : seen ? "ready to add" : "ready to add, not seen on this Wi-Fi yet"),
                FontSize = 12, Foreground = new SolidColorBrush(ColorUtil.TextTertiary), Margin = new(0, 2, 0, 0) });
            var icon = new DeviceIcon { Kind = kind == DeviceKind.Appliance ? DeviceIconKind.Fryer : kind == DeviceKind.Lamp ? DeviceIconKind.Lamp : DeviceIconKind.Network, Width = 20, Height = 20, Color = kind is null ? ColorUtil.TextTertiary : ColorUtil.Accent, Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            // The theme switch sits right after its content; a fixed width lines the switches up.
            var line = new DockPanel { Width = 560 }; DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon); line.Children.Add(copy);
            var box = new CheckBox { Content = line, IsEnabled = kind is not null, IsChecked = kind is not null && (!added || record.DeviceId == highlight), Margin = new(0, 6, 0, 6), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, record.Name);
            box.Checked += (_, _) => Count(); box.Unchecked += (_, _) => Count();
            checks.Add((box, record));
            root.Children.Add(box);
        }
        var status = Status(); root.Children.Add(status);
        add.Click += async (_, _) =>
        {
            if (Client is null) return;
            var chosen = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Record).ToArray();
            string text = System.Text.Json.JsonSerializer.Serialize(chosen.Select(d => new { name = d.Name, id = d.DeviceId, ip = d.Ip, model = d.Model, token = d.Token }));
            add.IsEnabled = false; status.Text = "Adding…";
            try
            {
                var reply = await Client.SendAsync(Commands.ImportTokens, new { text, selected = chosen.Select(d => d.DeviceId).ToArray() });
                if (!reply.Ok) { status.Text = "They couldn't be added: " + (reply.Error ?? "the service refused."); Count(); return; }
                await Client.SendAsync(Commands.FindDevices);
                ShowDone(chosen);
            }
            catch (Exception) { status.Text = "They couldn't be added. Is the Control Spawn service running?"; Count(); }
        };
        Count();
        root.Children.Add(Actions(add, Secondary("Back", ShowSignIn), Secondary("Cancel", Close)));
        body.Content = root;
    }

    private void ShowDone(IReadOnlyList<ExtractedToken> added)
    {
        found = [];
        var root = Step(added.Count == 1 ? added[0].Name + " is added" : $"{added.Count} devices added",
            string.Join(", ", added.Select(d => d.Name)) + ". They connect within a few seconds.");
        var buttons = new List<Button>();
        if (added.Any(d => MiHomeSupport.Kind(d.Model) == DeviceKind.Appliance)) buttons.Add(Page(buttons.Count == 0, "Open Air fryer", PageKey.Appliances));
        if (added.Any(d => MiHomeSupport.Kind(d.Model) == DeviceKind.Lamp)) buttons.Add(Page(buttons.Count == 0, "Open Lamps", PageKey.Lamp));
        buttons.Add(Secondary("Done", Close));
        root.Children.Add(Actions(buttons.ToArray()));
        body.Content = root;
        Button Page(bool primary, string text, PageKey key)
        {
            var button = primary ? Primary(text) : Secondary(text, () => { });
            button.Click += (_, _) => { Close(); OpenPageRequested?.Invoke(this, key); };
            return button;
        }
    }

    internal static StackPanel Step(string title, string copy)
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.WhiteSmoke });
        root.Children.Add(new TextBlock { Text = copy, Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 16) });
        return root;
    }
    // The theme's text boxes are bare; a filled field reads as something to type into.
    internal static T Field<T>(T box) where T : Control
    {
        box.Background = new SolidColorBrush(Color.FromRgb(30, 32, 38)); box.BorderBrush = new SolidColorBrush(Color.FromRgb(48, 50, 57)); box.BorderThickness = new(1);
        box.Foreground = Brushes.WhiteSmoke; box.Padding = new(12, 9, 12, 9); box.MaxWidth = 440; box.HorizontalAlignment = HorizontalAlignment.Left; box.Width = 440; box.Margin = new(0, 4, 0, 12);
        return box;
    }
    internal static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = new SolidColorBrush(ColorUtil.TextTertiary) };
    internal static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = new SolidColorBrush(ColorUtil.TextTertiary), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
    internal static TextBlock Status() => new() { Foreground = new SolidColorBrush(ColorUtil.Parse("#F5A524", Colors.Orange)), TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    internal static Button Primary(string text) { var b = new Button { Content = text, MinHeight = 40, Padding = new(20, 0, 20, 0), Margin = new(0, 0, 10, 0) }; b.SetResourceReference(StyleProperty, "PrimaryButton"); return b; }
    internal static Button Secondary(string text, Action click) { var b = new Button { Content = text, MinHeight = 40, Padding = new(18, 0, 18, 0), Margin = new(0, 0, 10, 0) }; b.Click += (_, _) => click(); return b; }
    internal static Button Link(string text) { var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0) }; b.SetResourceReference(StyleProperty, "TextAction"); return b; }
    internal static WrapPanel Actions(params Button[] buttons)
    {
        var row = new WrapPanel { Margin = new(0, 18, 0, 0) };
        foreach (var button in buttons) { AutomationProperties.SetName(button, button.Content as string ?? ""); row.Children.Add(button); }
        return row;
    }
}
