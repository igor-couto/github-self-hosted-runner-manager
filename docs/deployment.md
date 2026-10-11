# Docker Compose and Helm deployment

Both packages deploy one dashboard with persistent history, alert state and authentication keys. Production runner directories are read-only, and runner management is forced off. The host's runner services continue running independently. Neither package registers runners or needs a GitHub token for local monitoring.

## Docker Compose on a Linux runner server

Requirements: a repository checkout, Docker Engine and Compose 2.24.4 or newer. Run these commands on the server that owns the runners:

```bash
cd deploy/compose
cp .env.example .env
cp config/settings.example.json config/settings.json
id github-runner
```

Edit `.env` with that account's numeric UID/GID, the server name and the **absolute, real runner parent path**. Keep the same UID on subsequent deployments; changing it also requires migrating ownership of existing state files. The initializer sets ownership of the state volume's root only. Set `BIND_ADDRESS` to your server's LAN IP to access it from another device; the default is localhost. `PORT` defaults to 8080.

Edit `config/settings.json` for optional settings. Do not put alternate `RunnersRoots` or `Monitoring.StateDirectory` in this file: deployment configuration owns those paths. Files in `config/` must be readable by the selected account. Tokens may be regular files in this directory, referenced as `/etc/runner-room/github.token`, for example. Local config, secrets and `.env` are ignored by Git and excluded from the Docker build context.

```bash
docker compose up -d --build --wait
docker compose ps
docker compose logs --tail=100 dashboard
```

The image is built from this checkout; a public image registry is not assumed. If using a published image you control, set `RUNNER_ROOM_IMAGE` to its immutable tag, then use `docker compose pull` and `docker compose up -d --no-build --wait`. Both the initializer and dashboard use the same image. For a release checkout, set `APP_VERSION` to its version without `v` so application update checks identify the installed version correctly.

After changing settings, run `docker compose restart dashboard`. The named `state` volume holds `/var/lib/runner-room`. A short-lived initializer prepares its ownership; the dashboard runs as the non-root runner account with a read-only root filesystem, dropped capabilities and bounded log files. `/tmp` is ephemeral. The health check calls `/healthz` without authentication.

Stop and remove the deployment with `docker compose down`. This preserves state and all host runner files. **`docker compose down --volumes` also deletes dashboard history and authentication keys.** The native `uninstall.sh` command applies to systemd installations, not containers.

### Portable demo

This separate file uses explicitly labeled sample data, requires no runner path and does not share host processes. It works on Docker Desktop as well as Linux:

```bash
docker compose -f deploy/compose/compose.demo.yaml up -d --build --wait
# Open http://127.0.0.1:8080
docker compose -f deploy/compose/compose.demo.yaml down
```

### Updates and backups

Back up state and `config/` before updating. Stop the writer while archiving the volume. From `deploy/compose`, on Linux:

```bash
docker compose stop dashboard
docker compose run --rm -T --no-deps --entrypoint tar dashboard -C /var/lib/runner-room -czf - . > runner-room-state.tgz
docker compose start dashboard
```

Store the archive privately; it can contain authentication keys. To restore, stop the dashboard and extract the archive into an empty state volume using the same UID/GID. The corresponding command is `docker compose run --rm -T --no-deps --entrypoint tar dashboard -C /var/lib/runner-room -xzf - < runner-room-state.tgz`. Restore `config/` separately.

Check out the desired release, update `APP_VERSION`, then run `docker compose up -d --build --wait`. To roll back, rebuild the previous checkout or select the previous image tag. Restore a compatible backup if a future release changes the state format. The dashboard reports release availability; it does not replace its own container.

## Helm on Kubernetes

Requirements: Helm 3, Kubernetes 1.25+, Linux nodes, and a storage provisioner or an existing writable PVC. The chart is at `deploy/helm/runner-room`. Build an image and make it available to the cluster; use an immutable tag:

```bash
# Replace the registry/account/tag and version with your own release values.
docker build --build-arg VERSION=1.2.3 -t YOUR-REGISTRY/runner-room:1.2.3 .
docker push YOUR-REGISTRY/runner-room:1.2.3
```

Build on the node's architecture, or use Docker Buildx with the required platforms for mixed x64/ARM nodes. Loading `runner-room:local` into a local cluster is another option. This task does not publish an image or create a registry. For private registries, supply `imagePullSecrets` referring to an existing Kubernetes pull Secret.

### Demo installation

The chart defaults to sample data, without host PID access or host-path mounts:

```bash
helm upgrade --install room deploy/helm/runner-room \
  --namespace runner-room --create-namespace \
  --set image.repository=YOUR-REGISTRY/runner-room --set-string image.tag=1.2.3 \
  --wait --timeout 5m
kubectl -n runner-room port-forward service/room-runner-room 8080:8080
helm test room --namespace runner-room
```

Open `http://127.0.0.1:8080`. If the cluster has no default storage class, set `persistence.storageClass`, provide `persistence.existingClaim`, or set `persistence.enabled=false` for a disposable preview. With persistence disabled, pod replacement loses all dashboard state.

### Monitor a real runner node

Copy `values-linux.example.yaml` to `values-linux.yaml` inside the chart directory. Set the image, `hostMonitoring.nodeName` to the node's `kubernetes.io/hostname` label, `runnerPaths` to the actual directories, and `runAsUser`/`runAsGroup` to the host runner account. The directories must already exist on that node. This requires a namespace policy that permits host PID and read-only hostPath access; restricted/baseline Pod Security policies may reject it.

```bash
kubectl get nodes -L kubernetes.io/hostname
helm upgrade --install room deploy/helm/runner-room \
  --namespace runner-room --create-namespace \
  -f deploy/helm/runner-room/values-linux.yaml --wait --timeout 5m
```

The deployment is pinned to that node, uses one replica and `Recreate` upgrades so only one process writes its state. This causes a brief dashboard outage during upgrades. Do not scale it out or share one state claim between releases. For multiple runner nodes, install one separately configured release per node, each with its own PVC; this does not provide a fleet aggregator or discover Kubernetes runner pods automatically.

### Configuration, secrets and access

Non-sensitive application options go under `settings` in values. The chart creates a ConfigMap and restarts the pod when those settings change. For credentials or local password hashes, use an existing Secret with a `settings.json` key:

```bash
kubectl -n runner-room create secret generic room-settings --from-file=settings.json=/private/path/settings.json
# Add existingSettingsSecret: room-settings to your private values file.
```

For file-based integrations, create a Secret containing regular file keys and set, for example:

```yaml
existingSettingsSecret: room-settings
credentialsSecret: room-credentials
credentialKeys: [github.token, webhook.url]
```

Point the settings JSON's `GitHub.TokenFile` to `/etc/runner-room/secrets/github.token` and `Alerts.WebhookUrlFile` to `/etc/runner-room/secrets/webhook.url`. Keys are mounted individually with `subPath` because the application rejects symbolic-link token files. Neither settings nor tokens hot-reload; after changing either Secret, run `kubectl -n runner-room rollout restart deployment/room-runner-room`. Do not store secrets in chart values or commit local files. Helm stores supplied values in release records.

The default ClusterIP Service is accessible inside the cluster; port-forward is local only. Optional `service.type`, `ingress.enabled`, `ingress.className`, `ingress.host`, annotations and TLS entries support existing cluster networking. The chart does not install an ingress controller or certificate issuer. For example:

```yaml
ingress:
  enabled: true
  className: nginx
  host: runners.example.com
  tls:
    - secretName: runners-tls
      hosts: [runners.example.com]
```

Access is open by default, matching the native dashboard. Configure the existing [sign-in and access controls](../README.md) before exposing it to untrusted clients. With TLS termination, set `Access.PublicOrigin`, keep `Access.RequireHttps=true`, and allow the exact ingress proxy IPs in `Access.TrustedProxies`. The application does not accept CIDR ranges there. If proxy IPs change, the allowlist must be updated. Local HTTP-only sign-in needs `RequireHttps=false`. `/healthz` remains available to probes when sign-in is enabled.

### Storage, upgrades and removal

The default PVC is 1 GiB, ReadWriteOnce, retained on uninstall. `persistence.storageClass=null` uses the cluster default; `""` requests no dynamic storage class. An existing claim must be writable through the configured group and compatible with the selected node. Retain the UID/GID across upgrades. Use a CSI snapshot or an offline volume backup, along with copies of your Secret configuration, before upgrades.

Update the image tag in your values and run the same `helm upgrade --install` command. Inspect `helm history room -n runner-room`; roll back with `helm rollback room REVISION -n runner-room --wait`. ConfigMaps trigger automatic restarts; external Secret changes require the restart described above.

`helm uninstall room -n runner-room` removes the dashboard and Service but preserves the chart-created PVC by default. To reuse retained data on reinstall, set `persistence.existingClaim` to the retained claim name (normally `room-runner-room`). Delete that PVC explicitly only when its history and keys are no longer needed. Existing claims are never owned by the chart. Setting `persistence.retain=false` lets Helm remove a chart-created PVC on uninstall.

Package the chart for distribution:

```bash
helm lint deploy/helm/runner-room --strict
helm package deploy/helm/runner-room --destination artifacts
```

## Container visibility and limits

- Runner detection reads `/proc/<pid>/exe` and compares absolute executable paths. Real monitoring shares the host PID namespace and mounts each runner directory at the same absolute path. Use the runner account's numeric UID and readable parent directories. Absolute symlink targets must also be visible. Kernel `hidepid`, AppArmor/SELinux, non-dumpable processes, user-namespace remapping or runners owned by another UID can still prevent inspection; those readings may be unknown. Do not interpret unknown as offline.
- Host PID access exposes process metadata. The packages do not grant privileged mode, ptrace capabilities, Docker socket access or the systemd bus. The dashboard cannot administer host services; use the native installer for runner lifecycle management.
- CPU/RAM counters can reflect the Linux host while mount listings and disk capacity reflect mounted filesystems. Network counters use the container/pod network by default. The chart's optional `hostMonitoring.hostNetwork=true` exposes host network metrics and binds 8080 on the node, bypassing Service-only exposure. For Compose, an override can use `network_mode: host` with `ports: !reset []`; the app then binds node port 8080, and `BIND_ADDRESS`/`PORT` no longer apply. Configure host firewall/access accordingly.
- Sensors are limited to what the container can read from `/sys`. Raspberry Pi firmware commands and device access are not supplied. Kubernetes shows the pod hostname; Compose uses `RUNNER_HOSTNAME`. Docker Desktop's Linux VM is not the physical Windows/macOS host.
- These are single-host dashboard packages, with read-only runner mounts. No SSH, host agent, cluster-wide runner discovery or containerized runner-management privileges are added.

Implementation references: [Compose service configuration](https://docs.docker.com/reference/compose-file/services/), [Kubernetes volumes and hostPath](https://kubernetes.io/docs/concepts/storage/volumes/), and [Helm chart conventions](https://docs.helm.sh/docs/v3/chart_best_practices/conventions/).

## Validation

`node tests/deployment.cjs` requires Node 18+, Docker and Compose 2.24.4+. It lints/renders Helm variants using a pinned Helm container, builds the dashboard and native test fixture, and tests Compose health, real process visibility, static assets, read-only mounts and state preservation. It uses disposable containers/volumes and no GitHub credentials. Kubernetes installation, ingress routing, storage provisioning and physical host sensor visibility still need validation on the target cluster/server.
