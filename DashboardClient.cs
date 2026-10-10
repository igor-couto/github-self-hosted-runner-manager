using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RunnerRoom;

// Shared by the console and optional tray client. Sessions remain in memory only.
public sealed class DashboardClient : IDisposable
{
    private readonly HttpClient client;
    private string? csrf;
    public Uri Address { get; }
    public DashboardClient(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use an HTTP(S) server origin, such as https://runners.example.com.");
        Address = uri;
        client = new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(12) };
    }
    public async Task<JsonElement> Session(CancellationToken cancellation = default)
    {
        var data = await Get("/api/access/session", cancellation);
        csrf = data.TryGetProperty("csrfToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        return data;
    }
    public async Task Login(string user, string password, CancellationToken cancellation = default)
    {
        if (Address.Scheme != "https" && !Address.IsLoopback) throw new InvalidOperationException("Remote sign-in requires HTTPS. Configure HTTPS on the server or use an SSH tunnel to localhost.");
        await Session(cancellation);
        await Post("/api/access/login", new { username = user, password }, cancellation);
        await Session(cancellation); // Antiforgery token must match the newly authenticated identity.
    }
    public Task<JsonElement> Get(string path, CancellationToken cancellation = default) => Send(HttpMethod.Get, path, null, cancellation);
    public Task<JsonElement> Post(string path, object body, CancellationToken cancellation = default) => Send(HttpMethod.Post, path, body, cancellation);
    private async Task<JsonElement> Send(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal) || path.Contains('\\')) throw new ArgumentException("Only dashboard API paths are supported.");
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (method != HttpMethod.Get && csrf is not null) request.Headers.Add("X-RR-CSRF", csrf);
        using var response = await client.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(response.StatusCode switch {
            HttpStatusCode.Unauthorized => "Sign-in required. Use --user and a local dashboard account.",
            HttpStatusCode.Forbidden => "Access denied. This operation may require an administrator.",
            _ => $"Dashboard returned HTTP {(int)response.StatusCode}. Check the address and server configuration."
        });
        using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation), cancellationToken: cancellation);
        return data.RootElement.Clone();
    }
    public void Dispose() => client.Dispose();
}
