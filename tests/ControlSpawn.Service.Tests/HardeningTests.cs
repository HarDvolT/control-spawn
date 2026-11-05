using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Hardware;
using ControlSpawn.Lighting;
using ControlSpawn.Router;
using ControlSpawn.Service;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class HardeningTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "hardening-" + Guid.NewGuid().ToString("N"));
        public Sandbox() => System.IO.Directory.CreateDirectory(Directory);
        public string File(string name) => Path.Combine(Directory, name);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
    private sealed class Encryption : ISecretEncryption
    {
        public bool RefuseLegacy { get; init; }
        public byte[] Protect(byte[] value, byte[] entropy) => value.Select((b, i) => (byte)(b ^ entropy[i % entropy.Length])).ToArray();
        public byte[] Unprotect(byte[] value, byte[] entropy) => Protect(value, entropy);
        public byte[] UnprotectLegacy(byte[] value) => RefuseLegacy ? throw new CryptographicException() : value.ToArray();
    }
    private static SecretStore Store(string directory, Encryption? encryption = null) => new(directory, encryption ?? new(), p => Directory.CreateDirectory(p));

    [Fact]
    public void SecretAclContainsOnlySystemAndAdministratorsWithoutInheritance()
    {
        var acl = SecretStore.CreateSecurity(); Assert.True(acl.AreAccessRulesProtected);
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }, rules.Select(r => r.IdentityReference.Value).Order().ToArray());
        Assert.All(rules, rule => { Assert.False(rule.IsInherited); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights); });
    }

    [Fact]
    public void SecretsShareInstallEntropyAndMigrateOnlyAfterSuccessfulSave()
    {
        using var box = new Sandbox(); var root = box.File("secrets"); var legacy = box.File("old.json");
        byte[] secret = Encoding.UTF8.GetBytes("fixture-only-secret");
        File.WriteAllText(legacy, JsonSerializer.Serialize(new { dpapi = Convert.ToBase64String(secret) }));
        var store = Store(root);
        Assert.Equal(secret, store.Load("router.credentials.json", legacy)); Assert.False(File.Exists(legacy));
        var entropy = File.ReadAllBytes(Path.Combine(root, "entropy.bin")); Assert.Equal(32, entropy.Length);
        Assert.Equal(secret, Store(root).Load("router.credentials.json"));
        store.Save("lamp.123", secret);
        Assert.Equal(entropy, File.ReadAllBytes(Path.Combine(root, "entropy.bin")));
        Assert.NotEqual(secret, File.ReadAllBytes(Path.Combine(root, "lamp.123")));
        Assert.Throws<ArgumentException>(() => store.Save("../escape", secret));
        store.Remove("lamp.123"); Assert.Null(store.Load("lamp.123"));
    }
    [Fact]
    public void WrongUserOrFailedMigrationKeepsLegacyRecord()
    {
        using var box = new Sandbox(); var legacy = box.File("old.json");
        File.WriteAllText(legacy, "{\"dpapi\":\"AQID\"}");
        Assert.Throws<CryptographicException>(() => Store(box.File("secrets"), new() { RefuseLegacy = true }).Load("lamp", legacy));
        Assert.True(File.Exists(legacy));
        var denied = new SecretStore(box.File("denied"), new Encryption(), _ => throw new UnauthorizedAccessException());
        Assert.Throws<UnauthorizedAccessException>(() => denied.Load("lamp", legacy)); Assert.True(File.Exists(legacy));
    }
    [Fact]
    public void MissingEntropyNeverRekeysExistingSecrets()
    {
        using var box = new Sandbox(); var store = Store(box.Directory); store.Save("lamp", [1, 2, 3]);
        File.Delete(box.File("entropy.bin"));
        Assert.Throws<CryptographicException>(() => store.Load("lamp"));
        Assert.Throws<CryptographicException>(() => store.Save("other", [4])); Assert.False(File.Exists(box.File("entropy.bin")));
    }
    [Fact]
    public void ConsoleMigrationIncludesDisabledDevicesAndRouterSession()
    {
        using var box = new Sandbox(); var old = box.File(ControlSpawn.Core.ProductNames.DataFolder); Directory.CreateDirectory(Path.Combine(old, "router"));
        foreach (var name in new[] { "lamp.json", "lamp.json.123", "router/credentials.json", "router/session.json" })
            File.WriteAllText(Path.Combine(old, name), "{\"dpapi\":\"AQID\"}");
        var store = Store(box.File("machine")); Assert.True(SecretMigration.Run(box.Directory, store));
        foreach (var key in new[] { "lamp", "lamp.123", "router.credentials.json", "router.session.json" }) Assert.Equal(new byte[] { 1, 2, 3 }, store.Load(key));
        Assert.Empty(Directory.GetFiles(old, "*", SearchOption.AllDirectories));
        Assert.True(SecretMigration.Run(box.Directory, store));
    }
    [Fact]
    public void LampAndRouterAdaptersUseSharedMachineStore()
    {
        using var box = new Sandbox(); var secrets = Store(box.File("secrets"));
        var lamps = new LampTokenStore(box.File("old-lamp"), secrets: secrets);
        var token = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        lamps.Save(123, token); Assert.Equal(token, lamps.Load(123));
        var router = new CredentialStore(box.File("old-router"), secrets: secrets);
        router.SavePassword("fixture-password"); router.SaveSession("fixture-session");
        Assert.Equal("fixture-password", router.LoadPassword()); Assert.Equal("fixture-session", router.LoadSession());
        Assert.False(Directory.Exists(box.File("old-router")));
        router.ForgetPassword(); lamps.Remove(123); Assert.Null(router.LoadPassword()); Assert.Null(router.LoadSession()); Assert.Null(lamps.Load(123));
    }
    [Fact]
    public void HeartbeatExpiresAtThreeSecondsAndResetsOnlyOnce()
    {
        int calls = 0; var watch = new FanHeartbeat(() => calls++, TimeSpan.Zero);
        Assert.False(watch.Check(TimeSpan.FromSeconds(2.999)));
        watch.Beat(TimeSpan.FromSeconds(2)); Assert.False(watch.Check(TimeSpan.FromSeconds(4.999)));
        Assert.True(watch.Check(TimeSpan.FromSeconds(5))); watch.Beat(TimeSpan.FromSeconds(6));
        Assert.True(watch.Check(TimeSpan.FromSeconds(9))); Assert.Equal(1, calls);
    }
    [Fact]
    public void FanGuardServesOnlyTheServiceInItsOwnFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Control Spawn", "service");
        var guard = Path.Combine(folder, "ControlSpawn.FanGuard.exe");
        Assert.True(ProductNames.IsServiceBeside(Path.Combine(folder, "ControlSpawn.Service.exe"), guard));
        Assert.True(ProductNames.IsServiceBeside(Path.Combine(folder, "CONTROLSPAWN.SERVICE.EXE"), guard));
        Assert.False(ProductNames.IsServiceBeside(null, guard));
        Assert.False(ProductNames.IsServiceBeside(Path.Combine(folder, "Prism.Service.exe"), guard));          // the 1.0.2 exe
        Assert.False(ProductNames.IsServiceBeside(Path.Combine(folder, "ControlSpawn.FanGuard.exe"), guard));  // itself or another guard
        Assert.False(ProductNames.IsServiceBeside(Path.Combine(Path.GetTempPath(), "elsewhere", "ControlSpawn.Service.exe"), guard));
    }
    [Fact]
    public void RateBudgetIsSharedForAProcessAndRenewsWithTime()
    {
        var now = TimeSpan.Zero; var limiter = new ClientCommandLimiter(() => now);
        for (int i = 0; i < 20; i++) Assert.True(limiter.Allow("42"));
        Assert.False(limiter.Allow("42")); Assert.True(limiter.Allow("43"));
        now = TimeSpan.FromSeconds(1); Assert.True(limiter.Allow("42"));
    }
    [Fact]
    public void AppVerificationRejectsMissingManifestAndEitherChangedFile()
    {
        using var box = new Sandbox(); var hashes = new Dictionary<string, string>();
        foreach (var name in new[] { "ControlSpawn.exe", "ControlSpawn.dll" })
        { File.WriteAllText(box.File(name), name); hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(box.File(name)))); }
        Assert.False(PipeClientVerifier.MatchesInstallation(box.Directory, null));
        Assert.True(PipeClientVerifier.MatchesInstallation(box.Directory, hashes));
        File.AppendAllText(box.File("ControlSpawn.dll"), "changed"); Assert.False(PipeClientVerifier.MatchesInstallation(box.Directory, hashes));
    }
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":999}")]
    [InlineData("{\"fans\":null}")]
    public void CorruptSettingsPreserveEvidenceAndReportHealth(string contents)
    {
        using var box = new Sandbox(); var path = box.File("settings.json"); File.WriteAllText(path, contents);
        var store = new JsonSettingsStore(path); var defaults = store.Load(); defaults.Validate();
        Assert.NotNull(store.HealthWarning);
        Assert.Equal(contents, File.ReadAllText(Assert.Single(Directory.GetFiles(box.Directory, "*.bak"))));
        store.Save(defaults); Assert.Equal(ServiceSettings.CurrentSchemaVersion, store.Load().SchemaVersion); Assert.Null(store.HealthWarning);
    }
    [Fact]
    public void UnversionedSettingsMigrateWithoutLosingValues()
    {
        using var box = new Sandbox(); var path = box.File("settings.json");
        File.WriteAllText(path, "{\"lighting\":{\"color\":\"#123456\"},\"routerHost\":\"192.0.2.1\"}");
        var settings = new JsonSettingsStore(path).Load();
        Assert.Equal("#123456", settings.Lighting.Color); Assert.Equal("192.0.2.1", settings.RouterHost);
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("schemaVersion").GetInt32());
    }
    private sealed class TestModule(string name, Func<CancellationToken, Task> run, Func<TimeSpan, CancellationToken, Task>? delay = null)
        : HostedModule(name, _ => { }, delay)
    { protected override Task ExecuteAsync(CancellationToken token) => run(token); }
    private sealed class BrokenRgb : IRgbBackend
    {
        public Task SetColorsAsync(uint controller, IReadOnlyList<RgbColor> colors, CancellationToken token = default) => throw new NotSupportedException("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task UnexpectedRgbFailureCannotStopSensorFansOrLamps()
    {
        var controls = new FakeFanControlPort(); var lamp = new FakeLampClient();
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), controls, new FakeHealthProbe(), new BrokenRgb(), lamp, new FakeRouterBackend());
        using var stop = new CancellationTokenSource(); var running = runtime.RunAsync(stop.Token);
        Assert.False(running.IsCompleted); Assert.Equal(45, runtime.GetState(new()).System.CpuTemp);
        Assert.Contains(runtime.GetState(new()).Health.Warnings, w => w.Contains("rgb module failed"));
        Assert.Equal(100, controls.Speeds[7]); Assert.True(lamp.Power);
        stop.Cancel(); await running; Assert.All(controls.Headers, h => Assert.Contains(h, controls.Restored));
    }
    [Fact]
    public async Task FailedModuleBacksOffWhileSiblingKeepsRunning()
    {
        using var stop = new CancellationTokenSource(); var delays = new List<TimeSpan>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthy = new TestModule("fans", async token => { ready.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        var healthyTask = healthy.RunAsync(stop.Token); await ready.Task;
        var failed = new TestModule("rgb", _ => throw new IOException("fixture"), (delay, _) =>
        { delays.Add(delay); Assert.True(healthy.Health.Running); if (delays.Count == 4) stop.Cancel(); return Task.CompletedTask; }); // Check first: cancelling stops the sibling.
        await failed.RunAsync(stop.Token); await healthyTask;
        Assert.Equal(new[] { 1, 2, 4, 8 }, delays.Select(d => (int)d.TotalSeconds)); Assert.Equal(4, failed.Health.Failures);
    }
    [Fact]
    public void LogsRollRetainSevenDaysCapSizeAndOmitExceptionText()
    {
        using var box = new Sandbox(); var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        File.WriteAllText(box.File("controlspawn-2026-10-22.jsonl"), "expired");
        File.WriteAllText(box.File("controlspawn-2026-10-23.jsonl"), "retained");
        var errors = new List<string>(); using var provider = new RollingLogProvider(box.Directory, () => now, errors.Add);
        var logger = provider.CreateLogger("fans"); using var scope = logger.BeginScope("fans");
        logger.LogError(new IOException("fixture-secret"), "BIOS handoff failed");
        var path = box.File("controlspawn-2026-10-29.jsonl"); var text = File.ReadAllText(path);
        Assert.Contains("fans", text); Assert.DoesNotContain("fixture-secret", text); Assert.Single(errors);
        Assert.False(File.Exists(box.File("controlspawn-2026-10-22.jsonl"))); Assert.True(File.Exists(box.File("controlspawn-2026-10-23.jsonl")));
        using (var file = File.OpenWrite(path)) file.SetLength(10 * 1024 * 1024);
        logger.LogInformation("At cap"); Assert.Equal(10 * 1024 * 1024, new FileInfo(path).Length);
        now = now.AddDays(1); logger.LogInformation("Next day"); Assert.True(File.Exists(box.File("controlspawn-2026-10-30.jsonl")));
    }

    [Fact]
    public void ADevelopmentRunTrustsOnlyTheAppBuiltBesideIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "controlspawn-dev-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = Directory.CreateDirectory(Path.Combine(root, "src", "ControlSpawn.Service", "bin", "Release", "net10.0-windows")).FullName;
            Assert.Null(PipeClientVerifier.DevAppBesideBuild(service));   // nothing built beside it yet
            var appDir = Directory.CreateDirectory(Path.Combine(root, "src", "ControlSpawn.App", "bin", "Release", "net10.0-windows")).FullName;
            File.WriteAllText(Path.Combine(appDir, "Prism.App.exe"), "");   // a stale pre-1.0.3 build is not trusted
            Assert.Null(PipeClientVerifier.DevAppBesideBuild(service));
            File.WriteAllText(Path.Combine(appDir, "ControlSpawn.exe"), "");
            Assert.Equal(Path.Combine(appDir, "ControlSpawn.exe"), PipeClientVerifier.DevAppBesideBuild(service + Path.DirectorySeparatorChar));
            Assert.Null(PipeClientVerifier.DevAppBesideBuild(Directory.CreateDirectory(Path.Combine(root, "Program Files", "Control Spawn")).FullName));   // installed
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnInstalledServiceLooksForTheAppInTheAppFolderBesideIt()
    {
        var root = Path.Combine("C:" + Path.DirectorySeparatorChar, "Program Files", "Control Spawn");
        var expected = Path.GetFullPath(Path.Combine(root, "app"));
        Assert.Equal(expected, PipeClientVerifier.InstalledAppDirectory(Path.Combine(root, "service")));
        Assert.Equal(expected, PipeClientVerifier.InstalledAppDirectory(Path.Combine(root, "service") + Path.DirectorySeparatorChar));
    }
}
