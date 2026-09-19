## What is Kobblestone?

Kobblestone allows you to manage Minecraft server infrastructure on Kubernetes through custom resource types, turning
Minecraft into a first-class citizen of Kubernetes.

- [Features](#features)
- [CRD Overview](#crd-overview)
- [Getting Started](#getting-started)
- [Special Thanks](#special-thanks)

## Features

- Supports Minecraft servers of many different types and versions (Vanilla, PaperMC, Purpur, Fabric, etc.).
- Hostname based routing across multiple servers.
- Combine multiple servers to build a network behind a [Velocity](https://papermc.io/software/velocity/) proxy.
- Live and offline on-demand server backups with support for multiple storage backends (S3, PVC).
- Native [Gateway API](https://kubernetes.io/docs/concepts/services-networking/gateway/) integration for exposing
  servers, networks and routers (also supports LoadBalancer Services).
- Execute server commands declaratively via Kubernetes resources.
- Automatic server scale-down/-up based on player inactivity.
- Bot programming and deployment based on [Mineflayer](https://github.com/PrismarineJS/mineflayer).
- Manage Microsoft accounts and device-flow authorization for authenticated bots.

## CRD Overview

- `Server`: Manages a Minecraft server instance.
- `Router`: Defines a router that is able to route Minecraft traffic based on hostnames.
- `Route`: Defines a routable hostname and target.
- `Network`: Manages and configures a [Velocity](https://papermc.io/software/velocity/) proxy instance.
- `BackupRepo`: Defines a storage backend for backups. Supports S3 buckets and PVC.
- `Backup`: Creates a backup of a server and stores it inside the referenced repo. Supports live backups coordinated via
  RCON.
- `Restore`: Restores a backup on a server.
- `Command`: Execute a command on a server via RCON and capture the response.
- `CommandGroup`: Creates and executes multiple commands in sequential order.
- `Account`: Manages the authorization state for a Microsoft account.
- `BotBehavior`: Defines the behavior/logic of a bot.
- `Bot`: Manages a Minecraft bot instance.
- `BotGroup`: Manages multiple instances of the same bot against different target servers.

## Getting Started

### Installing Kobblestone

Kobblestone can be installed via a simple `kubectl apply` command:

```batch
kubectl apply -f https://get.kobblestone.io/v0.1.0/kobblestone.yaml
```

### Deploying a simple Vanilla server

Define your server in a `server.yaml` file...

```yaml
apiVersion: kobblestone.io/v1alpha1
kind: Server
metadata:
  name: my-server
spec:
  eula: true
  type: Vanilla
  version: "26.2"
  storage:
    size: 1Gi
  resources:
    limits:
      memory: 2Gi
  jvm:
    memory:
      # You may want to decrease this for lower memory limits
      heapPercentage: 75
```

... and apply.

```batch
kubectl apply -f server.yaml \
    && kubectl wait --for=condition=Running=True server/my-server --timeout=5m
```

The command will wait for the server to be up and running. This may take some time if launched for the first time.

### Exposing your server outside the cluster

By default, servers are only available inside the cluster. You have multiple options for exposing your server, which are
more or less applicable depending on your specific environment.

**Option A: LoadBalancer Service** (not recommended)

The simplest and most straight forward option is to make the servers `Service` of type `LoadBalancer`. If supported by
your environment, it will give your server an external IP where it is reachable.

**Warning**: Beware that if you are in the cloud, this will probably allocate resources that cost money. It is also
recommended to disable RCON if the external IP is routable directly from the internet. Even though RCON is
password-protected, it is still unencrypted.

```yaml
apiVersion: kobblestone.io/v1alpha1
kind: Server
metadata:
  name: my-server
spec:
  # ...
  service:
    type: LoadBalancer
    annotations: { } # Configure environment specifics of LB here (like MetalLB ip-pool)
  rcon:
    disabled: true
```

**Option B: Gateway API**

If you have [Gateway API](https://kubernetes.io/docs/concepts/services-networking/gateway/) >= v1.6.0 installed on your
cluster, this might be the most optimal option for you.

You can easily attach servers (and other resources) to your gateways. Kobblestone will manage the `TCPRoute` for you.

```yaml
apiVersion: kobblestone.io/v1alpha1
kind: Server
metadata:
  name: my-server
spec:
  # ...
  tcpRoute:
    parentRefs:
      - name: my-gateway
        sectionName: minecraft # TCP listener on your gateway
```

Your server will then be accessible through the gateways TCP listener.

**Option C: Router**

You can also expose your server indirectly through a central `Router` instance. The advantage here is that we only have
to expose one component (the router) and all the servers stay cluster internal. In addition to that, we also have
hostname based routing.

For that, we need to deploy and expose a `Router` first.

```yaml
apiVersion: kobblestone.io/v1alpha1
kind: Router
metadata:
  name: my-router
spec:
  replicas: 1 # You can scale routers horizontally
  # You can use a LoadBalancer Service...
  service:
    type: LoadBalancer
  # ... or the Gateway API
  tcpRoute:
    parentRefs:
      - name: my-gateway
        sectionName: minecraft # TCP listener on your gateway
```

Secondly, we can configure the default route on the server.

```yaml
apiVersion: kobblestone.io/v1alpha1
kind: Server
metadata:
  name: my-server
spec:
  # ...
  route:
    parentRef:
      name: my-router
    hostname: server.example.com # Some hostname that resolves to the routers external endpoint
```

## Special Thanks

- [itzg](https://github.com/itzg) for
  creating [docker-minecraft-server](https://github.com/itzg/docker-minecraft-server), [mc-router](https://github.com/itzg/mc-router), [docker-mc-backup](https://github.com/itzg/docker-mc-backup)
  and [docker-mc-proxy](https://github.com/itzg/docker-mc-proxy). These awesome projects provide core functionality for
  Kobblestone. Check them out!
- [PrismarineJs](https://github.com/PrismarineJS) for creating [mineflayer](https://github.com/prismarinejs/mineflayer).
  Kobblestone uses it to implement bots.