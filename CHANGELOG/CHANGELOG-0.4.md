- [v0.4.0](#v040)

# v0.4.0

## Summary

Kobblestone 0.4 adds file access to servers and backups and makes deployments fit stricter clusters:

- **New `FileBrowser`**: browse and edit the files of a `Server`, the content of a `Backup` or the archives in a `BackupRepo` through a web interface, SFTP, WebDAV or a REST API, with per-user permissions, read-only targets and optional expiry. The file manager runs next to the data and follows the server when it moves.
- **Resources for every container**: all containers have configurable requests and limits, and the containers Kobblestone runs for itself have defaults, so namespaces with `ResourceQuota`s accept them. `Server` `spec.resources` now applies to the server container as documented, also on clusters older than Kubernetes 1.34.
- **Stable addresses**: `status` shows load balancer addresses or the `Service`'s DNS name in the cluster, never its changing cluster IP.
- **Separate manifests**: `kobblestone-crds.yaml` and `kobblestone-operator.yaml` next to the complete `kobblestone.yaml`.
- **Kubernetes 1.32 or newer** is required: related resources are found with field selectors, filtered by the API server.
- **Re-create existing `Server`s and `Network`s**: Kobblestone only uses `Secret`s it created for a resource, and earlier versions created them without an owner (see the upgrade notes).
- **Updated components**: KubeOps 14, `itzg/minecraft-server` 2026.11.1, mc-router 1.56.5 and others; quieter operator logs.

## Upgrade Notes

- Upgrade with the complete manifest of this release or the Helm chart: it adds the `FileBrowser` CRD, and the operator needs additional permissions for `FileBrowser`s (managing `Deployment`s, `NetworkPolicy`s, `HTTPRoute`s and `TCPRoute`s, deleting `Pod`s, reading `PersistentVolumeClaim`s and listing `BackupRepo`s).
- Kobblestone now requires Kubernetes 1.32 or newer: the CRDs of `Backup`, `Restore`, `Route`, `Bot` and `BackupRepo` declare `selectableFields`, which older API servers do not support. Apply the CRDs of this release (included in the manifest and the Helm chart).
- `Server` `spec.resources` now applies to the server container instead of the `Pod`, as documented. Setting it on the `Pod` (pod-level resources) only works with Kubernetes 1.34 or newer: on older clusters it was ignored, so servers ran without requests and limits and sized their JVM heap from the node's memory. The mc-router sidecar has its own resources now (`spec.pod.proxyResources`), so `spec.resources` no longer includes it. `Backup` and `Restore` `job.resources` likewise apply to each container of the `Job` instead of the `Pod`. Running `Server`s are restarted once by the upgrade (as by the new server image).
- `Server`s and `Network`s created with an earlier version stop being reconciled and report `SecretConflict`: their `Secret` (named like the resource) has no owner, and Kobblestone no longer uses `Secret`s it does not own. Re-create them (deleting a `Server` deletes its volume: back it up first and restore the `Backup` into the new server). Alternatively, delete the `Secret` and restart: a `Server` with `kubectl rollout restart statefulset/<name>`, a `Network` by restarting its proxy `Deployment` and all of its servers, since a new RCON password or forwarding secret is generated.

## Changes by Kind

### Feature

- Every container Kobblestone creates has configurable compute resources, and the containers Kobblestone runs for itself have default requests and memory limits, so namespaces whose `ResourceQuota` requires them accept the `Pod`s:
  - New `Server` `spec.pod.proxyResources` for the mc-router sidecar (default: request `10m` CPU and `32Mi` memory, limit `128Mi` memory).
  - New `Account` `spec.authorization.pod.resources` for the authorization `Job`.
  - `Backup` `job.resources` also applies to the upload to S3 and to the `Job` deleting the archive (`retainPolicy`); `Restore` `job.resources` to the check for existing data and the download from S3.
  - Defaults for the containers of backup, restore, cleanup and authorization `Job`s and of `FileBrowser`s: request `10m` CPU and `64Mi` memory, limit `256Mi` memory.
  - The Minecraft server, the `Network` proxy, `Router`s and `Bot`s still have no defaults: unset means no requests or limits.
- Each release also comes with the manifests `kobblestone-crds.yaml` (only the CRDs) and `kobblestone-operator.yaml` (only the operator: namespace, RBAC, `Deployment` and `Service`), next to `kobblestone.yaml`, which still contains both. This allows managing the CRDs separately from the operator, e.g. to update them first or keep them when the operator is removed.
- New resource `FileBrowser`: file access to the data of a `Server`, the content of a `Backup` or the archives in a `BackupRepo`, through a web interface, SFTP, WebDAV and a REST API ([SFTPGo](https://github.com/drakkan/sftpgo)). The operator runs the file manager next to the data: while a server runs, on the server's node (its volume can only be mounted there), and it follows the server when it moves. The server is not restarted or changed for this.
  - `targets` selects what to browse: up to 16 `Server`s, `Backup`s and `BackupRepo`s, each a folder when there are several. `readOnly` is enforced on the mounted volume; `Backup`s are always read-only and extracted into a scratch volume (`pod.scratchSize`) first; `BackupRepo`s are read-only by default, and S3 repos are served directly from the bucket.
  - `users` with passwords and SSH keys from `Secret`s and `permissions` per path (`None`, `ReadOnly`, `ReadWrite`). Without `users`, one user with full access is generated (`status.credentialsSecretRef`).
  - Interfaces, each opt-in with its own `Service`: `web` (with an optional `HTTPRoute`, and `embedding` into other sites), `sftp` (with an optional `TCPRoute`; the host key is generated once and kept), `webdav` (with an optional `HTTPRoute`) and `api` (SFTPGo's user REST API with a generated or given token). `allowedFrom` restricts an interface to certain peers with a `NetworkPolicy`.
  - `expiresAfterSeconds` (e.g. `7200` for two hours) limits the access: an expired `FileBrowser` deletes its file manager, `Service`s, routes, `NetworkPolicy` and credentials, and reports `Expired`.
  - Volumes in use on another node by Kobblestone's `Pod`s (servers, backup, restore and cleanup `Job`s, other `FileBrowser`s) move the file manager there as soon as they start; volumes in use on different nodes make the `FileBrowser` `Failed` until that resolves. `Pod`s created by users are not taken into account.
  - `kubectl get filebrowsers` shows the status, the address of the web interface and the expiry; `-o wide` also the addresses of SFTP, WebDAV and the API.
- New `Backup` and `Restore` field `job.nodeSelector` for the `Pod`s of their `Job`s (for `Backup`s also the `Pod` deleting the archive). The backup of a running server still always runs on the server's node, where its volume is attached.
- `Router`s report their current and ready replicas (`status.replicas`, `status.readyReplicas`, columns `Replicas` and `Ready`). The `Replicas` column showed `spec.replicas`, which is empty unless set. The `Network` column `Replicas` (`spec.proxy.replicas`) is removed; the `Proxy` column shows the ready and desired proxy replicas.

### Bug or Regression

- Fix `Bot`s not connecting to `Network`s: MCC detects the Minecraft version from the server list ping, and the Velocity proxy answers with the newest version it supports, which can be newer than the backend servers and the bundled MCC ("This version is not supported"). Bots now use the version of their target (`Server` `spec.version`, `Network` `spec.serverTemplate.version`) when it is a release version. A `MinecraftVersion` in `config` still takes precedence.
- Fix chat behind a `Network` with `proxy.offline: true`: the managed servers rejected every chat message ("Chat disabled due to missing profile public key"), since players without verified accounts have no profile keys. Managed servers of offline networks get `enforce-secure-profile=false`, unless `properties` sets it.
- Fix `Route`s created together with their `Router` (or while a router `Pod` starts) sometimes never being registered in mc-router until the router `Pod` restarted. `Route`s are retried until every router `Pod` has synced its routes. Router `Pod`s have a readiness probe on mc-router's API, and the operator only syncs routes once it is reachable.
- Fix volumes of deleted `Server`s and `BackupRepo`s staying in `Terminating`: the finished `Pod`s of backup, restore and cleanup `Job`s kept using them as long as their `Backup` or `Restore` existed. These `Job`s are now deleted 10 minutes after they finished. `Job`s finished before the upgrade stay until they are deleted by hand. A failed cleanup `Job` (`retainPolicy: RequireDelete`) is thereby retried every 10 minutes.
- Fix the `Secret`s of `Server`s (RCON password) and `Network`s (forwarding secret) not being deleted with them: they are owned by their resource now. A `Secret` with the resource's name that it does not own (e.g. one of the user's) is no longer used: the resource reports `SecretConflict` (a warning event, and the `Error` condition of `Server`s or `status.message` of `Network`s) until it is deleted or renamed.

### Other (Cleanup or Flake)

- `status.address` of `Server`s, `Router`s and `Network`s and the interface addresses of `FileBrowser`s (`status.web`, `sftp`, `webdav`, `api`) no longer show the cluster IP of the `Service`, which changes when the `Service` is recreated. Without an external address, they show the `Service`'s DNS name in the cluster instead, e.g. `my-server.minecraft.svc.cluster.local` (with `:port` for `FileBrowser`s). External addresses are still preferred, and a hostname assigned by a load balancer (e.g. AWS) is now used too, instead of being skipped.
- Controllers find the resources that reference an object (the `Backup`s and `Restore`s of a `Server`, the `Route`s of a `Router`, the `Bot`s of an `Account`) with field selectors on the referencing spec field, filtered by the API server, instead of listing every resource of the namespace and filtering it in the operator. The CRDs declare these fields as `selectableFields` (added to the generated CRDs at build time, since KubeOps does not generate them). The operator does not change the metadata of these resources for this. The `FileBrowser`s of a `Server` (their targets are a list, which cannot be a selectable field) are found in an in-memory index kept by the `FileBrowser` controller. The `Pod`s using a server's or repo's volume are found by their labels: backup, restore and cleanup `Job` `Pod`s are labelled with their server (`kobblestone.io/target-server`) and repo (`kobblestone.io/backup-repo`), file manager `Pod`s with the servers and repos they mount; repos sharing a volume are found with a selectable field (`spec.persistentVolumeClaim.name`).
- RCON commands sent by the operator (`Command`s, `CommandGroup`s) stop waiting when the reconcile is cancelled; the RCON library takes no cancellation tokens.
- Quieter operator logs: the start and completion of every reconciliation, watcher events and cache tracing are no longer logged at `Information`; failures and retries are still logged. To see them again, set `Logging__LogLevel__KubeOps=Information` (e.g. with `extraEnv` in the Helm chart).
- The router `Pod` controller waits for the `Pod`'s IP and ready containers instead of failing (`UriFormatException`) and retrying. `Command`s report `Executed successfully` (was misspelled).

### Dependency

- Bump KubeOps to `14.0.0` and `KubernetesClient` to `21.0.41` (KubeOps 14 requires `21.0.8`; the newer version fixes watch reads that kept running after the watch was cancelled). Kobblestone uses none of the APIs removed in KubeOps 14 (synchronous client methods, JsonPatch.Net types, the development tunnel), and the generated CRDs and RBAC are unchanged.
- Bump `KubernetesCRDModelGen.Models.gateway.networking.k8s.io` to `1.59.0`, `Tomlyn` to `2.11.1`, `MessagePack` to `3.1.12` and `Microsoft.Extensions.Hosting` to `10.0.15`.
- Bump `itzg/minecraft-server` to `2026.11.1` for `Server`s. Includes the fix for the Java4Shell vulnerability in the base image.
- Bump `itzg/mc-router` to `1.56.5` for `Router`s and `Server` sidecars. Includes support for the Minecraft 26.4 handshake, a fix for auto-scaling same-named `Service`s in different namespaces, and security updates of OpenTelemetry. Server list pings are no longer logged at `info`. The route API stays compatible (the optional API key and per-route PROXY protocol are opt-in and not used by Kobblestone).
- Bump `itzg/mc-proxy` to `java25-2026.11.2` for `Network`s.
- Bump `itzg/mc-backup` to `2026.11.2` for `Backup`s.
- Bump `amazon/aws-cli` to `2.42.4` for `Backup`s, `Restore`s, snapshot cleanups and `FileBrowser`s using S3 repos. The standard retry mode now retries faster, and the bundled CA certificates no longer include the `AAA Certificate Services` root (S3-compatible endpoints whose certificate chain ends only at that root fail TLS verification).
