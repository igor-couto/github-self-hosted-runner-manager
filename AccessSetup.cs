using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace RunnerRoom;

internal sealed class AdminAccess { }

internal static class AccessSetup
{
    internal static string StateDirectory(RunnerOptions options) => options.Monitoring.StateDirectory ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY") ??
        (OperatingSystem.IsLinux() ? "/var/lib/runner-room" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunnerRoom"));
    internal static bool IsAdmin(HttpContext context, RunnerOptions options) => !options.Access.Enabled || context.User.IsInRole("admin");
    internal static void Configure(WebApplicationBuilder builder, RunnerOptions options)
    {
        var access = options.Access;
        var accounts = new AccessAccounts(access);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton<AccessAudit>();
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-RR-CSRF";
            o.Cookie.Name = "RunnerRoom.Csrf";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = access.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/api/access/login", StringComparison.OrdinalIgnoreCase) ?
                RateLimitPartition.GetFixedWindowLimiter("login", _ => new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }) :
                RateLimitPartition.GetNoLimiter("other"));
            o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        if (!access.Enabled) return;
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
            o.ForwardLimit = 1;
            o.KnownNetworks.Clear(); o.KnownProxies.Clear();
            foreach (var address in access.TrustedProxies)
            {
                if (!IPAddress.TryParse(address, out var parsed)) throw new InvalidOperationException("Access.TrustedProxies must contain exact IP addresses.");
                o.KnownProxies.Add(parsed);
            }
            // With no proxy configured, do not enable forwarding at all (an empty allowlist trusts everyone).
            if (access.TrustedProxies.Length == 0) o.ForwardedHeaders = ForwardedHeaders.None;
        });
        var keys = Path.Combine(StateDirectory(options), "session-keys");
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else Directory.CreateDirectory(keys);
        builder.Services.AddDataProtection().SetApplicationName("RunnerRoom").PersistKeysToFileSystem(new DirectoryInfo(keys));
        var auth = builder.Services.AddAuthentication("Session").AddCookie("Session", o =>
        {
            o.Cookie.Name = "RunnerRoom.Session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = access.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(access.SessionHours); o.SlidingExpiration = false;
            o.Events.OnValidatePrincipal = async context =>
            {
                if (context.Principal is null || !accounts.ValidSession(context.Principal))
                { context.RejectPrincipal(); await context.HttpContext.SignOutAsync("Session"); }
            };
            o.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        if (access.GitHubUsers.Length == 0) return;
        var secret = LocalRunnerReader.ReadSmall(access.GitHubClientSecretFile!, 8192)?.Trim();
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("GitHub sign-in client secret file is unreadable or empty.");
        auth.AddOAuth("GitHub", o =>
        {
            o.SignInScheme = "Session";
            o.ClientId = access.GitHubClientId!; o.ClientSecret = secret;
            o.CallbackPath = "/signin-github";
            o.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            o.TokenEndpoint = "https://github.com/login/oauth/access_token";
            o.UserInformationEndpoint = "https://api.github.com/user";
            o.Scope.Clear(); // Public identity only. Login tokens never become runner-management tokens.
            o.UsePkce = true; o.SaveTokens = false;
            o.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(5);
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = access.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.BackchannelTimeout = TimeSpan.FromSeconds(10);
            o.BackchannelHttpHandler = new HttpClientHandler { AllowAutoRedirect = false };
            o.Events.OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                request.Headers.UserAgent.ParseAdd("RunnerRoom/1.0");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await context.Backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
                if (!response.IsSuccessStatusCode) throw new AuthenticationFailureException("GitHub identity could not be verified.");
                await using var stream = await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);
                var bytes = new byte[65537];
                var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, context.HttpContext.RequestAborted);
                if (length == bytes.Length) throw new AuthenticationFailureException("GitHub identity response is too large.");
                using var json = JsonDocument.Parse(bytes.AsMemory(0, length));
                context.Principal = GitHubIdentity(accounts, json.RootElement) ?? throw new AuthenticationFailureException("GitHub account is not allowed.");
                context.HttpContext.RequestServices.GetRequiredService<AccessAudit>().Add(context.HttpContext, "login_succeeded", context.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
            };
            o.Events.OnRemoteFailure = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<AccessAudit>().Add(context.HttpContext, "github_login_failed", "github");
                context.HandleResponse(); context.Response.Redirect("/login.html?error=github"); return Task.CompletedTask;
            };
        });
    }
    internal static ClaimsPrincipal? GitHubIdentity(AccessAccounts accounts, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number <= 0 ||
            !root.TryGetProperty("login", out var login) || login.ValueKind != JsonValueKind.String) return null;
        return accounts.Principal("github:" + number, LocalRunnerReader.Text(login.GetString()) ?? "GitHub user");
    }
    internal static void Use(WebApplication app, RunnerOptions options)
    {
        if (options.Access.Enabled)
        {
            app.UseForwardedHeaders();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/healthz") { await next(); return; }
                var origin = new Uri(options.Access.PublicOrigin!);
                if (options.Access.RequireHttps && !context.Request.IsHttps ||
                    !string.Equals(context.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase))
                { context.Response.StatusCode = 400; await context.Response.WriteAsync("Use the configured dashboard origin and HTTPS setting."); return; }
                await next();
            });
            app.UseAuthentication();
        }
        app.UseRouting(); app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (!options.Access.Enabled) { await next(); return; }
            var path = context.Request.Path.Value is { Length: > 1 } value ? new PathString(value.TrimEnd('/')) : context.Request.Path;
            var publicApi = path == "/api/access/session" || path == "/api/access/login";
            if (!publicApi && path.StartsWithSegments("/api") && context.User.Identity?.IsAuthenticated != true)
            { context.Response.StatusCode = 401; return; }
            if ((path == "/" || path == "/index.html") && context.User.Identity?.IsAuthenticated != true)
            { context.Response.Redirect("/login.html"); return; }
            if (path.StartsWithSegments("/api") && context.Request.Method != "GET" && context.Request.Method != "HEAD")
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { context.Response.StatusCode = 403; return; }
            }
            var admin = context.GetEndpoint()?.Metadata.GetMetadata<AdminAccess>() is not null;
            if (admin && !context.User.IsInRole("admin"))
            {
                context.RequestServices.GetRequiredService<AccessAudit>().Add(context, "access_denied");
                context.Response.StatusCode = 403; return;
            }
            await next();
        });
    }
    private sealed record LoginRequest(string? Username, string? Password);
    internal static void Map(WebApplication app, RunnerOptions options)
    {
        app.MapGet("/api/access/session", (HttpContext context, IAntiforgery csrf) => Results.Ok(new
        {
            enabled = options.Access.Enabled, authenticated = context.User.Identity?.IsAuthenticated == true,
            name = context.User.Identity?.Name, role = !options.Access.Enabled ? "admin" : context.User.FindFirstValue(ClaimTypes.Role),
            canAdmin = IsAdmin(context, options), localLogin = options.Access.Enabled && options.Access.LocalUsers.Length > 0,
            githubLogin = options.Access.Enabled && options.Access.GitHubUsers.Length > 0,
            csrfToken = options.Access.Enabled ? csrf.GetAndStoreTokens(context).RequestToken : null
        }));
        app.MapPost("/api/access/login", async (HttpContext context, AccessAccounts accounts, AccessAudit audit) =>
        {
            if (!options.Access.Enabled) return Results.NotFound();
            if (!context.Request.HasJsonContentType()) return Results.BadRequest();
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 8192;
            LoginRequest? input;
            try { input = await context.Request.ReadFromJsonAsync<LoginRequest>(); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException) { return Results.BadRequest(); }
            var principal = accounts.Login(input?.Username, input?.Password);
            if (principal is null) { audit.Add(context, "login_failed", "local"); return Results.Unauthorized(); }
            await context.SignInAsync("Session", principal, new AuthenticationProperties { IsPersistent = false });
            audit.Add(context, "login_succeeded", principal.FindFirstValue(ClaimTypes.NameIdentifier));
            return Results.Ok(new { message = "Signed in." });
        }).RequireRateLimiting("login");
        app.MapPost("/api/access/logout", async (HttpContext context, AccessAudit audit) =>
        {
            if (options.Access.Enabled) { audit.Add(context, "logout"); await context.SignOutAsync("Session"); }
            return Results.Ok();
        });
        app.MapGet("/auth/github", () => options.Access.Enabled && options.Access.GitHubUsers.Length > 0 ?
            Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, ["GitHub"]) : Results.NotFound());
        app.MapGet("/api/access/audit", (AccessAudit audit) => Results.Ok(new { entries = audit.Recent(), warning = audit.Warning })).WithMetadata(new AdminAccess());
        app.MapGet("/api/integration", async (RunnerMonitor monitor) =>
        {
            var snapshot = await monitor.GetSnapshotAsync();
            return Results.Ok(new {
                accessEnabled = options.Access.Enabled, sessionHours = options.Access.SessionHours,
                localUsers = options.Access.LocalUsers.Select(u => new { name = u.Username, role = u.Role }),
                githubUsers = options.Access.GitHubUsers.Select(u => new { id = u.Id, role = u.Role }),
                githubSignIn = options.Access.Enabled && options.Access.GitHubUsers.Length > 0,
                credentialConfigured = !string.IsNullOrWhiteSpace(options.GitHub.Token) || !string.IsNullOrWhiteSpace(options.GitHub.TokenFile),
                runners = snapshot.Runners.Select(r => new { r.DisplayName, r.Repository, r.Organization, r.GitHubUrl, r.AgentId, r.GitHub }),
                snapshot.CheckedAt
            });
        }).WithMetadata(new AdminAccess());
    }
}
