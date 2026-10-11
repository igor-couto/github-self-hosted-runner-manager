namespace RunnerRoom.Features.Access;

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
