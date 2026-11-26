# Kobblestone operator

Kubernetes operator for Minecraft servers (.NET 10, KubeOps 14). Related repos (`github.com/kobblestoneio/`): `charts`
(Helm chart), `docs`, `website`.

Shell: Windows; Git Bash and PowerShell are available. `gh` may not be installed. Use `curl https://api.github.com/...`
(unauthenticated is fine for public repos).

## Layout

- `src/Kobblestone.Operator`: the operator, with `Controller/`, `Entities/V1Alpha1/` (CRD types; doc comments become CRD
  descriptions), `Finalizer/`, `Services/`, `Utils/`, `Constants/`.
- `src/Kobblestone.AccountAuth`, `src/Kobblestone.BotRunner`: separate images (`ghcr.io/kobblestoneio/account-auth`,
  `bot-runner`), each with its own Dockerfile. `src/Kobblestone.Sessions`: shared library.
- `examples/`: one sample manifest per resource kind.

## Build and generated files

- Building `Kobblestone.Operator` regenerates `kustomize/generated/*` (KubeOps; never edit by hand) and flattens the
  kustomizations into three manifests (needs `kubectl` on PATH): `kobblestone.yaml` (everything, `kustomize/` ->
  `kustomize/base/`), `kobblestone-crds.yaml` (`kustomize/crds/`) and `kobblestone-operator.yaml` (`kustomize/operator/`).
  Expect them in the diff after a build. Hand-written overrides of the operator (Service, Deployment patch) go into
  `kustomize/base/`. The crds overlay removes everything else by kind, and the build fails if a non-CRD kind is left
  in `kobblestone-crds.yaml`. `SelectableFields.targets` post-processes the CRDs.
- The manifests are committed and published by tagging: `get.kobblestone.io/<tag>/<path>` redirects to
  `raw.githubusercontent.com/kobblestoneio/kobblestone/refs/tags/<tag>/<path>`, so any file at the repo root is served
  once it is committed in the tagged commit (no release assets). Rebuild before tagging so the manifests are current.
- NuGet versions are central in `Directory.Packages.props`.
- Running locally: first build, then install the generated CRDs. The Debug-only CRD installer in `Program.cs` does not
  overwrite existing CRDs and does not add `selectableFields`, which the controllers' field selectors need:
  `kubectl apply --server-side --force-conflicts -f kobblestone-crds.yaml`. Then
  `dotnet run --no-build --project src/Kobblestone.Operator` against the current kube context. The operator rolls existing
  `Server`s/`Router`s onto the default images in `ContainerImages.cs`.
- Code paths that only work in cluster are skipped (e.g. `Services/RouterApi.cs` returns early when not
  in cluster, so mc-router routes are not registered from a local run).
- Local runs and `kubectl apply` of `kobblestone.yaml` conflict with a Helm-installed chart over the CRDs. Uninstall
  the chart first (see the `charts` repo's AGENTS.md for doing that without losing resources).

## Testing against a cluster

- Use fully qualified resource names (`kubectl get backups.kobblestone.io`). Other operators define the same kinds
  (Longhorn and CloudNativePG both have `Backup`), and the short name may resolve to theirs.
- Git Bash rewrites absolute paths in arguments (`kubectl exec ... -- cat /data/x` becomes a Windows path). Set
  `MSYS_NO_PATHCONV=1` for such commands.
- `sftp -b` implies `BatchMode=yes`, which disables password authentication; add `-o BatchMode=no` and feed the
  password through `SSH_ASKPASS` with `SSH_ASKPASS_REQUIRE=force`. Prefix batch commands with `-` to continue after an
  expected failure.
- `kubectl port-forward` takes one resource: forward the container ports of the pod (`deploy/<name>`), not several
  Services. Port-forwards bypass `NetworkPolicy`s; test `allowedFrom` from pods in different namespaces.
- `Logging__LogLevel__KubeOps=Information` shows every reconciliation (hidden by default in `appsettings.json`).

## Changelog

`CHANGELOG/CHANGELOG-<minor>.md`, Kubernetes-style sections: `Upgrade Notes`, `Feature`, `Bug or Regression`,
`Other (Cleanup or Flake)`, `Dependency`. The files use CRLF line endings.

Every minor release (`vX.Y.0`, starting with v0.4.0) begins with a `## Summary` section, directly after the release
heading and before `## Upgrade Notes`: one intro sentence and a few bold-led bullets with the highlights for users (new
resources, notable behavior changes, new requirements such as the minimum Kubernetes version, updated components).
Keep it in sync when entries are added later in the release cycle. Patch releases (`vX.Y.Z`) don't need one.

## External components (default container images)

All default images are pinned in `src/Kobblestone.Operator/Constants/ContainerImages.cs`. To check for updates:

- Docker Hub tags: `curl -s "https://hub.docker.com/v2/repositories/<ns>/<repo>/tags?page_size=100&ordering=last_updated"`
  (`library/ubuntu` for official images).
- ghcr tags (own images): get an anonymous token from `https://ghcr.io/token?scope=repository:kobblestoneio/<img>:pull`,
  then `GET https://ghcr.io/v2/kobblestoneio/<img>/tags/list`.
- Release notes: GitHub releases API for `itzg/mc-router`, `itzg/docker-minecraft-server`, `itzg/docker-mc-proxy`,
  `itzg/docker-mc-backup`, `drakkan/sftpgo`. AWS CLI: `CHANGELOG.rst` on the `v2` branch of `aws/aws-cli`.

What to verify before bumping:

- **mc-router**: the operator uses its REST API (`Services/RouterApi.cs`: `GET/POST /routes`, `DELETE /routes/{host}`,
  model `Services/RouterRoute.cs`) and CLI flags/env (`Controller/RouterController.cs`, and the sidecar in
  `Controller/ServerController.cs`: `API_BINDING`, `AUTO_SCALE_DOWN*`, `WEBHOOK_REQUIRE_USER`, ...). Diff
  `server/configs.go` and `server/api_server.go` between the old and new tag.
- **minecraft-server / mc-proxy**: tags are `<release>-<java>` and `<java>-<release>` respectively.
- **aws-cli**: S3-compatible providers are sensitive to checksum, retry and CA bundle changes.
- Ubuntu stays on the current LTS.
- Add a `Dependency` entry to the current changelog for every bump.
