using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ControlSpawn.Lighting;

/// <summary>Short-lived secret-bearing import record; never serialize or log the token.</summary>
public sealed class ExtractedToken
{
    public string Name { get; init; } = "Device";
    public uint DeviceId { get; init; }
    public string? Ip { get; init; }
    public string Model { get; init; } = "";
    [JsonIgnore] public string Token { get; init; } = "";
    public string MaskedToken => "••••••••••••••••••••••••••••••••";
    public override string ToString() => $"{Name} ({DeviceId})";
}

public static class TokenExtractorParser
{
    public static IReadOnlyList<ExtractedToken> Parse(string input)
    {
        if (input.Length > 4_000_000) throw new ArgumentException("Import is too large.");
        try
        {
            var records = new List<Dictionary<string, string>>();
            if (input.TrimStart().StartsWith('[') || input.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(input);
                void Walk(JsonElement node)
                {
                    if (node.ValueKind == JsonValueKind.Object)
                    {
                        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in node.EnumerateObject())
                            if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) fields[p.Name] = p.Value.ToString();
                        if (fields.ContainsKey("token")) records.Add(fields);
                        else foreach (var p in node.EnumerateObject()) Walk(p.Value);
                    }
                    else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Walk(item);
                }
                Walk(doc.RootElement);
            }
            else
            {
                var lines = input.Replace("\r", "").Split('\n');
                if (lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) is { } header && header.Contains(',') && header.Contains("token", StringComparison.OrdinalIgnoreCase))
                {
                    var rows = Csv(input); var keys = rows[0];
                    records.AddRange(rows.Skip(1).Where(r => r.Count == keys.Count).Select(r => keys.Select((k, i) => (k, r[i])).ToDictionary(p => p.k.Trim(), p => p.Item2, StringComparer.OrdinalIgnoreCase)));
                }
                else
                {
                    var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines)
                    {
                        var match = Regex.Match(line, @"^\s*(NAME|ID|DID|IP|TOKEN|MODEL)\s*:\s*(.*?)\s*$", RegexOptions.IgnoreCase);
                        if (!match.Success) continue;
                        string key = match.Groups[1].Value, value = match.Groups[2].Value;
                        if (current.ContainsKey(key)) { records.Add(current); current = new(StringComparer.OrdinalIgnoreCase); }
                        current[key] = value;
                    }
                    if (current.Count > 0) records.Add(current);
                }
            }
            return records.Select(r => {
                string Get(params string[] keys) => keys.Select(k => r.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
                var idText = Get("id", "did", "deviceId"); var hex = idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (!uint.TryParse(hex ? idText[2..] : idText, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id is 0 or uint.MaxValue) throw new FormatException();
                var token = Get("token"); if (token.Length != 32 || !token.All(Uri.IsHexDigit)) throw new FormatException();
                var model = Get("model"); var name = Get("name");
                return new ExtractedToken { DeviceId = id, Token = token, Model = model, Ip = Get("ip", "localip"), Name = string.IsNullOrWhiteSpace(name) ? model : name };
            }).GroupBy(r => r.DeviceId).Select(g => g.Last()).ToArray();
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or InvalidOperationException)
        { throw new ArgumentException("Cannot parse token import. Expected NAME / ID / IP / TOKEN / MODEL records, JSON or CSV."); }
    }
    private static List<List<string>> Csv(string input)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '"') { if (quoted && i + 1 < input.Length && input[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && (c == ',' || c == '\n')) { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); if (c == '\n') { rows.Add(row); row = []; } }
            else cell.Append(c);
        }
        if (quoted) throw new FormatException();
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().TrimEnd('\r')); rows.Add(row); }
        return rows;
    }
}

public sealed record XiaomiSignInResult(IReadOnlyList<ExtractedToken> Devices, Uri? VerificationUrl = null);
public interface IXiaomiTokenProvider
{
    Task<XiaomiSignInResult> SignInAsync(string username, string password, string region, CancellationToken token);
}
