using System.Security.Cryptography;
using System.Text;

namespace ControlSpawn.Router;

/// <summary>
/// Read-only client for the ZLT X17M 5G router web API.
///
/// Only four API commands are ever sent: get token, login, read signal, and read the connected
/// devices list. Every request goes through <see cref="Call"/>, which refuses any other cmd,
/// because other cmds on this router can reboot it or change settings.
/// </summary>
public sealed class RouterClient
{
    public const string CmdGetToken = "3830c61a-620d-47da-ae47-33d8401401c4";
    public const string CmdLogin = "d2aa9843-494b-4947-9621-a46ec652ecd9";
    public const string CmdSignal = "89af35c9-b448-4fc7-a477-828a3e9467f8";

    /// <summary>
    /// Read-only connected-devices list (allowed by the owner on 2026-10-28). The router web UI's
    /// "Connected devices" page reads it with request_get, i.e. method GET; it returns
    /// dhcp_list_info (hostname, ip, ipv6, mac, interface) and wlan24g/5g_wifi_info. That page's
    /// "Block" button uses a different cmd (23, POST), never sent here.
    /// </summary>
    public const string CmdDevices = "5332f5ee-5be9-4843-b85f-1b251aa5f4ff";

    public static readonly IReadOnlySet<string> AllowedCmds = new HashSet<string>(StringComparer.Ordinal)
    {
        CmdGetToken, CmdLogin, CmdSignal, CmdDevices,
    };

    public static readonly IReadOnlySet<string> GetOnlyCmds = new HashSet<string>(StringComparer.Ordinal)
    {
        CmdSignal, CmdDevices,
    };

    public const string DefaultHost = "192.168.0.1";
    public const int TimeoutSeconds = 4;

    private readonly string _password;
    private readonly IRouterTransport _send;

    public RouterClient(
        string password,
        string host = DefaultHost,
        string username = "admin",
        IRouterTransport? transport = null,
        string sessionId = "")
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("password is empty", nameof(password));
        }

        Host = host;
        Url = $"https://{host}/cgi-bin/http.cgi";
        Username = username;
        _password = password;

        // A session saved from the last run is tried first: the router allows only one login and
        // keeps a closed widget's session alive for a while, so logging in again would fail.
        SessionId = sessionId ?? "";
        _send = transport ?? new HttpsRouterTransport(host);
    }

    public string Host { get; }

    public string Url { get; }

    public string Username { get; }

    /// <summary>The router's session id; pass the saved one back in to survive a restart.</summary>
    public string SessionId { get; set; }

    public string Login()
    {
        var resp = Call(new Dictionary<string, object?>
        {
            ["cmd"] = CmdGetToken,
            ["method"] = "GET",
            ["sessionId"] = "",
        });

        if (!resp.TryGetValue("token", out var tokenValue) || !Raw.Truthy(tokenValue))
        {
            throw new RouterError("No token in router reply");
        }

        var token = Raw.Str(tokenValue);

        // Our own sessionId, kept in case the login reply does not return one.
        var ownSid = Md5HexRandom() + Md5HexRandom();
        var passwd = Sha256Hex(token + _password);

        resp = Call(new Dictionary<string, object?>
        {
            ["cmd"] = CmdLogin,
            ["method"] = "POST",
            ["username"] = Username,
            ["passwd"] = passwd,
            ["token"] = token,
            ["sessionId"] = "",
            ["isSingleLogin"] = "1",
        });

        if (IsAlreadyLogin(resp))
        {
            throw new AuthError("Router is logged in elsewhere", alreadyLoggedIn: true);
        }

        if (Raw.IsFalse(resp.GetValueOrDefault("success"))
            || IsLoginFail(resp.GetValueOrDefault("login_fail"))
            || IsLoginFail(resp.GetValueOrDefault("login_fail2")))
        {
            var message = Raw.Str(resp.GetValueOrDefault("message"));
            throw new AuthError($"Login refused: {(message.Length > 0 ? message : "unknown reason")}");
        }

        SessionId = Raw.Truthy(resp.GetValueOrDefault("sessionId"))
            ? Raw.Str(resp.GetValueOrDefault("sessionId"))
            : ownSid;
        return SessionId;
    }

    /// <summary>Returns the signal map, logging in first (or again) when needed.</summary>
    public IReadOnlyDictionary<string, object?> GetSignal()
    {
        if (SessionId.Length == 0)
        {
            Login();
        }

        var resp = Call(SignalBody());
        if (IsSessionExpired(resp) || IsAlreadyLogin(resp))
        {
            SessionId = "";
            Login();
            resp = Call(SignalBody());
            if (IsSessionExpired(resp))
            {
                SessionId = "";
                throw new AuthError("Session rejected right after login", alreadyLoggedIn: true);
            }
        }

        if (Raw.IsFalse(resp.GetValueOrDefault("success")))
        {
            throw new RouterError($"Router error: {Raw.Str(resp.GetValueOrDefault("message"))}");
        }

        return resp;
    }

    /// <summary>
    /// Connected-devices list, or null when there is no valid session. Never logs in by itself,
    /// so it cannot fight the signal poll for the router's single login.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? GetDevices()
    {
        if (SessionId.Length == 0)
        {
            return null;
        }

        var resp = Call(new Dictionary<string, object?>
        {
            ["cmd"] = CmdDevices,
            ["method"] = "GET",
            ["sessionId"] = SessionId,
        });

        if (IsSessionExpired(resp) || Raw.IsFalse(resp.GetValueOrDefault("success")))
        {
            return null;
        }

        return resp;
    }

    /// <summary>
    /// The single guarded entry point. Refuses any cmd outside the allow-list, and sends the two
    /// read commands with method GET only.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Call(IReadOnlyDictionary<string, object?> body)
    {
        body.TryGetValue("cmd", out var cmdValue);
        var cmd = cmdValue as string;
        if (cmd is null || !AllowedCmds.Contains(cmd))
        {
            throw new RouterPermissionError(
                $"Refusing to send non read-only cmd {(cmdValue is null ? "None" : "'" + Raw.StrOf(cmdValue) + "'")}");
        }

        body.TryGetValue("method", out var methodValue);
        if (GetOnlyCmds.Contains(cmd) && !(methodValue is string method && method == "GET"))
        {
            throw new RouterPermissionError("Read commands are only ever sent with method GET");
        }

        var resp = _send.Send(body);
        if (resp is not IReadOnlyDictionary<string, object?> map)
        {
            throw new RouterError("Unexpected router reply");
        }

        return map;
    }

    /// <summary>Python <c>_is_already_login</c>: the reply mentions "alreadyLogin"/"already_login".</summary>
    public static bool IsAlreadyLogin(object? resp)
    {
        return ContainsMarker(resp, "alreadylogin") || ContainsMarker(resp, "already_login");
    }

    private static bool ContainsMarker(object? value, string marker)
    {
        switch (value)
        {
            case string s:
                return s.ToLowerInvariant().Contains(marker, StringComparison.Ordinal);
            case IReadOnlyDictionary<string, object?> map:
                foreach (var (key, item) in map)
                {
                    if (key.ToLowerInvariant().Contains(marker, StringComparison.Ordinal)
                        || ContainsMarker(item, marker))
                    {
                        return true;
                    }
                }

                return false;
            case IEnumerable<object?> list:
                return list.Any(item => ContainsMarker(item, marker));
            default:
                return false;
        }
    }

    /// <summary>Python <c>_session_expired</c>: the reply's message is NO_AUTH or LOGIN_TIMEOUT.</summary>
    public static bool IsSessionExpired(IReadOnlyDictionary<string, object?> resp)
    {
        var message = Raw.Str(resp.GetValueOrDefault("message"));
        return message is "NO_AUTH" or "LOGIN_TIMEOUT";
    }

    private Dictionary<string, object?> SignalBody() => new()
    {
        ["cmd"] = CmdSignal,
        ["method"] = "GET",
        ["sessionId"] = SessionId,
    };

    private static bool IsLoginFail(object? value) => value is string text && text == "fail";

    private static string Md5HexRandom()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    }

    private static string Sha256Hex(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
