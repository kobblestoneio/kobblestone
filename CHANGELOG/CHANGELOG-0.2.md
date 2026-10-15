- [v0.2.0](#v020)

# v0.2.0

## Upgrade Notes

- Let all running `Backup`s and `Restore`s finish before upgrading. Jobs created by previous versions are not considered when serializing backups and restores.
- Apply the complete manifest of this release. The operator requires additional RBAC permissions (listing `Backup`s, `Restore`s and `Job`s, reading `PersistentVolumeClaim`s and managing `Lease`s).
- The operator now uses leader election via a `Lease` in its own namespace. After a restart or rollout, a new operator Pod waits until the lease of the previous Pod has been released or has expired (at most 15 seconds) before it starts reconciling.
- A `Server` is now kept stopped while a `Restore` or an offline `Backup` of it is running, even if `spec.state` is `Running` or the server is woken up from hibernation. It is started as soon as the operation has finished.
- The auto-sleep webhook now requests hibernation by setting the annotation `kobblestone.io/hibernate: "true"` on the `Server` instead of writing its status. The operator records the request in the `Hibernating` condition and removes the annotation, as it already did for `kobblestone.io/wake-up`.
- `status.address` of `Server`s now contains the IP address of the server's `Service` instead of its cluster-internal DNS name and port (`<name>.<namespace>.svc.cluster.local:<port>`). It is shown in the new `Address` column. The `Host` column (the hostname of `spec.route`) was removed from `Server`s and `Network`s.
- `BackupRepo`s of type `PersistentVolumeClaim` now create their claim with the name from `spec.persistentVolumeClaim.name`. Previously, the claim was named after the `BackupRepo`. If those names differed, a new claim is created on upgrade. The old claim (and the snapshots in it) is left untouched and can be migrated or deleted manually.

## Changes by Kind

### Feature

- `Backup`s and `Restore`s of the same `Server` are queued and run one after another, in the order they were created. Waiting requests do not get a `Job` until they can run. The running `Job`s are the single source of truth, so a failed or deleted `Job` can no longer leave a `Server` locked.
- `Backup`s, `Restore`s and snapshot cleanups using a `BackupRepo` of type `PersistentVolumeClaim` run one at a time per volume, unless the claim supports `ReadWriteMany`. This prevents Multi-Attach errors when multiple operations would run on different nodes.
- Online backups of running servers are scheduled on the node of the `Server` Pod. Offline backups of stopped servers can run on any node.
- The `Server` conditions `BackupInProgress` and `RestoreInProgress` now reflect the running `Job`s and correct themselves on every reconcile.
- Reasons why a resource cannot be reconciled yet (e.g. a missing `BackupRepo`, a `Server` that is not stopped or busy, an unknown `Router`) are now reported as Kubernetes events and, for `Backup`s and `Restore`s, in `status.message`. The resource is retried periodically instead of failing the reconciliation.
- `Backup`s and `Restore`s are marked as `Failed` if their `Job` is deleted before it finished.
- Leader election is enabled, so the operator can safely run with multiple replicas.
- Every resource's status is now written only by its own controller. Controllers watching child resources (Jobs, StatefulSets, Services, `Command`s) queue the owning resource for reconciliation instead of updating its status, which prevents conflicting and lost status updates.
- `Router`s, `Server`s and `Network`s report the IP address of their `Service` in `status.address` (shown in the `Address` column), preferring external addresses: the IP assigned by a load balancer, then the first of `spec.externalIPs`, then the cluster IP. The address is updated as soon as the `Service` changes, e.g. when a load balancer assigned an IP.
- All fields of all resources are now documented in the CRDs (shown by `kubectl explain` and in the CRD reference docs).
- `Backup`s and `Restore`s have a new status field `startedAt`, the time their `Job` was created.

### Bug or Regression

- Fix two pending `Backup`s of the same `Server` blocking each other forever.
- Fix `Restore`s not waiting for running `Backup`s or other `Restore`s of the same `Server`.
- Fix online backups failing with Multi-Attach errors when the backup Pod was scheduled on a different node than the `Server`.
- Fix backup Pods being blocked from accessing RCON by the `NetworkPolicy` of the `Server`.
- Fix reconciliation errors for `Backup`s and `Restore`s whose `Job` already existed (e.g. after an operator restart).
- Fix a stale `BackupInProgress` condition blocking the deletion of a `Server` forever.
- Fix `Route`s not being retried when their `Router` did not exist yet.
- Fix `Command`s not being retried when the RCON password secret did not exist yet.
- Fix `BackupRepo`s of type `PersistentVolumeClaim` creating their claim with the name of the `BackupRepo` instead of `spec.persistentVolumeClaim.name`.
- Fix `Restore`s ignoring `spec.job.image`.
- Fix `Network`s ignoring `spec.proxy.offline`. If set to `true`, the proxy now runs with `online-mode = false`.
- Fix the documentation of `Server` `spec.lifecycle.rolloutPolicy`, which listed `OnRestart` instead of the actual value `Manual`.
- Fix `Router`s reporting their cluster IP when the first load balancer ingress entry had a hostname instead of an IP.
- Fix the auto-sleep webhook never hibernating a `Server` (namespace and name were swapped when looking up the server).
- Fix `Server`s never hibernating again after being woken up, because the `kobblestone.io/wake-up` annotation was never removed.
- Fix `Backup`s with `retainPolicy: RequireDelete` never retrying a failed cleanup. Deleting the failed cleanup `Job` now starts a new one.

### Dependency

- Bump KubeOps to `13.3.3`.
- Bump `KubernetesCRDModelGen.Models.gateway.networking.k8s.io` to `1.57.0`.
- Bump `itzg/mc-backup` to `2026.10.0` for `Backup`s.
- Pin `itzg/mc-proxy` to `java25-2026.10.0` for `Network`s.
- Bump `itzg/mc-router` to `1.48.0` for `Router`s and `Server` sidecars. With `autoSleep.mode: Hibernate`, only player logins reset the scale-down timer now, so server list pings no longer keep a server awake.
- Bump `itzg/minecraft-server` to `2026.10.0` for `Server`s.
- Bump `amazon/aws-cli` to `2.37.13` for `Backup`s, `Restore`s and snapshot cleanups using S3 repos. Checksums are only sent when required, to stay compatible with S3-compatible providers.
- Bump `ubuntu` to `26.04` (from the end-of-life `25.04`) for `Backup`s, `Restore`s and snapshot cleanups.
