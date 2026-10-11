# Runner Room Helm chart

Deploys one dashboard with a Service, health probes, optional ingress, persistent state and external Secret support. Default values display sample data without host access. For a Linux runner node, use `values-linux.example.yaml` and configure the node, absolute runner paths, UID/GID and your container image.

Build and publish the repository Dockerfile to a registry your cluster can reach; the chart does not assume a public image exists. See the [deployment guide](https://github.com/igor-couto/github-self-hosted-runner-manager/blob/main/docs/deployment.md) for setup, authentication, upgrades, backups and removal.

```bash
helm lint . --strict
helm upgrade --install room . --namespace runner-room --create-namespace \
  --set image.repository=YOUR-REGISTRY/runner-room --set-string image.tag=YOUR-TAG --wait
kubectl -n runner-room port-forward service/room-runner-room 8080:8080
helm test room -n runner-room
```

The dashboard runs non-root with read-only runner mounts and runner management disabled. Real monitoring needs host PID access and node-local mounts; it does not work under a policy forbidding those features. The PVC is retained by default when uninstalling. The `values.yaml` file documents all supported settings.
