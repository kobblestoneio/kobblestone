- [v0.3.0](#v030)

# v0.3.0

## Upgrade Notes

- Upgrade with the complete manifest of this release or the Helm chart: the operator needs additional permissions (managing `Server`s for `Network`s, `ConfigMap`s for the forwarding configuration of `Server`s, and watching `Deployment`s).
- `Network`s were reworked and are not compatible with previous versions. Recreate your `Network`s; there is no automatic migration:
  - `servers[]` entries are now managed by default: the network creates and configures the `Server`s itself, rendered from the new `serverTemplate` and each entry's `spec`. To keep an existing `Server` and its data, name it in `servers[].serverName`; it joins the network. Its spec is replaced by the rendered one, so put everything it needs (including `storage`) into `serverTemplate` or the entry's `spec`.
  - `servers[].targetRef` is only allowed together with the new `unmanaged: true`, which keeps the previous behavior: the proxy forwards players to the target without configuring it.
  - `serverTemplate` requires `type`, `version` and `eula`, which all servers of the network share; `servers[].spec` cannot override them. Both contain the other settings of `Server`, except those the network owns: managed servers have no `forwarding`, `offline`, `route`, `tcpRoute` or `service` settings and are only reachable through the proxy. `autoSleep.mode: Hibernate` is not supported for managed servers.
- Paper-based servers with forwarding (all Paper, Pufferfish, Purpur and Folia servers managed by a `Network`) get a `config/paper-global.yml` managed by Kobblestone, which replaces the server's file on every start. Other Paper settings in that file fall back to their defaults; changes to it on the server's volume are lost.
- The `NetworkPolicy` of `Server`s allows RCON from the operator's namespace, which the operator reads from its `POD_NAMESPACE` environment variable (set by the manifest and the Helm chart). Without it, `Command`s and `CommandGroup`s cannot reach servers in clusters that enforce `NetworkPolicy`s.
- The proxy now always uses Velocity's modern forwarding (`player-info-forwarding-mode = "modern"`). Unmanaged servers have to be configured with the network's forwarding secret (key `forwarding.secret` in the network's `Secret`), or with the new `Server` field `spec.forwarding`.

## Changes by Kind

### Feature

- `Network`s manage their servers: each entry of `servers` is a `Server` created and kept compatible by the network (forwarding with the network's secret, offline mode, only reachable through the proxy). Shared settings go into `serverTemplate`, per-server settings into `servers[].spec`, which is merged onto the template (objects field by field, other values replace the template's). Manual changes to managed `Server`s are reverted.
- Existing `Server`s can join a `Network` by naming them in `servers[].serverName`, keeping their data. A server that would lose its `storage` by joining is rejected instead.
- `serverDeletionPolicy` decides what happens to managed `Server`s that are removed from `servers` or whose `Network` is deleted: `Retain` (default) keeps them with their data as standalone servers, `Delete` deletes them.
- The proxy of a `Network` is restarted when its configuration changes, so changes of `servers` (added, removed, renamed or reordered servers, hostnames, `try`) and `proxy.config` take effect. Players connected to the proxy are disconnected. Servers that do not exist (yet) are part of the configuration as well, so their creation does not restart the proxy.
- `servers[].try` excludes a server from the servers players join first; it stays reachable via `/server` and its forced host.
- `Network`s report the readiness of the proxy (`status.proxyReadyReplicas` of `status.proxyReplicas`, `Proxy` column) and of their servers (`status.readyServers` of `status.serverCount`, `Servers` column), each server's phase in `status.servers`, and problems with server entries in `status.message` and as warning events. The proxy has a readiness probe, so it only counts as ready once it accepts connections.
- New `Server` field `spec.forwarding`: configures a server to accept players forwarded by a Velocity proxy. Supported for Paper, Pufferfish, Purpur and Folia (built in) and for Fabric and Quilt (installs FabricProxy-Lite and Fabric API from Modrinth). Managed servers of a `Network` get it automatically.

### Bug or Regression

- Fix Velocity migrating the generated `velocity.toml` on every start and warning about a deprecated configuration version: the config now sets `config-version` (`2.7`), unless `proxy.config` contains one.
- Fix `Command`s and `CommandGroup`s not reaching servers in clusters that enforce `NetworkPolicy`s: the server's `NetworkPolicy` only allowed RCON from `Backup`s. It now also allows RCON from the operator's namespace.
- Fix the server's `NetworkPolicy` blocking Minecraft and RCON traffic when `service.port` or `rcon.port` was changed: it used these `Service` ports instead of the ports in the `Pod`.
- Fix `Network`s not using player information forwarding: the generated `velocity.toml` did not set `player-info-forwarding-mode`, so the forwarding secret was not used unless it was set in `spec.proxy.config`.

### Documentation

- The API reference of all resources is now part of the website at [kobblestone.io/docs/reference](https://kobblestone.io/docs/reference), generated from the CRDs. It replaces the reference on doc.crds.dev.
