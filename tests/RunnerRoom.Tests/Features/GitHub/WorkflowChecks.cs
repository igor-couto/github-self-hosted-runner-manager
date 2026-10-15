using System.Security.Cryptography;
using System.Text;
using RunnerRoom.Features.GitHub;

namespace RunnerRoom.Tests.Features.GitHub;
internal static class WorkflowChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var scan = GitHubWorkflowScanner.Parse("build.yml", "name: Build\njobs:\n  pi:\n    runs-on: [self-hosted, ARM64]\n  pool:\n    runs-on:\n      group: home\n      labels: linux\n  matrix:\n    runs-on: ${{ matrix.os }}\n  reusable:\n    uses: org/repo/.github/workflows/build.yml@main\n");
        check(scan.Jobs[0].SelfHosted && scan.Jobs[0].Labels.SequenceEqual(new[] { "self-hosted", "ARM64" }), "Workflow label arrays remain intact.");
        check(scan.Jobs[1].Group == "home" && scan.Jobs[1].SelfHosted, "Runner groups are discovered.");
        check(scan.Jobs[2].Dynamic && scan.Jobs[3].Dynamic, "Expressions and reusable workflows remain unresolved.");
        try { GitHubWorkflowScanner.Parse("alias.yml", "a: &a [x]\njobs: *a"); check(false, "Aliases must be rejected."); } catch (FormatException) { check(true, "Aliases rejected safely."); }
        var body = Encoding.UTF8.GetBytes("{\"action\":\"completed\"}");
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), body));
        check(GitHubWebhookReceiver.ValidSignature(body, "secret", signature), "Signed webhook accepted.");
        check(!GitHubWebhookReceiver.ValidSignature(body, "wrong", signature), "Wrong webhook secret rejected.");
        check(!GitHubWebhookReceiver.ValidSignature(Encoding.UTF8.GetBytes("tampered"), "secret", signature), "Tampered webhook rejected.");
        check(!GitHubWebhookReceiver.ValidSignature(body, "secret", "sha256=garbage"), "Malformed webhook signature rejected.");
    }
}
