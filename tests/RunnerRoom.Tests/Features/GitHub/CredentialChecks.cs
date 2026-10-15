using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RunnerRoom.Features.GitHub;

namespace RunnerRoom.Tests.Features.GitHub;

internal static class CredentialChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-room-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var signingKey = RSA.Create(2048);
            var keyPath = Path.Combine(directory, "private-app-key.pem");
            await File.WriteAllTextAsync(keyPath, signingKey.ExportRSAPrivateKeyPem());
            var clock = new TestClock(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
            var issuedAt = clock.GetUtcNow();
            var requestCount = 0;
            using var handler = new Handler(async (request, cancellation) =>
            {
                requestCount++;
                check(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == "https://api.github.com/app/installations/987/access_tokens", "GitHub App credentials use only the fixed installation token endpoint.");
                check(request.Headers.Authorization?.Scheme == "Bearer", "Installation token requests authenticate with a bearer JWT.");
                check(await request.Content!.ReadAsStringAsync(cancellation) == "{}", "Token creation never sends private key material in the request body.");
                var parts = request.Headers.Authorization!.Parameter!.Split('.');
                check(parts.Length == 3 && !parts.Any(p => p.Contains('=')), "GitHub App JWTs use unpadded base64url encoding.");
                using var header = JsonDocument.Parse(Decode(parts[0]));
                using var payload = JsonDocument.Parse(Decode(parts[1]));
                check(header.RootElement.GetProperty("alg").GetString() == "RS256" && header.RootElement.GetProperty("typ").GetString() == "JWT", "GitHub App JWTs declare the RSA SHA-256 algorithm.");
                check(payload.RootElement.GetProperty("iss").GetString() == "123", "The JWT identifies the configured GitHub App.");
                check(payload.RootElement.GetProperty("iat").GetInt64() == clock.GetUtcNow().AddSeconds(-60).ToUnixTimeSeconds() &&
                    payload.RootElement.GetProperty("exp").GetInt64() == clock.GetUtcNow().AddMinutes(9).ToUnixTimeSeconds(), "JWT validity accommodates clock skew and stays within GitHub's ten-minute limit.");
                check(signingKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "GitHub App JWT signatures verify against the configured RSA key.");
                await Task.Yield();
                return TokenResponse("installation-test-token-" + requestCount, clock.GetUtcNow().AddHours(1));
            });
            using var http = new HttpClient(handler);
            GitHubOptions AppOptions() => new() { App = new() { AppId = 123, InstallationId = 987, PrivateKeyFile = keyPath } };
            var credentials = new GitHubCredentialProvider(http, AppOptions(), clock);
            var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => credentials.GetTokenAsync()));
            check(tokens.All(t => t == "installation-test-token-1") && requestCount == 1, "Concurrent API requests reuse one cached installation token.");
            clock.Now = issuedAt.AddMinutes(54);
            check(await credentials.GetTokenAsync() == "installation-test-token-1" && requestCount == 1, "An installation token remains cached while more than five minutes of validity remain.");
            clock.Now = issuedAt.AddMinutes(55);
            check(await credentials.GetTokenAsync() == "installation-test-token-2" && requestCount == 2, "Installation tokens renew before expiration at the five-minute boundary.");

            async Task Reject(GitHubOptions options, string description, string? secret = null, HttpClient? client = null)
            {
                try
                {
                    await new GitHubCredentialProvider(client ?? http, options, clock).GetTokenAsync();
                    check(false, description);
                }
                catch (GitHubCredentialException error)
                {
                    check(!error.Message.Contains(directory, StringComparison.Ordinal) &&
                        (secret is null || !error.Message.Contains(secret, StringComparison.Ordinal)) && error.InnerException is null,
                        description + " Error messages omit credentials, private paths, and inner exceptions.");
                }
            }
            var mixed = AppOptions(); mixed.Token = "private-pat-test-sentinel";
            await Reject(mixed, "PAT and App configurations cannot be combined.", mixed.Token);
            mixed = AppOptions(); mixed.TokenFile = Path.Combine(directory, "private-pat-file");
            await Reject(mixed, "Token-file and App configurations cannot be combined.");
            await Reject(new() { App = new() { AppId = 123 } }, "Incomplete App configurations fail safely.");
            var unreadable = AppOptions(); unreadable.App.PrivateKeyFile = Path.Combine(directory, "missing-key");
            await Reject(unreadable, "Missing App private-key files fail safely.");
            const string invalidKey = "private-invalid-key-sentinel";
            await File.WriteAllTextAsync(keyPath, invalidKey);
            await Reject(AppOptions(), "Malformed App private keys fail safely.", invalidKey);
            check(requestCount == 2, "Invalid local credential settings do not issue GitHub requests.");
            await File.WriteAllTextAsync(keyPath, signingKey.ExportRSAPrivateKeyPem());

            foreach (var json in new[] { "{}", "[]", "{\"token\":\"private-response-token\",\"expires_at\":\"invalid\"}", "{\"token\":42,\"expires_at\":null}", new string('x', 65537) })
            {
                using var invalidHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
                using var invalidHttp = new HttpClient(invalidHandler);
                await Reject(AppOptions(), "Invalid or oversized installation-token responses fail safely.", "private-response-token", invalidHttp);
            }
            using var expiringHandler = new Handler((_, _) => Task.FromResult(TokenResponse("private-expiring-token", clock.GetUtcNow().AddMinutes(5))));
            using var expiringHttp = new HttpClient(expiringHandler);
            await Reject(AppOptions(), "Near-expiry installation tokens are rejected.", "private-expiring-token", expiringHttp);
            using var deniedHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private-response-secret") }));
            using var deniedHttp = new HttpClient(deniedHandler);
            await Reject(AppOptions(), "GitHub authorization failures never expose response bodies.", "private-response-secret", deniedHttp);
            using var networkHandler = new Handler((_, _) => throw new HttpRequestException("private-network-detail"));
            using var networkHttp = new HttpClient(networkHandler);
            await Reject(AppOptions(), "Network failures return safe messages.", "private-network-detail", networkHttp);

            var patPath = Path.Combine(directory, "token.txt");
            await File.WriteAllTextAsync(patPath, "  private-file-token\n");
            var pat = new GitHubCredentialProvider(http, new() { TokenFile = patPath });
            check(await pat.GetTokenAsync() == "private-file-token" && pat.Configured && !pat.UsesApp, "PAT files remain supported without GitHub App authentication.");
            await File.WriteAllTextAsync(patPath, "private-rotated-token");
            check(await pat.GetTokenAsync() == "private-rotated-token", "Rotated PAT files are picked up on the next request.");
            await Reject(new() { TokenFile = Path.Combine(directory, "missing-pat") }, "Missing PAT files fail safely.");
            var unconfigured = new GitHubCredentialProvider(http, new());
            check(await unconfigured.GetTokenAsync() is null && !unconfigured.Configured && !unconfigured.UsesApp, "Absent credentials remain an explicit unconfigured state.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static HttpResponseMessage TokenResponse(string token, DateTimeOffset expiration) => new(HttpStatusCode.Created)
    { Content = new StringContent(JsonSerializer.Serialize(new { token, expires_at = expiration })) };
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
