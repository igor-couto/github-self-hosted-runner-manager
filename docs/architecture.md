# Project structure

Runner Room uses a solution at the repository root, production projects under `src/`, and separate tests under `tests/`. This is a common .NET repository convention, illustrated by Microsoft's [eShop reference application](https://github.com/dotnet/eShop), rather than a layout required by the SDK.

The main application is organized by feature, keeping related options, models and implementation together. Microsoft's [ASP.NET Core feature organization guidance](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/develop-asp-net-core-mvc-apps#feature-organization) describes this approach. The web UI, background collectors and management operations share one deployment and configuration. The Windows tray companion is a separate, optional client.

```text
RunnerRoom.sln                       Solution containing production and test projects
global.json                          SDK selection for the repository
src/
  RunnerRoom/
    RunnerRoom.csproj                ASP.NET Core application project
    Program.cs                       Entry point, service registration and HTTP routes
    Configuration/
      RunnerOptions.cs               Aggregate configuration bound from server settings
    Features/
      Access/                        Authentication, permissions and access audit
      Alerts/                        Alert evaluation, scheduling and notification delivery
      Analytics/                     Runner/job history, storage and reports
      GitHub/                        Runner connectivity API client and its settings
      Maintenance/                   Application release checks
      Management/                    Runner lifecycle, pools, services, packages and GitHub actions
      Monitoring/                    Host metrics, process/resource usage and measurement history
      Quotas/                        Optional provider quota integrations
      Runners/                       Local discovery, runner inventory, current jobs and logs
    Interfaces/
      Client/                        HTTP client shared by the console and Windows tray
      CommandLine/                   CLI commands and interactive terminal dashboard
    Properties/
      AssemblyInfo.cs                Assembly-level settings, including test visibility
    wwwroot/                         HTML, CSS, JavaScript, translations and PWA assets
  RunnerRoom.Tray/
    RunnerRoom.Tray.csproj            Optional Windows Forms tray project
    Program.cs                       Tray application entry point and interface
tests/
  RunnerRoom.Tests/
    RunnerRoom.Tests.csproj          Console checks referencing the server project
    Program.cs                       Console test runner
    Features/                        Checks grouped by application feature
    Interfaces/                      Client and terminal checks
  ...                                Browser checks and Linux integration fixtures
docs/                                Architecture notes and README screenshots
deploy/compose/                      Linux and demo Compose deployments
deploy/helm/runner-room/              Helm chart, values and Kubernetes templates
deploy/linux/                        Native systemd update helper
.github/workflows/                   Continuous integration and release packaging
Dockerfile                           Main application image, built from the repository root
install.sh / uninstall.sh            Linux installation and removal scripts
settings.example.json                Example deployed-server configuration
LICENSE / README.md                  Repository license and documentation
```

## Placement and dependencies

- Keep all production C#, project files and web assets in their owning project under `src/`. Tests, deployment scripts, repository configuration and documentation remain outside production projects.
- Put a feature's options, models and implementation together in its feature folder. `src/RunnerRoom/Configuration/RunnerOptions.cs` composes those options for configuration binding; it does not hold inventory models or parsing code.
- Match namespaces to folders below the project directory, for example `RunnerRoom.Features.Monitoring` and `RunnerRoom.Interfaces.Client`. `src` is a repository grouping, not part of a namespace. Use explicit imports for dependencies between features.
- Keep small, closely related records together in a feature model file. Split classes when they have distinct responsibilities; a separate project or file for every record is unnecessary.
- Keep the server's `Program.cs` focused on startup and HTTP composition. Endpoint behavior specific to access or management belongs in that feature's setup class.
- The shared dashboard client depends on HTTP and JSON contracts, not server implementation types. The tray project links that source file directly; it does not reference the ASP.NET Core project.
- Test folders mirror feature ownership. The test entry point invokes the checks; placing a new check file in a folder does not automatically execute it.

The feature folders describe ownership within one application, not enforced assembly boundaries. For example, analytics and alerts consume runner/system snapshots, and management uses local runner discovery. Shared implementation dependencies remain explicit in imports. JSON payloads, saved configuration keys and HTTP routes do not depend on source-file locations.

## Build and packaging

Run commands from the repository root:

```bash
# Build all projects in the solution, or only the server:
dotnet build
dotnet build src/RunnerRoom

# Run the dashboard with sample data:
dotnet run --project src/RunnerRoom -- --Demo true --urls http://127.0.0.1:8080

# Run the console checks:
dotnet run --project tests/RunnerRoom.Tests

# Publish only the Linux server:
dotnet publish src/RunnerRoom/RunnerRoom.csproj -c Release -r linux-x64 --self-contained true -p:InvariantGlobalization=true -o artifacts/package-linux-x64
```

Select a project explicitly for `dotnet run` and `dotnet publish`: the solution contains multiple applications, and the optional tray application targets Windows. SDK file discovery is scoped to each project directory, so the server does not need exclusions for tests or the tray source. Each project has its own ignored `bin/` and `obj/` output directories.

Build Docker images from the repository root with `docker build -t runner-room .`. The main Dockerfile copies the server project from `src/RunnerRoom/`. Linux management tests build the test project; installer tests consume a published Linux server package. The server project includes the repository's `LICENSE`, `install.sh`, `uninstall.sh`, and native update helper as linked publish content. Release packages retain the application executable and `wwwroot` assets, together with the scripts needed to maintain native installations. Application update endpoints request the separate systemd updater through its fixed inbox; the web process does not execute privileged installation commands.
