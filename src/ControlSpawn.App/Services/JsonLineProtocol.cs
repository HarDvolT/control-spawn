using System.Text;
using System.Text.Json;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Services;

/// <summary>
/// JSON-lines framing for the service pipe: one UTF-8 JSON object per "\n"-terminated line.
/// Kept free of I/O so the framing rules are unit-testable.
/// </summary>
public static class JsonLineProtocol
{
    public const char Separator = '\n';

    public static string Serialize(Request request) =>
        JsonSerializer.Serialize(request, PipeContract.Json);

    public static string Serialize(Response response) =>
        JsonSerializer.Serialize(response, PipeContract.Json);

    public static string Serialize(StateMessage state) =>
        JsonSerializer.Serialize(state, PipeContract.Json);

    public static JsonElement? ToArgs(object? args) =>
        args is null ? null : JsonSerializer.SerializeToElement(args, PipeContract.Json);

    public static Request CreateRequest(int id, string command, object? args = null) =>
        new(id, command, ToArgs(args));

    /// <summary>Returns false for blank or malformed lines instead of throwing.</summary>
    public static bool TryParse(string? line, out StateMessage? state, out Response? response)
    {
        state = null;
        response = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var type = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

            if (string.Equals(type, "state", StringComparison.OrdinalIgnoreCase))
            {
                state = root.Deserialize<StateMessage>(PipeContract.Json);
                if (state is null || state.System is null || state.Fans is null || state.Lighting is null || state.Health is null
                    || state.System.CpuCoreLoads is null || state.System.DimmTemps is null || state.System.Disks is null
                    || state.Lighting.Devices is null || state.Health.Warnings is null
                    || state.Fans.Any(f => f is null) || state.System.Disks.Any(d => d is null)
                    || state.Lighting.Devices.Any(d => d is null)) { state = null; return false; }
                return true;
            }

            if (string.Equals(type, "response", StringComparison.OrdinalIgnoreCase) ||
                root.TryGetProperty("ok", out _))
            {
                response = root.Deserialize<Response>(PipeContract.Json);
                return response is not null;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static StateMessage? ParseState(string? line) =>
        TryParse(line, out var state, out _) ? state : null;

    public static Response? ParseResponse(string? line) =>
        TryParse(line, out _, out var response) ? response : null;
}

/// <summary>
/// Buffers arbitrary chunks from a stream and hands back complete lines. Handles messages split
/// across reads, several messages in one read, CRLF and blank lines.
/// </summary>
public sealed class JsonLineReader
{
    private readonly StringBuilder _buffer = new();

    public IReadOnlyList<string> Append(string? chunk)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(chunk)) return lines;
        _buffer.Append(chunk);

        while (true)
        {
            var text = _buffer.ToString();
            var index = text.IndexOf(JsonLineProtocol.Separator);
            if (index < 0) break;
            var line = text[..index].TrimEnd('\r');
            _buffer.Remove(0, index + 1);
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
        }
        return lines;
    }

    public IReadOnlyList<string> Flush()
    {
        var lines = new List<string>();
        var rest = _buffer.ToString().TrimEnd('\r');
        _buffer.Clear();
        if (!string.IsNullOrWhiteSpace(rest)) lines.Add(rest);
        return lines;
    }

    public string Pending => _buffer.ToString();
}
