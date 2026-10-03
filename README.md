# Runner Room

A simple **C# / ASP.NET Core 9** prototype that lists GitHub Actions runners on a Linux server and shows **On**, **Off**, or **Unknown**. The frontend is plain HTML, CSS, and JavaScript. No Python, database, GitHub token, or frontend build step.

## Run on your Linux server

Install the .NET 9 SDK, copy this project to the server, then run from the project directory:

```bash
dotnet run -- --RunnersRoot /srv/actions-runners --urls http://0.0.0.0:8080
```

Replace `/srv/actions-runners` with the folder containing your runners:

```text
/srv/actions-runners/
  runner-one/    # .runner, bin/Runner.Listener, run.sh, ...
  runner-two/
  runner-three/
```

Open `http://YOUR_SERVER_IP:8080` from another device on your network. Run as the Linux user that owns your runners so their process identities can be read. Allow TCP 8080 from your LAN if your firewall blocks it. This prototype has no authentication and is intended for a trusted local network.

A single runner directory also works. Discovery checks the supplied folder and its immediate children; symbolic-link child folders are skipped. The root can also be set through the `RunnersRoot` environment variable.

## Preview locally

```bash
dotnet run -- --Demo true --urls http://127.0.0.1:8080
```

Open `http://localhost:8080`. Demo mode works on Windows, Linux, or macOS and is clearly labeled. Real process detection requires Linux.

## What On and Off mean

- **On:** a local `Runner.Listener` or `Runner.Worker` executable from that runner folder is running.
- **Off:** no matching process was found.
- **Unknown:** the OS or permissions prevent a reliable process check.

The list refreshes every five seconds. `On` describes a local process, **not a confirmed connection to GitHub**. This prototype supports the normal executable layout from the Linux runner archive. It does not inspect jobs, logs, metrics, or systemd services, and cannot start or stop runners. It reads only runner names from `.runner` and process identities from `/proc`; credentials are not read.

## Docker preview

```bash
docker build -t runner-room .
docker run --rm -p 127.0.0.1:8080:8080 runner-room --Demo true
```

For monitoring actual host runners, run the .NET app directly on the Linux host. An ordinary Docker container cannot see the host's runner processes; mounting only the runner folder is insufficient. Docker is useful here for previewing and testing Linux process detection inside an isolated container.

## Files

- `Program.cs`: small web server and `GET /api/runners` endpoint.
- `RunnerMonitor.cs`: folder discovery and Linux process detection.
- `wwwroot/`: HTML, CSS, and JavaScript.
- `Dockerfile`: build and run in Linux.

Build with `dotnet build`. To publish and run without the project source:

```bash
dotnet publish -c Release -o ./publish
cd publish
dotnet RunnerRoom.dll --RunnersRoot /srv/actions-runners --urls http://0.0.0.0:8080
```
