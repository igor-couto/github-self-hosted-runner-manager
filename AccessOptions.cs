using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;

namespace RunnerRoom;

public sealed class AccessOptions
{
    public bool Enabled { get; set; }
    public bool RequireHttps { get; set; } = true;
    public string? PublicOrigin { get; set; }
    public int SessionHours { get; set; } = 8;
    public string[] TrustedProxies { get; set; } = [];
    public LocalAccount[] LocalUsers { get; set; } = [];
    public GitHubAccount[] GitHubUsers { get; set; } = [];
    public string? GitHubClientId { get; set; }
    public string? GitHubClientSecretFile { get; set; }
}
public sealed record LocalAccount(string Username, string PasswordHash, string Role);
public sealed record GitHubAccount(string Id, string Role);

internal sealed class AccessAccounts
{
    internal static readonly PasswordHasher<string> Hasher = new(Microsoft.Extensions.Options.Options.Create(
        new PasswordHasherOptions { IterationCount = 600000 }));
    private readonly AccessOptions options;
    private readonly string dummyHash = Hasher.HashPassword("dummy", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    internal AccessAccounts(AccessOptions options)
    {
        this.options = options;
        if (!options.Enabled) return;
        if (!Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https") ||
            origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0 || origin.UserInfo.Length > 0 ||
            options.RequireHttps && origin.Scheme != "https")
            throw new InvalidOperationException("Access.PublicOrigin must be an absolute origin without a path, query or credentials. Use HTTPS unless RequireHttps is explicitly false.");
        if (options.SessionHours is < 1 or > 24 || options.LocalUsers.Length + options.GitHubUsers.Length > 100)
            throw new InvalidOperationException("Access supports 1–24 hour sessions and up to 100 users.");
        if (options.LocalUsers.Any(u => u is null || !ValidUsername(u.Username) || !ValidRole(u.Role) || !ValidHash(u.PasswordHash)) ||
            options.LocalUsers.Select(u => u.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.LocalUsers.Length ||
            options.GitHubUsers.Any(u => u is null || !long.TryParse(u.Id, out var id) || id <= 0 || id.ToString() != u.Id || !ValidRole(u.Role)) ||
            options.GitHubUsers.Select(u => u.Id).Distinct().Count() != options.GitHubUsers.Length)
            throw new InvalidOperationException("Access users must have unique valid identities and a viewer or admin role.");
        if (!options.LocalUsers.Any(u => u.Role == "admin") && !options.GitHubUsers.Any(u => u.Role == "admin"))
            throw new InvalidOperationException("Authentication requires at least one explicitly configured administrator.");
        if (options.GitHubUsers.Length > 0 && (string.IsNullOrWhiteSpace(options.GitHubClientId) || string.IsNullOrWhiteSpace(options.GitHubClientSecretFile)))
            throw new InvalidOperationException("GitHub sign-in requires Access.GitHubClientId and Access.GitHubClientSecretFile.");
    }
    internal static bool ValidUsername(string? name) => name is not null && Regex.IsMatch(name, "^[a-zA-Z0-9_.-]{3,64}$");
    internal static bool ValidRole(string? role) => role is "viewer" or "admin";
    private static bool ValidHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length > 2048) return false;
        try
        {
            var bytes = Convert.FromBase64String(hash);
            if (bytes.Length < 61 || bytes[0] != 1) return false;
            var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4));
            var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4));
            var salt = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4));
            return prf is 1 or 2 && iterations is >= 600000 and <= 1000000 && salt is >= 16 and <= 64 &&
                bytes.Length - 13 - salt is >= 32 and <= 64;
        }
        catch (FormatException) { return false; }
    }
    internal string? Role(string subject) => subject.StartsWith("local:", StringComparison.Ordinal) ?
        options.LocalUsers.FirstOrDefault(u => "local:" + u.Username.ToLowerInvariant() == subject)?.Role :
        options.GitHubUsers.FirstOrDefault(u => "github:" + u.Id == subject)?.Role;
    private string Stamp(string subject)
    {
        var hash = subject.StartsWith("local:", StringComparison.Ordinal) ?
            options.LocalUsers.FirstOrDefault(u => "local:" + u.Username.ToLowerInvariant() == subject)?.PasswordHash : "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject + "|" + Role(subject) + "|" + hash)));
    }
    internal ClaimsPrincipal? Principal(string subject, string name)
    {
        var role = Role(subject); if (role is null) return null;
        return new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, subject), new(ClaimTypes.Name, name),
            new(ClaimTypes.Role, role), new("access_stamp", Stamp(subject))], "Session"));
    }
    internal bool ValidSession(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier) is { } subject &&
        Role(subject) is { } role && role == principal.FindFirstValue(ClaimTypes.Role) && principal.FindFirstValue("access_stamp") == Stamp(subject);
    internal ClaimsPrincipal? Login(string? username, string? password)
    {
        if (password is null || password.Length > 1024 || !ValidUsername(username)) return null;
        var user = options.LocalUsers.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        PasswordVerificationResult result;
        try { result = Hasher.VerifyHashedPassword(username!, user?.PasswordHash ?? dummyHash, password); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }
        return user is not null && result != PasswordVerificationResult.Failed ? Principal("local:" + user.Username.ToLowerInvariant(), user.Username) : null;
    }
    internal static void HashPasswordCommand()
    {
        string password;
        if (Console.IsInputRedirected) password = Console.ReadLine() ?? "";
        else
        {
            Console.Error.Write("Password (at least 12 characters): ");
            var text = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
                else if (!char.IsControl(key.KeyChar) && text.Length < 1024) text.Append(key.KeyChar);
            }
            Console.Error.WriteLine(); password = text.ToString();
        }
        if (password.Length is < 12 or > 1024) throw new InvalidOperationException("Use a password between 12 and 1024 characters.");
        Console.WriteLine(Hasher.HashPassword("account", password));
    }
}
