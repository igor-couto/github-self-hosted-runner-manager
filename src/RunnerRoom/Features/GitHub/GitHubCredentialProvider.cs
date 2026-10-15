using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RunnerRoom.Features.Runners;

namespace RunnerRoom.Features.GitHub;

// Installation credentials stay on the server and are renewed before expiration.
public sealed class GitHubCredentialProvider(HttpClient http, GitHubOptions options, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1);
    private string? installationToken;
    private DateTimeOffset expires;
    public bool UsesApp => options.App.AppId != 0 || options.App.InstallationId != 0 || !string.IsNullOrWhiteSpace(options.App.PrivateKeyFile);
    public bool Configured => UsesApp || !string.IsNullOrWhiteSpace(options.Token) || !string.IsNullOrWhiteSpace(options.TokenFile);

    public async Task<string?> GetTokenAsync(CancellationToken cancellation = default)
    {
        if (!UsesApp)
        {
            var token = options.Token;
            if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(options.TokenFile))
            {
                token = LocalRunnerReader.ReadSmall(options.TokenFile, 8192);
                if (string.IsNullOrWhiteSpace(token)) throw new GitHubCredentialException("The GitHub token file could not be read. Check its path and service account permissions.");
            }
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
        if (!string.IsNullOrWhiteSpace(options.Token) || !string.IsNullOrWhiteSpace(options.TokenFile))
            throw new GitHubCredentialException("Configure either a GitHub token or a GitHub App installation, not both.");
        if (options.App.AppId <= 0 || options.App.InstallationId <= 0 || string.IsNullOrWhiteSpace(options.App.PrivateKeyFile))
            throw new GitHubCredentialException("GitHub App setup needs an App ID, installation ID, and private key file.");
        await gate.WaitAsync(cancellation);
        try
        {
            if (installationToken is not null && expires > clock.GetUtcNow().AddMinutes(5)) return installationToken;
            var pem = LocalRunnerReader.ReadSmall(options.App.PrivateKeyFile, 32768);
            if (string.IsNullOrWhiteSpace(pem)) throw new GitHubCredentialException("The GitHub App private key file could not be read. Check its path and service account permissions.");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            var now = clock.GetUtcNow();
            var unsigned = Base64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")) + "." +
                Base64(JsonSerializer.SerializeToUtf8Bytes(new { iat = now.AddSeconds(-60).ToUnixTimeSeconds(), exp = now.AddMinutes(9).ToUnixTimeSeconds(), iss = options.App.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            var jwt = unsigned + "." + Base64(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/app/installations/{options.App.InstallationId}/access_tokens");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.UserAgent.ParseAdd("RunnerRoom/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode) throw new GitHubCredentialException("GitHub could not authorize this App installation. Check its IDs, private key, installation access, and API limits.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var buffer = new byte[65537];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellation);
            if (count == buffer.Length) throw new JsonException();
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            var token = json.RootElement.GetProperty("token").GetString();
            var expiration = json.RootElement.GetProperty("expires_at").GetDateTimeOffset();
            if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || expiration <= now.AddMinutes(5)) throw new JsonException();
            installationToken = token; expires = expiration;
            return token;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or HttpRequestException or IOException or FormatException)
        { throw new GitHubCredentialException("GitHub App authentication failed. Check the private key, installation settings, and network connectivity."); }
        finally { gate.Release(); }
    }
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class GitHubCredentialException(string message) : Exception(message);
