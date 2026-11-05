using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ControlSpawn.Router.Tests;

public class RatingTests
{
    [Fact]
    public void Boundaries()
    {
        Assert.Equal(Ratings.Excellent, Ratings.Rate(Metric.Rsrp, -80));
        Assert.Equal(Ratings.Good, Ratings.Rate(Metric.Rsrp, -80.5));
        Assert.Equal(Ratings.Good, Ratings.Rate(Metric.Rsrp, -90));
        Assert.Equal(Ratings.Fair, Ratings.Rate(Metric.Rsrp, -100));
        Assert.Equal(Ratings.Poor, Ratings.Rate(Metric.Rsrp, -101));
        Assert.Equal(Ratings.Excellent, Ratings.Rate(Metric.Sinr, 20));
        Assert.Equal(Ratings.Good, Ratings.Rate(Metric.Sinr, 13));
        Assert.Equal(Ratings.Fair, Ratings.Rate(Metric.Sinr, 0));
        Assert.Equal(Ratings.Poor, Ratings.Rate(Metric.Sinr, -0.5));
        Assert.Equal(Ratings.Excellent, Ratings.Rate(Metric.Rsrq, -10));
        Assert.Equal(Ratings.Good, Ratings.Rate(Metric.Rsrq, -15));
        Assert.Equal(Ratings.Fair, Ratings.Rate(Metric.Rsrq, -20));
        Assert.Equal(Ratings.Poor, Ratings.Rate(Metric.Rsrq, -21));
        Assert.Null(Ratings.Rate(Metric.Rsrp, null));
    }

    [Fact]
    public void ExampleResponse()
    {
        var summary = Ratings.Summarize(D.Example());
        Assert.True(summary.MainIs5G);
        Assert.Equal("5G NSA", summary.Network);
        Assert.Equal("n78", summary.Band);
        Assert.Equal(3, summary.Bars);

        // 5G: RSRP -97 Fair, SINR 8 Fair -> Fair
        Assert.Equal(Ratings.Fair, summary.Overall);
        Assert.Equal(Ratings.Fair, summary.Lte.Ratings[Metric.Rsrp]);
        Assert.Equal(Ratings.Good, summary.Lte.Ratings[Metric.Rsrq]);
        Assert.Equal(Ratings.Fair, summary.Lte.Ratings[Metric.Sinr]);
        Assert.Equal(Ratings.Fair, summary.Nr.Ratings[Metric.Rsrp]);
        Assert.Equal(Ratings.Good, summary.Nr.Ratings[Metric.Rsrq]);
        Assert.Equal(Ratings.Fair, summary.Nr.Ratings[Metric.Sinr]);
    }

    [Fact]
    public void FourGOnlyAndEmptyStrings()
    {
        var data = D.Example().With(
            ("network_type_str", "4G"),
            ("RSRP_5G", ""),
            ("RSRQ_5G", ""),
            ("SINR_5G", ""),
            ("currentband_5g", ""),
            ("SINR", "-2"));
        var summary = Ratings.Summarize(data);
        Assert.False(summary.MainIs5G);
        Assert.Equal("B1+B3", summary.Band);
        Assert.Equal(Ratings.Poor, summary.Overall); // RSRP -92 Fair, SINR -2 Poor
        Assert.Null(summary.Nr.Values[Metric.Rsrp]);
    }

    [Fact]
    public void NoData()
    {
        var summary = Ratings.Summarize(new Dictionary<string, object?>());
        Assert.Null(summary.Overall);
        Assert.Equal("No service", summary.Network);
    }
}

public class ClientTests
{
    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void LoginAndPoll()
    {
        var fake = new FakeRouter();
        var client = new RouterClient("secret", transport: fake);
        var data = client.GetSignal();
        Assert.Equal("-97", Raw.Str(data["RSRP_5G"]));

        var login = fake.Sent[1];
        Assert.Equal(Sha256Hex("tok123secret"), Raw.Str(login["passwd"]));
        Assert.Equal("1", Raw.Str(login["isSingleLogin"]));
        Assert.Equal("", Raw.Str(login["sessionId"]));
        Assert.Equal(new string('S', 64), Raw.Str(fake.Sent[2]["sessionId"]));

        client.GetSignal();
        Assert.Equal(1, fake.Logins); // session reused
    }

    [Fact]
    public void NoAuthRelogs()
    {
        var fake = new FakeRouter(signalReplies: new[]
        {
            (IReadOnlyDictionary<string, object?>)D.ExampleWithSuccess(),
            D.Map(("success", false), ("message", "NO_AUTH")),
        });
        var client = new RouterClient("secret", transport: fake);
        client.GetSignal();
        client.GetSignal();
        Assert.Equal(2, fake.Logins);
    }

    [Fact]
    public void LoginTimeoutRelogs()
    {
        var fake = new FakeRouter(signalReplies: new[]
        {
            (IReadOnlyDictionary<string, object?>)D.Map(("success", false), ("message", "LOGIN_TIMEOUT")),
        });
        var client = new RouterClient("secret", transport: fake, sessionId: "old");
        Assert.Equal("-97", Raw.Str(client.GetSignal()["RSRP_5G"]));
        Assert.Equal(1, fake.Logins);
    }

    [Fact]
    public void SavedSessionIsReused()
    {
        var fake = new FakeRouter();
        var client = new RouterClient("secret", transport: fake, sessionId: "saved");
        client.GetSignal();
        Assert.Equal(0, fake.Logins);
        Assert.Equal("saved", Raw.Str(fake.Sent[0]["sessionId"]));
    }

    [Fact]
    public void LoginFailFlag()
    {
        var client = new RouterClient(
            "secret",
            transport: new FakeRouter(loginReply: D.Map(("login_fail", "fail"), ("login_times", "1"))));
        var error = Assert.Throws<AuthError>(() => client.Login());
        Assert.False(error.AlreadyLoggedIn);
    }

    [Fact]
    public void ParseSkipsLeadingJunk()
    {
        var parsed = RouterJson.Parse("\uFEFFxx{\"success\": true}");
        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(parsed);
        Assert.True((bool)map["success"]!);
    }

    [Fact]
    public void WrongPassword()
    {
        var client = new RouterClient("wrong", transport: new FakeRouter());
        var error = Assert.Throws<AuthError>(() => client.GetSignal());
        Assert.False(error.AlreadyLoggedIn);
    }

    [Fact]
    public void AlreadyLogin()
    {
        var client = new RouterClient(
            "secret",
            transport: new FakeRouter(loginReply: D.Map(("success", false), ("message", "alreadyLogin"))));
        var error = Assert.Throws<AuthError>(() => client.GetSignal());
        Assert.True(error.AlreadyLoggedIn);
    }

    [Fact]
    public void OnlyAllowedCmdsAreSent()
    {
        var fake = new FakeRouter(signalReplies: new[]
        {
            (IReadOnlyDictionary<string, object?>)D.Map(("success", false), ("message", "NO_AUTH")),
        });
        var client = new RouterClient("secret", transport: fake);
        for (var i = 0; i < 3; i++)
        {
            client.GetSignal();
        }

        Assert.All(fake.Sent, body => Assert.Contains(Raw.Str(body["cmd"]), RouterClient.AllowedCmds));
        Assert.Equal(
            new[] { RouterClient.CmdGetToken, RouterClient.CmdLogin, RouterClient.CmdSignal, RouterClient.CmdDevices }
                .OrderBy(cmd => cmd, StringComparer.Ordinal),
            RouterClient.AllowedCmds.OrderBy(cmd => cmd, StringComparer.Ordinal));

        Assert.Throws<RouterPermissionError>(
            () => client.Call(D.Map(("cmd", "00000000-reboot"), ("method", "POST"))));
    }

    [Fact]
    public void DeviceListIsGetOnly()
    {
        var fake = new FakeRouter();
        var client = new RouterClient("secret", transport: fake);
        Assert.Null(client.GetDevices()); // no session yet: no request and no login
        Assert.Empty(fake.Sent);

        client.SessionId = new string('S', 64);
        foreach (var cmd in new[] { RouterClient.CmdDevices, RouterClient.CmdSignal })
        {
            Assert.Throws<RouterPermissionError>(
                () => client.Call(D.Map(("cmd", cmd), ("method", "POST"), ("sessionId", client.SessionId))));
        }

        Assert.Empty(fake.Sent);
    }

    [Fact]
    public void OwnSessionIdFallback()
    {
        var client = new RouterClient(
            "x",
            transport: new FakeRouter(loginReply: D.Map(("success", true))));
        var sessionId = client.Login();
        Assert.Equal(64, sessionId.Length);
        Assert.True(sessionId.All(Uri.IsHexDigit));
    }
}
