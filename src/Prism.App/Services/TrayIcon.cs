using Prism.App.Models;
using Prism.App.ViewModels;
using Prism.App.Widgets;
using Prism.Core.Ipc;
using Forms = System.Windows.Forms;
namespace Prism.App.Services;
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly IPrismClient client;
    private readonly Dictionary<string, string> applianceStates = new();
    private readonly HashSet<string> notified = new();
    private IReadOnlyList<ApplianceState> lastAppliances = [];
    private readonly Forms.ToolStripMenuItem cancelCooking = new("Cancel cooking");
    public TrayIcon(MainViewModel vm, IWidgetHost widgets, Action open, Action quit)
    {
        client = vm.Client;
        _menu.Items.Add("Open", null, (_, _) => open());
        var scenes = new Forms.ToolStripMenuItem("Scenes");
        foreach (var scene in SceneCatalog.All)
            scenes.DropDownItems.Add(scene.Name, null, async (_, _) => await vm.ApplySceneAsync(scene.Name));
        _menu.Items.Add(scenes);
        var fans = new Forms.ToolStripMenuItem("Fan mode");
        foreach (var mode in new[] { "Silent", "Standard", "Turbo", "Full" })
            fans.DropDownItems.Add(mode, null, async (_, _) => await vm.SetFanModeAsync(mode));
        _menu.Items.Add(fans);
        _menu.Items.Add("Lights off", null, async (_, _) => await vm.LightsOffAsync());
        _menu.Items.Add(cancelCooking);
        _menu.Items.Add("Show/Hide widgets", null, (_, _) => widgets.Toggle());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => quit());
        GraphiteTrayTheme.Apply(_menu);
        _icon = new Forms.NotifyIcon { Text = "Control Spawn", Icon = AppIcon(), ContextMenuStrip = _menu, Visible = true };
        cancelCooking.Click += async (_, _) => {
            foreach (var appliance in lastAppliances)
            {
                var cancel = appliance.Actions.FirstOrDefault(a => a.Name == "cancel-cooking"); if (cancel is null) continue;
                try { var reply = await client.SendAsync(Commands.ApplianceAction, new { id = appliance.Id, siid = cancel.Siid, aiid = cancel.Aiid }); if (!reply.Ok) _icon.ShowBalloonTip(6000, appliance.Name, "Cancel could not be delivered.", Forms.ToolTipIcon.Warning); }
                catch { _icon.ShowBalloonTip(6000, appliance.Name, "Cancel could not be delivered.", Forms.ToolTipIcon.Warning); }
            }
        };
        _icon.DoubleClick += (_, _) => open();
        client.StateReceived += OnState;
    }
    // The exe's own icon (Assets/ControlSpawn.ico); the stock one only if it can't be read.
    private static System.Drawing.Icon AppIcon()
    {
        try { return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch (Exception e) when (e is ArgumentException or IOException) { return System.Drawing.SystemIcons.Application; }
    }
    private void OnState(object? sender, StateMessage state)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => OnState(sender, state)); return; }
        var discovered = (state.NewDevices ?? []).Where(d => notified.Add(d.Id)).ToArray();
        if (state.Discovery?.NotifyNewDevices != false && discovered.Length > 0) _icon.ShowBalloonTip(6000, "New devices found", string.Join(", ", discovered.Select(d => d.Name)), Forms.ToolTipIcon.Info);
        lastAppliances = state.Appliances ?? [];
        foreach (var appliance in state.Appliances ?? [])
        {
            if (applianceStates.TryGetValue(appliance.Id, out var old))
            {
                if (appliance.Status == "done" && old != "done") _icon.ShowBalloonTip(6000, appliance.Name, "Cooking finished", Forms.ToolTipIcon.Info);
                if (appliance.TurnBasket && old != "basket") _icon.ShowBalloonTip(6000, appliance.Name, "Turn basket", Forms.ToolTipIcon.Info);
            }
            applianceStates[appliance.Id] = appliance.TurnBasket ? "basket" : appliance.Status;
        }
        cancelCooking.Enabled = lastAppliances.Any(a => a.Actions.Any(x => x.Name == "cancel-cooking"));
        cancelCooking.Text = lastAppliances.Count == 1 ? "Cancel cooking · " + lastAppliances[0].Name : "Cancel all cooking";
    }
    public void Dispose() { client.StateReceived -= OnState; _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); }
}
