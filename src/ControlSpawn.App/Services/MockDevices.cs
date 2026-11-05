using System.Text.Json;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Lighting;

namespace ControlSpawn.App.Services;

public sealed partial class MockControlSpawnClient
{
    private IReadOnlyList<DeviceState> registry = [];
    private DiscoveryOptions discoveryOptions = new();
    private int fryerStatus = 1, fryerMode;
    private bool fryerShake = true;
    private CookRecord[]? fryerHistory;
    private double fryerTemperature = 180, fryerTime = 20, fryerLeft;
    private (string Nonce, string Id, int Action, DateTimeOffset Expires)? pendingStart;
    private StateMessage Decorate(StateMessage state)
    {
        var now = state.Timestamp;
        var found = state.Lighting.Devices.Select(d => new DeviceState(d.Id, DeviceKind.RgbController, d.Name, "openrgb", now, now, DeviceStatus.Ready, ["sync", "case-map"], Configured: true)).ToList();
        found.AddRange((state.Lamps ?? []).Select(l => new DeviceState(l.Id!, DeviceKind.Lamp, l.Name!, "miio", now, now, DeviceStatus.Ready, ["color", "brightness"], Model: l.Model, DeviceId: l.DeviceId, Ip: l.Host, Configured: true)));
        found.AddRange(state.Fans.Where(f => f.Rpm > 0).Select(f => new DeviceState($"fan:{f.Header}", DeviceKind.FanHeader, f.Name, "lhm", now, now, DeviceStatus.Ready, f.IsPump ? ["rpm"] : ["rpm", "curve"], Configured: true)));
        found.Add(new("appliance:9001", DeviceKind.Appliance, "Kitchen air fryer", "miio", now, now, DeviceStatus.Ready, ["properties", "cancel", "confirmed-start"], Model: "careli.fryer.maf10a", DeviceId: 9001, Ip: "192.0.2.3", Configured: true));
        found.Add(new("lamp:470481367", DeviceKind.Lamp, "New Mi Home device", "miio", now, now, DeviceStatus.NeedsToken, [], DeviceId: 470481367, Ip: "192.0.2.4"));
        found.Add(new("network:286C07000001", DeviceKind.NetworkDevice, "Xiaomi device", "router", now, now, DeviceStatus.Ready, ["info"], Ip: "192.0.2.5", Mac: "28:6C:07:00:00:01", Vendor: "Xiaomi"));
        registry = DeviceRegistry.Observe(registry, found, now);
        var spec = MiotSpec.Load("careli.fryer.maf10a")!;
        var reply = JsonSerializer.SerializeToElement(new[] { (1, (double)fryerStatus), (2, 0d), (3, fryerTime), (4, fryerTemperature), (5, fryerLeft), (8, (double)fryerMode), (14, 1d) }.Select(p => (object)new { siid = 2, piid = p.Item1, code = 0, value = p.Item2 }).Append(new { siid = 2, piid = 15, code = 0, value = fryerShake }));
        // A few past cooks, so the Recent cooks card has something to show in mock mode. Fixed once, like real history.
        fryerHistory ??= [new(now.AddHours(-3.2), now.AddHours(-3), 200, 15, "Finished"), new(now.AddDays(-1).AddMinutes(-22), now.AddDays(-1), 180, 22, "Finished"),
            new(now.AddDays(-2).AddMinutes(-4), now.AddDays(-2), 160, 6, "Ended")];
        var appliance = MiotMapping.Map("appliance:9001", "Kitchen air fryer", spec, reply) with { History = fryerHistory };
        return DeviceNames.Apply(state with { Appliances = [appliance] }, registry, discoveryOptions);
    }
    private Response DeviceCommand(string command, JsonElement? element)
    {
        try
        {
            if (command == Commands.FindDevices) return new(0, true);
            var a = element ?? throw new ArgumentException();
            if (command == Commands.SetDiscoveryOptions) { discoveryOptions = a.GetProperty("options").Deserialize<DiscoveryOptions>(PipeContract.Json)!; return new(0, true); }
            if (command == Commands.AddToken)
            {
                var token = a.GetProperty("token").GetString() ?? "";
                if (token.Length != 32 || !token.All(Uri.IsHexDigit)) return new(0, false, "A token is 32 characters of 0-9 and a-f.");
                var id = a.TryGetProperty("id", out var picked) ? picked.GetString() : null;
                var target = registry.FirstOrDefault(d => d.Id == id) ?? throw new ArgumentException();
                bool fryer = target.Name.Contains("fryer", StringComparison.OrdinalIgnoreCase);
                registry = registry.Select(d => d.Id == id ? d with { Configured = true, Dismissed = false, Status = DeviceStatus.Ready, Kind = fryer ? DeviceKind.Appliance : DeviceKind.Lamp } : d).ToArray();
                return new(0, true);
            }
            if (command == Commands.ImportTokens)
            {
                var records = TokenExtractorParser.Parse(a.GetProperty("text").GetString()!);
                var selected = a.GetProperty("selected").EnumerateArray().Select(d => uint.Parse(d.ToString())).ToHashSet();
                foreach (var r in records.Where(r => selected.Contains(r.DeviceId)))
                {
                    string id = $"lamp:{r.DeviceId}";
                    if (r.Model.StartsWith("yeelink.light.", StringComparison.Ordinal))
                        extraLamps[id] = new(r.Model, true, true, 60, "#FF4D1A", "own", "miio", id, r.Name, r.DeviceId, r.Ip);
                    registry = DeviceRegistry.Observe(registry, [new(id, r.Model.Contains(".fryer.", StringComparison.Ordinal) ? DeviceKind.Appliance : DeviceKind.Lamp, r.Name, "miio", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DeviceStatus.Ready, [], Model: r.Model, DeviceId: r.DeviceId, Ip: r.Ip, Configured: true)], DateTimeOffset.UtcNow);
                }
                return new(0, true);
            }
            string deviceId = a.GetProperty("id").GetString()!;
            var device = registry.FirstOrDefault(d => d.Id == deviceId) ?? throw new ArgumentException();
            switch (command)
            {
                case Commands.RenameDevice:
                    var rename = DeviceRegistry.Rename(device, a.TryGetProperty("name", out var name) ? name.GetString() : null, a.TryGetProperty("reset", out var reset) && reset.GetBoolean());
                    registry = registry.Select(d => d.Id == deviceId ? rename : d).ToArray(); return new(0, true);
                case Commands.DismissDevice:
                    registry = registry.Select(d => d.Id == deviceId ? d with { Dismissed = true } : d).ToArray(); return new(0, true);
                case Commands.SetupDevice:
                    if (device.Status == DeviceStatus.NeedsToken) throw new ArgumentException();
                    registry = registry.Select(d => d.Id == deviceId ? d with { Configured = true } : d).ToArray(); return new(0, true);
            }
            if (deviceId != "appliance:9001") throw new ArgumentException();
            var spec = MiotSpec.Load(device.Model!)!;
            if (command == Commands.ApplianceProperties)
            {
                foreach (var value in a.GetProperty("values").EnumerateArray())
                {
                    var p = spec.Properties.Single(p => p.Siid == value.GetProperty("siid").GetInt32() && p.Piid == value.GetProperty("piid").GetInt32());
                    if (p.Name is not ("target-time" or "target-temperature" or "mode" or "turn-pot-config")) throw new ArgumentException();
                    if (p.Piid == 15) { fryerShake = value.GetProperty("value").GetBoolean(); continue; }
                    double n = p.Clamp(value.GetProperty("value")).GetDouble(); if (p.Piid == 3) fryerTime = n; if (p.Piid == 4) fryerTemperature = n; if (p.Piid == 8) fryerMode = (int)n;
                }
                pendingStart = null; return new(0, true);
            }
            int siid = a.GetProperty("siid").GetInt32(), aiid = a.GetProperty("aiid").GetInt32();
            var action = spec.Actions.Single(x => x.Siid == siid && x.Aiid == aiid);
            if (command == Commands.PrepareApplianceStart && a.GetProperty("origin").GetString() == "app" && action.Name is "start-cook" or "resume-cook")
            {
                string nonce = Guid.NewGuid().ToString("N"); pendingStart = (nonce, deviceId, aiid, DateTimeOffset.UtcNow.AddSeconds(30));
                return new(0, true, Data: JsonSerializer.SerializeToElement(new { confirmation = nonce }));
            }
            if (command == Commands.ApplianceAction && action.Name is "cancel-cooking" or "pause")
            { pendingStart = null; fryerStatus = action.Name == "pause" ? 2 : 1; return new(0, true); }
            if (command == Commands.ConfirmApplianceStart)
            {
                var pending = pendingStart; pendingStart = null;
                if (pending is not { } p || p.Id != deviceId || p.Action != aiid || p.Expires < DateTimeOffset.UtcNow || a.GetProperty("origin").GetString() != "app" || !a.GetProperty("confirmed").GetBoolean() || a.GetProperty("confirmation").GetString() != p.Nonce) throw new ArgumentException();
                fryerStatus = 4; fryerLeft = fryerTime; return new(0, true);
            }
            throw new ArgumentException();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
        { return new(0, false, "Invalid device command or confirmation."); }
    }
}
