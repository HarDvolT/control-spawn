namespace ControlSpawn.App.Widgets;

/// <summary>
/// The Widgets page talks to the desktop through this seam, so its view-model can be tested
/// without creating any windows.
/// </summary>
public interface IWidgetHost
{
    event Action? VisibilityChanged;
    bool IsVisible { get; }
    void ShowAll();
    void HideAll();
    void Toggle();
    void Sync();
    /// <summary>Lines every widget up down the right edge of the main screen.</summary>
    void LineUp();
}

/// <summary>Test double / headless host.</summary>
public sealed class NullWidgetHost : IWidgetHost
{
    public event Action? VisibilityChanged;
    public bool IsVisible { get; private set; } = true;
    public int SyncCount { get; private set; }

    public void ShowAll() { IsVisible = true; VisibilityChanged?.Invoke(); }
    public void HideAll() { IsVisible = false; VisibilityChanged?.Invoke(); }
    public void Toggle() { IsVisible = !IsVisible; VisibilityChanged?.Invoke(); }
    public void Sync() => SyncCount++;
    public void LineUp() { }
}
