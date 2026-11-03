using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Prism.Lighting.Tests;

public sealed class XiaomiCloudFixtureTests
{
    private sealed class FixtureHandler(CookieContainer cookies, bool twoStep, string? devicesOn = null) : HttpMessageHandler
    {
        public int Calls;
        public List<string> Paths { get; } = [];
        public List<string> Hosts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; string path = request.RequestUri!.AbsolutePath; Paths.Add(path); Hosts.Add(request.RequestUri.Host);
            string response;
            if (path.EndsWith("serviceLogin", StringComparison.Ordinal)) response = "&&&START&&&{\"_sign\":\"fixture-sign\"}";
            else if (path.EndsWith("serviceLoginAuth2", StringComparison.Ordinal))
            {
                string form = await request.Content!.ReadAsStringAsync(token);
                Assert.DoesNotContain("fixture-password", form);
                response = twoStep ? "&&&START&&&{\"notificationUrl\":\"https://account.xiaomi.com/identity/authStart?context=recorded-fixture\"}"
                    : "&&&START&&&{\"ssecurity\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"userId\":\"42\",\"location\":\"https://sts.api.io.mi.com/sts\"}";
            }
            else if (path == "/sts")
            { cookies.Add(new Cookie("serviceToken", "fixture-session", "/", "sts.api.io.mi.com")); response = "ok"; }
            else
            {
                var fields = (await request.Content!.ReadAsStringAsync(token)).Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                var key = SHA256.HashData(new byte[16].Concat(Convert.FromBase64String(fields["_nonce"])).ToArray());
                var plain = devicesOn is not null && request.RequestUri.Host != devicesOn ? "{\"result\":{\"homelist\":[]}}"
                    : path.Contains("gethome", StringComparison.Ordinal) ? "{\"result\":{\"homelist\":[{\"uid\":42,\"id\":7}]}}"
                    : "{\"result\":{\"device_info\":[{\"name\":\"Fixture lamp\",\"did\":\"470481367\",\"localip\":\"192.0.2.1\",\"model\":\"yeelink.light.color5\",\"token\":\"0123456789abcdef0123456789abcdef\"}]}}";
                response = Convert.ToBase64String(XiaomiTokenProvider.Rc4(key, Encoding.UTF8.GetBytes(plain)));
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
    [Fact]
    public async Task OptionalSignInUsesOnlyInjectedFixtureHttpAndParsesResult()
    {
        FixtureHandler? handler = null;
        var provider = new XiaomiTokenProvider(cookies => handler = new(cookies, false));
        Assert.Null(handler); // Creating the provider is inert.
        var result = await provider.SignInAsync("fixture-user", "fixture-password", "sg", default);
        var device = Assert.Single(result.Devices); Assert.Equal(470481367u, device.DeviceId);
        Assert.Equal("yeelink.light.color5", device.Model); Assert.Null(result.VerificationUrl); Assert.Equal(5, handler!.Calls);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", JsonSerializer.Serialize(result));
    }
    [Fact]
    public async Task TwoStepReturnsVerificationUrlAndMakesNoDeviceListRequests()
    {
        FixtureHandler? handler = null; var provider = new XiaomiTokenProvider(cookies => handler = new(cookies, true));
        var result = await provider.SignInAsync("fixture-user", "fixture-password", "de", default);
        Assert.Empty(result.Devices); Assert.Equal("account.xiaomi.com", result.VerificationUrl!.Host); Assert.Equal(2, handler!.Calls);
    }
    /// <summary>Few people know which server Mi Home keeps their devices on; "auto" goes through them until one answers with devices.</summary>
    [Fact]
    public async Task AutoRegionFindsTheServerThatHasTheDevices()
    {
        FixtureHandler? handler = null;
        var provider = new XiaomiTokenProvider(cookies => handler = new(cookies, false, "us.api.io.mi.com"));
        var result = await provider.SignInAsync("fixture-user", "fixture-password", XiaomiTokenProvider.AutoRegion, default);
        Assert.Equal(470481367u, Assert.Single(result.Devices).DeviceId);
        Assert.Equal(["de.api.io.mi.com", "sg.api.io.mi.com", "us.api.io.mi.com", "us.api.io.mi.com"], handler!.Hosts.Where(h => h.EndsWith("api.io.mi.com", StringComparison.Ordinal) && !h.StartsWith("sts", StringComparison.Ordinal)));
    }
    [Fact]
    public void Rc4Drop1024MatchesRfc6229FiveByteKeyVector()
    {
        Assert.Equal("30ABBCC7C20B01609F23EE2D5F6BB7DF", Convert.ToHexString(XiaomiTokenProvider.Rc4([1, 2, 3, 4, 5], new byte[16])));
    }
}
