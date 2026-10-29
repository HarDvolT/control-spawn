using System.Security.Cryptography;
using System.Text;

namespace Prism.Router.Tests;

/// <summary>Stands in for the HTTPS transport and records every body sent.</summary>
internal sealed class FakeRouter : IRouterTransport
{
    private readonly Queue<IReadOnlyDictionary<string, object?>> _signalReplies;
    private readonly IReadOnlyDictionary<string, object?>? _loginReply;

    public FakeRouter(
        string password = "secret",
        IEnumerable<IReadOnlyDictionary<string, object?>>? signalReplies = null,
        IReadOnlyDictionary<string, object?>? loginReply = null)
    {
        Password = password;
        _signalReplies = new Queue<IReadOnlyDictionary<string, object?>>(
            signalReplies ?? Array.Empty<IReadOnlyDictionary<string, object?>>());
        _loginReply = loginReply;
    }

    public string Password { get; }

    public List<IReadOnlyDictionary<string, object?>> Sent { get; } = new();

    public int Logins { get; private set; }

    public object? Send(IReadOnlyDictionary<string, object?> body)
    {
        Sent.Add(body);
        var cmd = Raw.Str(body.GetValueOrDefault("cmd"));

        if (cmd == RouterClient.CmdGetToken)
        {
            return D.Map(("token", "tok123"));
        }

        if (cmd == RouterClient.CmdLogin)
        {
            Logins++;
            if (_loginReply is not null)
            {
                return _loginReply;
            }

            var ok = Raw.Str(body.GetValueOrDefault("passwd")) == Sha256Hex("tok123" + Password);
            return ok
                ? D.Map(("success", true), ("sessionId", new string('S', 64)))
                : D.Map(("success", false), ("message", "PASSWD_ERR"));
        }

        if (cmd == RouterClient.CmdSignal)
        {
            return _signalReplies.Count > 0 ? _signalReplies.Dequeue() : D.ExampleWithSuccess();
        }

        throw new InvalidOperationException("unexpected cmd");
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
