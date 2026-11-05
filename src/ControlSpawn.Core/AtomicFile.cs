using System.Text;

namespace ControlSpawn.Core;

/// <summary>
/// Settings writes that survive a power cut. Writing a temp file and renaming it was not enough: the rename could reach
/// the disk before the data, so after a crash settings.json came back empty or zeroed and every fan curve, scene and lamp
/// reset to defaults. The data is now flushed to the disk before the swap, and the file it replaces is kept as
/// <c>&lt;file&gt;.bak</c>, the last good copy, which loaders fall back to when the main file is unreadable.
/// </summary>
public static class AtomicFile
{
    public static string Backup(string path) => path + ".bak";
    public static void WriteAllText(string path, string text)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(text));
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(full)) File.Replace(temporary, full, Backup(full), ignoreMetadataErrors: true);
            else File.Move(temporary, full);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
