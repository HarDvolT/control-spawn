using System.Collections.ObjectModel;

namespace ControlSpawn.App.Common;

/// <summary>
/// Row lists refreshed once a second used to be cleared and refilled, which makes their ItemsControl throw away and rebuild
/// every row's visuals each time, even when nothing changed (desktop widgets do this all day). Rows are records, so equal
/// rows are left alone and only changed ones are replaced, with rows added or removed only at the end.
/// </summary>
public static class RowSync
{
    public static void ReplaceWith<T>(this ObservableCollection<T> target, IEnumerable<T> items)
    {
        int i = 0;
        foreach (var item in items)
        {
            if (i < target.Count) { if (!EqualityComparer<T>.Default.Equals(target[i], item)) target[i] = item; }
            else target.Add(item);
            i++;
        }
        while (target.Count > i) target.RemoveAt(target.Count - 1);
    }
    /// <summary>Builds the new rows into a list, then applies only the differences.</summary>
    public static void Rebuild<T>(this ObservableCollection<T> target, Action<List<T>> build)
    {
        var rows = new List<T>(target.Count); build(rows); target.ReplaceWith(rows);
    }
}
