using System.Text.Json;
using ControlSpawn.Core;
using ControlSpawn.Service;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class SettingsDurabilityTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "controlspawn-settings-" + Guid.NewGuid().ToString("N"));
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

    // #12: one bad part resets only that part.
    [Fact]
    public void OneInvalidPartIsResetAndEveryOtherPartIsKept()
    {
        var path = File("settings.json"); var store = new JsonSettingsStore(path);
        var good = new ServiceSettings
        {
            RouterHost = "192.0.2.9",
            Fans = new() { ["Radiator"] = new(ControlSpawn.Core.FanMode.Silent), ["Case fans"] = new(ControlSpawn.Core.FanMode.Turbo, ControlSpawn.Core.TemperatureSource.Gpu) },
            Lighting = new(ControlSpawn.Core.LightingEffect.Breathing, "#123456")
        };
        store.Save(good);
        // A lamp an older or buggy version wrote with an impossible brightness.
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
        json["lamps"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"host":"192.0.2.4","brightness":500,"id":"lamp:1","deviceId":1,"name":"Desk"}]""");
        System.IO.File.WriteAllText(path, json.ToJsonString());

        var loaded = store.Load();
        Assert.Equal("192.0.2.9", loaded.RouterHost);
        Assert.Equal(ControlSpawn.Core.FanMode.Silent, loaded.Fans["Radiator"].Mode);
        Assert.Equal((ControlSpawn.Core.FanMode.Turbo, ControlSpawn.Core.TemperatureSource.Gpu), (loaded.Fans["Case fans"].Mode, loaded.Fans["Case fans"].Source));
        Assert.Equal("#123456", loaded.Lighting.Color);
        Assert.Null(loaded.Lamps);   // only the lamps went back to defaults
        Assert.Equal("Some settings were invalid and were reset to defaults: lamps. Everything else was kept, and the original file was saved as a backup.", store.HealthWarning);
        Assert.Contains("\"brightness\":500", System.IO.File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "settings.json.corrupt-*.bak"))));
    }

    [Fact]
    public void NewerOrNonObjectFilesAreNotRepaired()
    {
        Assert.Null(JsonSettingsStore.Repair("""{"schemaVersion":2,"routerHost":"192.0.2.1","lamps":7}"""));
        Assert.Null(JsonSettingsStore.Repair("[1,2]"));
        Assert.Null(JsonSettingsStore.Repair("{"));
        Assert.Null(JsonSettingsStore.Repair("""{"routerHost":"192.0.2.1"}"""));   // nothing to repair
        var repaired = JsonSettingsStore.Repair("""{"routerHost":"192.0.2.1","fans":null,"ledLayers":{"x":{"custom":["red"]}}}""");
        Assert.Equal(["fans", "ledLayers"], repaired!.Value.Dropped);
        Assert.Equal("192.0.2.1", repaired.Value.Settings.RouterHost);
    }
}
