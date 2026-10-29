using Prism.App.Models;
using Prism.App.ViewModels;
using Prism.App.Widgets;
using Forms = System.Windows.Forms;
namespace Prism.App.Services;
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    public TrayIcon(MainViewModel vm, IWidgetHost widgets, Action open, Action quit)
    {
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
        _menu.Items.Add("Show/Hide widgets", null, (_, _) => widgets.Toggle());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => quit());
        GraphiteTrayTheme.Apply(_menu);
        _icon = new Forms.NotifyIcon { Text = "Prism", Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => open();
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); }
}
