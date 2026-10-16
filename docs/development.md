# Development and checks

[Back to README](../README.md)

Production projects live under `src/`, checks and fixtures under `tests/`, and the solution at `RunnerRoom.sln`. The main application, including its HTML, CSS and JavaScript, is in `src/RunnerRoom/`; the optional tray client is in `src/RunnerRoom.Tray/`.

See [Project structure](architecture.md) for folder ownership, namespaces and build conventions.

Requires the .NET 9 SDK:

```bash
dotnet run --project src/RunnerRoom -- --Demo true --urls http://127.0.0.1:8080
```

Open `http://localhost:8080`. Demo data is explicitly labeled. To inspect real Linux runners, replace `--Demo true` with `--RunnersRoot /path/to/runners`.

Run these commands from the repository root. Build the solution with `dotnet build`, or just the server with `dotnet build src/RunnerRoom`. Check system metric parsing and failure cases with `dotnet run --project tests/RunnerRoom.Tests`. For a Docker demo:

```bash
docker build -t runner-room .
docker run --rm -p 127.0.0.1:8080:8080 runner-room --Demo true
```

`tests/dashboard.cjs` exercises detail routes, preferences/layout persistence, themes, mobile layouts and offline behavior against this demo. It requires Node, Playwright and Chrome: install Playwright in your development environment, then `node tests/dashboard.cjs`. Set `RUNNER_ROOM_TEST_URL` to use another local demo origin. Screenshots are written to the ignored `artifacts/` directory. The console checks include quota parsing, stale/invalid data, credential destination/masking, stable job IDs and terminal text escaping. Live provider credentials and native tray interaction need separate end-to-end validation.

For installer tests:

```bash
dotnet publish src/RunnerRoom/RunnerRoom.csproj -c Release -r linux-x64 --self-contained true -p:InvariantGlobalization=true -o artifacts/package-linux-x64
docker build -f tests/installer.Dockerfile -t runner-room-installer-test .
docker run --rm runner-room-installer-test
```

These tests use the actual bundled x64 application and real Linux runner stand-in processes. Release downloads and service supervision are stubbed inside the disposable container; `systemd-analyze verify` checks the generated unit. Tests cover missing arguments, x64/ARM32/ARM64 download selection (including mixed kernel/userspace bitness), checksum failure, first installation, stale parent registration metadata, busy/idle/offline detection through versioned binaries, runner metadata and log summaries, service state parsing, recursive/multiple roots, aliases, failed-update rollback, and settings preservation during updates. The console checks also exercise GitHub responses using a fake HTTP handler, including denied access, invalid data, registration matching, and token isolation. Architecture selection tests stub system identity; they do not execute an ARM binary on the x64 test host.

Detailed monitoring checks cover per-core/load/swap parsing, network and disk rates, counter resets, UTC midnight, history restart/retention/failure, multiple filesystems and bind mounts, process trees/PID reuse, workspace scan limits, temperatures, battery units and Raspberry Pi flags. The installer integration also verifies background collection, live runner resource attribution, workspace sizes and service-owned history files. Hardware parsing uses fixtures; testing in Docker does not validate physical Raspberry Pi sensors or battery hardware.

Analytics checks cover UTC bucket boundaries, sampling gaps, incomplete events, log replay deduplication, duration statistics, filters/pagination, redaction, CSV escaping, retention, restart recovery and persistence failures. Installer integration verifies imported job durations, service-owned analytics files and history preservation through updates.

For runner management lifecycle tests with a native test-only listener and a non-root Linux account:

```bash
docker build -f tests/management.Dockerfile -t runner-room-management-checks .
docker run --rm runner-room-management-checks
```

These tests cover process start/stop, busy-worker refusal, crash retry/exhaustion, persisted restoration, interrupted maintenance, corrupt-state preservation, batch/pool operations, scope/path validation and credential isolation. GitHub requests are mocked; tests never register or cancel real runners/workflows. Real organization policies, systemd authorization and a full GitHub registration should be verified with your server's credentials before broad rollout.

## Publishing releases (maintainers only)

The release workflow builds self-contained packages for all three targets and uploads them, their checksums, `install.sh`, and `uninstall.sh` to a GitHub Release:

| Target | Release asset |
| --- | --- |
| x64 | `runner-room-linux-x64.tar.gz` |
| ARM32 (ARMv7+) | `runner-room-linux-arm.tar.gz` |
| ARM64 | `runner-room-linux-arm64.tar.gz` |

All three can be cross-published on GitHub's `ubuntu-latest` x64 runner; ARM hardware is not required to build them. The installer always downloads the latest published release unless `--version` is supplied.

After committing and pushing release changes, push a new version tag (replace the example with the next unused version):

```bash
git tag v0.1.0
git push origin v0.1.0
```

Wait for the **Release** workflow to succeed before using the new release. The public installer requires a public repository and release; it does not request GitHub credentials.

If a release failed on an older commit, push the fix and a **new version tag**. Re-running the old workflow uses the original tagged code and will repeat the same error.

### Optional Raspberry Pi build runner

Use a Raspberry Pi with a **64-bit Linux OS** and a registered GitHub self-hosted runner. Give it the custom label `runner-room-arm-builder`. Then create this repository **Actions variable** under Settings → Secrets and variables → Actions → Variables:

```text
Name:  ARM_RUNNER_LABELS
Value: ["self-hosted","linux","ARM64","runner-room-arm-builder"]
```

Both ARM32 and ARM64 package jobs will run on that Pi; one registered runner processes them sequentially. The workflow installs the .NET 9 SDK through `setup-dotnet`. The Pi needs the usual GitHub runner prerequisites plus Git, Bash, tar, and sha256sum. It does not need Docker or ShellCheck: validation, x64 installer tests, and release publication stay on GitHub-hosted runners.

Leave the variable unset to build everything on GitHub-hosted runners. Set it only once the Pi runner is online and has all four labels; otherwise the ARM jobs wait in the queue.
