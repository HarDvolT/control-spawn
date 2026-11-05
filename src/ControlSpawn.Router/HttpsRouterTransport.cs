using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ControlSpawn.Router;

/// <summary>
/// The only HTTPS opener the port uses for the router. Certificate validation is switched off
/// for the router's self-signed certificate <em>only</em> for the configured router host, and
/// system proxies are ignored because the router is on the LAN.
/// </summary>
public sealed class HttpsRouterTransport : IRouterTransport, IDisposable
{
    private const int TimeoutSeconds = 4;

    private readonly HttpClient _http;
    private readonly string _host;
    private readonly string _url;

    public HttpsRouterTransport(string host, string path = "/cgi-bin/http.cgi")
    {
        _host = host;
        _url = $"https://{host}{path}";

        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            {
                if (errors == SslPolicyErrors.None)
                {
                    return true;
                }

                // Self-signed router certificate: accept it for the router host only.
                return string.Equals(request.RequestUri?.Host, _host, StringComparison.OrdinalIgnoreCase);
            },
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };
    }

    public string Url => _url;

    public object? Send(IReadOnlyDictionary<string, object?> body)
    {
        var json = JsonSerializer.Serialize(body);
        string text;
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = _http.PostAsync(_url, content).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                // Python's urllib raises HTTPError for 4xx/5xx, which the widget reports as
                // "Router unreachable".
                throw new RouterError($"Router unreachable: HTTP {(int)response.StatusCode}");
            }

            using var reader = new StreamReader(
                response.Content.ReadAsStream(),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or SocketException)
        {
            throw new RouterError($"Router unreachable: {Unwrap(e).GetType().Name}", e);
        }

        return RouterJson.Parse(text);
    }

    public void Dispose() => _http.Dispose();

    private static Exception Unwrap(Exception e)
    {
        var inner = e;
        while (inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        return inner;
    }
}
