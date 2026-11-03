using System.Text.Json;
using Prism.Core;
using Prism.Service;
using Xunit;

namespace Prism.Service.Tests;

public sealed class SettingsDurabilityTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "prism-settings-" + Guid.NewGuid().ToString("N"));
    private string File(string name) => Path.Combine(folder, name);
    public SettingsDurabilityTests() => Directory.CreateDirectory(folder);
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    [Fact]
    public void EachSaveKeepsThePreviousFileAsTheLastGoodCopyAndLeavesNoTempFiles()
    {
        var store = new JsonSettingsStore(File("settings.json"));
        store.Save(new ServiceSettings { RouterHost = "192.0.2.1" });
        Assert.False(System.IO.File.Exists(File("settings.json.bak")));
        store.Save(new ServiceSettings { RouterHost = "192.0.2.2" });
        Assert.Equal("192.0.2.1", JsonDocument.Parse(System.IO.File.ReadAllText(File("settings.json.bak"))).RootElement.GetProperty("routerHost").GetString());
        Assert.Equal("192.0.2.2", store.Load().RouterHost);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0\0\0\0\0\0\0\0")]
    [InlineData("{\"routerHost\":\"192.0.")]
    public void FileLeftEmptyZeroedOrCutOffByACrashLoadsTheLastGoodCopy(string damaged)
    {
        var path = File("settings.json"); var store = new JsonSettingsStore(path);
        store.Save(new ServiceSettings { RouterHost = "192.0.2.7" });
        store.Save(new ServiceSettings { RouterHost = "192.0.2.8" });
        System.IO.File.WriteAllText(path, damaged);
        var loaded = store.Load();
        Assert.Equal("192.0.2.7", loaded.RouterHost);
        Assert.Contains("last good copy", store.HealthWarning);
        Assert.Single(Directory.GetFiles(folder, "settings.json.corrupt-*.bak"));   // the damaged file is kept as evidence
    }

    [Fact]
    public void BothDamagedStillStartsWithDefaults()
    {
        var path = File("settings.json");
        System.IO.File.WriteAllText(path, "{"); System.IO.File.WriteAllText(AtomicFile.Backup(path), "nope");
        var store = new JsonSettingsStore(path);
        Assert.Equal(new ServiceSettings().RouterHost, store.Load().RouterHost);
        Assert.Contains("defaults were loaded", store.HealthWarning);
    }

    [Fact]
    public void OldUnversionedBackupStillLoads()
    {
        var path = File("settings.json");
        System.IO.File.WriteAllText(path, "\0\0");
        System.IO.File.WriteAllText(AtomicFile.Backup(path), "{\"lighting\":{\"color\":\"#123456\"}}");   // written before schemaVersion existed
        Assert.Equal("#123456", new JsonSettingsStore(path).Load().Lighting.Color);
    }
}
