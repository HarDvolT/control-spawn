using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ControlSpawn.Lighting;

/// <summary>Optional, user-initiated cloud session. No files, logs, stored credentials or startup requests.</summary>
public sealed class XiaomiTokenProvider(Func<CookieContainer, HttpMessageHandler>? handlerFactory = null) : IXiaomiTokenProvider
{
    /// <summary>Devices live on the server of the region picked in Mi Home, which few people remember; "auto" tries each.</summary>
    public const string AutoRegion = "auto";
    private static readonly string[] Regions = ["de", "sg", "us", "cn", "ru", "in", "tw", "i2"];
    public async Task<XiaomiSignInResult> SignInAsync(string username, string password, string region, CancellationToken token)
    {
        if (region != AutoRegion && !Regions.Contains(region)) throw new ArgumentException("Invalid region.");
        var cookies = new CookieContainer();
        string device = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        foreach (var host in new[] { ".mi.com", ".xiaomi.com" })
        { cookies.Add(new Cookie("sdkVersion", "accountsdk-18.8.15", "/", host)); cookies.Add(new Cookie("deviceId", device, "/", host)); }
        using var http = new HttpClient(handlerFactory?.Invoke(cookies) ?? new HttpClientHandler { CookieContainer = cookies }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Android-7.1.1 APP/com.xiaomi.mihome APPV/10.5.201");
        cookies.Add(new Cookie("userId", Uri.EscapeDataString(username), "/", "account.xiaomi.com"));
        using var initial = Parse(await http.GetStringAsync("https://account.xiaomi.com/pass/serviceLogin?sid=xiaomiio&_json=true", token));
        var passwordBytes = Encoding.UTF8.GetBytes(password); string hash;
        try { hash = Convert.ToHexString(MD5.HashData(passwordBytes)); } finally { CryptographicOperations.ZeroMemory(passwordBytes); }
        var loginFields = new Dictionary<string, string> { ["sid"] = "xiaomiio", ["hash"] = hash, ["callback"] = "https://sts.api.io.mi.com/sts", ["qs"] = "%3Fsid%3Dxiaomiio%26_json%3Dtrue", ["user"] = username, ["_sign"] = initial.RootElement.GetProperty("_sign").GetString()!, ["_json"] = "true" };
        using var form = new FormUrlEncodedContent(loginFields);
        using var login = await http.PostAsync("https://account.xiaomi.com/pass/serviceLoginAuth2", form, token); login.EnsureSuccessStatusCode();
        using var auth = Parse(await login.Content.ReadAsStringAsync(token)); loginFields.Clear();
        if (auth.RootElement.TryGetProperty("notificationUrl", out var verification) && verification.ValueKind == JsonValueKind.String)
        {
            var url = new Uri(verification.GetString()!); if (url.Scheme != "https" || url.Host != "account.xiaomi.com") throw new IOException("Invalid verification URL.");
            return new([], url);
        }
        string security = auth.RootElement.GetProperty("ssecurity").GetString()!;
        string userId = auth.RootElement.GetProperty("userId").ToString();
        var location = new Uri(auth.RootElement.GetProperty("location").GetString()!);
        if (location.Scheme != "https" || !(location.Host.EndsWith(".mi.com", StringComparison.Ordinal) || location.Host.EndsWith(".xiaomi.com", StringComparison.Ordinal))) throw new IOException("Invalid sign-in redirect.");
        using var session = await http.GetAsync(location, token); session.EnsureSuccessStatusCode();
        string serviceToken = cookies.GetCookies(location)["serviceToken"]?.Value ?? throw new IOException("Cloud session unavailable.");
        string hostName = "";
        var call = token;
        async Task<JsonDocument> Query(string path, object data)
        {
            byte[] nonceBytes = RandomNumberGenerator.GetBytes(12); BinaryPrimitives.WriteInt32BigEndian(nonceBytes.AsSpan(8), (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60));
            string nonce = Convert.ToBase64String(nonceBytes); byte[] key = SHA256.HashData(Convert.FromBase64String(security).Concat(nonceBytes).ToArray());
            try
            {
                string keyText = Convert.ToBase64String(key);
                var fields = new Dictionary<string, string> { ["data"] = JsonSerializer.Serialize(data) };
                string Signature() => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes("POST&" + path + "&" + string.Join("&", fields.Select(p => p.Key + "=" + p.Value)) + "&" + keyText)));
                fields["rc4_hash__"] = Signature();
                foreach (var k in fields.Keys.ToArray()) fields[k] = Convert.ToBase64String(Rc4(key, Encoding.UTF8.GetBytes(fields[k])));
                string signature = Signature(); fields["signature"] = signature; fields["ssecurity"] = security; fields["_nonce"] = nonce;
                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{hostName}/app{path}") { Content = new FormUrlEncodedContent(fields) };
                request.Headers.Add("MIOT-ENCRYPT-ALGORITHM", "ENCRYPT-RC4"); request.Headers.Add("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
                using var response = await http.SendAsync(request, call); response.EnsureSuccessStatusCode();
                var decrypted = Rc4(key, Convert.FromBase64String(await response.Content.ReadAsStringAsync(call)));
                // JsonDocument can retain a byte buffer; copy into its owned representation before wiping ours.
                try { return JsonDocument.Parse(Encoding.UTF8.GetString(decrypted)); } finally { CryptographicOperations.ZeroMemory(decrypted); }
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        var records = new List<ExtractedToken>();
        foreach (var server in region == AutoRegion ? Regions : [region])
        {
            hostName = (server == "cn" ? "" : server + ".") + "api.io.mi.com";
            foreach (var pair in new[] { ("userId", userId), ("serviceToken", serviceToken), ("yetAnotherServiceToken", serviceToken), ("locale", "en_GB") }) cookies.Add(new Cookie(pair.Item1, pair.Item2, "/", hostName));
            // Searching every server: one that is slow or down is skipped after a while instead of failing the whole search.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (region == AutoRegion) limit.CancelAfter(TimeSpan.FromSeconds(12));
            call = limit.Token;
            try { await CollectAsync(); }
            catch (Exception e) when (region == AutoRegion && !token.IsCancellationRequested && e is HttpRequestException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException or OperationCanceledException) { continue; }
            if (records.Count > 0) break;
        }
        return new(records.GroupBy(r => r.DeviceId).Select(g => g.Last()).ToArray());
        async Task CollectAsync()
        {
            using var homes = await Query("/v2/homeroom/gethome", new { fg = true, fetch_share = true, fetch_share_dev = true, limit = 300, app_ver = 7 });
            if (!homes.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("homelist", out var homelist)) return;
            foreach (var home in homelist.EnumerateArray())
            {
                using var devices = await Query("/v2/home/home_device_list", new { home_owner = home.GetProperty("uid"), home_id = home.GetProperty("id"), limit = 200, get_split_device = true, support_smart_home = true });
                // Feed through the same strict fixture-tested parser; BLE/non-miIO records have no 32-hex token.
                var list = devices.RootElement.GetProperty("result").GetProperty("device_info").EnumerateArray()
                    .Where(d => d.TryGetProperty("token", out var t) && t.GetString() is { Length: 32 } && d.TryGetProperty("did", out var id) && uint.TryParse(id.ToString(), out _)).ToArray();
                records.AddRange(TokenExtractorParser.Parse(JsonSerializer.Serialize(list)));
            }
        }
    }
    public static JsonDocument Parse(string response) => JsonDocument.Parse(response.StartsWith("&&&START&&&", StringComparison.Ordinal) ? response[11..] : response);
    public static byte[] Rc4(byte[] key, byte[] bytes)
    {
        var state = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(); int j = 0;
        for (int i = 0; i < 256; i++) { j = (j + state[i] + key[i % key.Length]) & 255; (state[i], state[j]) = (state[j], state[i]); }
        int x = 0; j = 0; var result = new byte[bytes.Length];
        for (int k = -1024; k < bytes.Length; k++) { x = (x + 1) & 255; j = (j + state[x]) & 255; (state[x], state[j]) = (state[j], state[x]); byte value = state[(state[x] + state[j]) & 255]; if (k >= 0) result[k] = (byte)(bytes[k] ^ value); }
        CryptographicOperations.ZeroMemory(state); return result;
    }
}
