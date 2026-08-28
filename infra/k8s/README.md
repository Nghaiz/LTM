# infra/k8s — a game server on a sandbox Kubernetes node

The LAN/dev counterpart to [`infra/compose/`](../compose/). Compose stays authoritative for
anything reachable from the Internet; this exists to put a **real UDP game server on a real
second machine** so that a two-client match stops being a thing only the scripted harness
has ever done.

Applied and verified on `debian-sandbox` (192.168.94.130) on 2026-08-28.

## What it is

One `Deployment`, one `ConfigMap`, one `Namespace`. No master, no TLS, no signed tickets —
`MasterLinkBootstrap` treats an unset `IRONFRONT_MASTER_HOST` as a supported state and says
so in the log: `master link: standalone — no host configured, matches will not be
advertised.` A standalone game server is the whole of what a LAN match needs.

```bash
kubectl apply -f gameserver-lan.yaml
kubectl -n ironfront logs deploy/game-server -f
```

Clients then dial the node directly:

```
IRONFRONT_CLIENT_HOST=192.168.94.130
IRONFRONT_CLIENT_PORT=27015
IRONFRONT_CLIENT_PLAYER_ID=<distinct per client, never 0>
IRONFRONT_CLIENT_DISPLAY_NAME=<distinct per client, or the killfeed is unreadable>
```

## Sharing a cluster that belongs to another project

This node also runs a DevOps Learning Platform (`dlp-*`, `traefik`, `monitoring`). The
manifest is written so that the entire Ironfront footprint is one namespace and reverts
with a single command:

```bash
kubectl delete namespace ironfront
```

Nothing outside `ironfront` is created, patched or referenced: no ClusterRole, no CRD, no
webhook, no apiserver flag, no NodePort-range change, no CNI or MTU change. The one
node-level resource claimed is **UDP 27015** via `hostNetwork` — check it is free first
(`ss -lnup | grep 27015`) and change the port in both the ConfigMap and the container port
if it is not.

**Never install `docker-ce` on a kubeadm node to get around an image problem.** It replaces
the `containerd.io` the kubelet is running on, and the cluster goes with it.

## Getting the image onto a node with slow egress

Measured on this VM: 43 KB/s to GitHub against the Windows host's 4.4 MB/s over the same
URL, so `kubectl` sat in `ContainerCreating` for 13 minutes on 23 MB of one layer. Path MTU
is also below 1500 (`ping -M do -s 1472` fails, 1450 passes) and
`net.ipv4.tcp_mtu_probing=1` did not recover the throughput. Lowering the host MTU or
clamping MSS would reach into Calico's configuration, which belongs to the other project.

So the image travels over the LAN instead, at 25 MB/s:

```bash
# On a machine with working egress
docker pull ghcr.io/nghaiz/ironfront-game-server:gameserver-v0.3.0
docker tag  ghcr.io/nghaiz/ironfront-game-server:gameserver-v0.3.0 \
            ghcr.io/nghaiz/ironfront-game-server:lan-v0.3.0
docker save -o gs.tar ghcr.io/nghaiz/ironfront-game-server:lan-v0.3.0
scp gs.tar nghaiz@192.168.94.130:/tmp/

# On the node
sudo ctr -n k8s.io images import --digests --all-platforms /tmp/gs.tar
sudo ctr -n k8s.io images tag \
  ghcr.io/nghaiz/ironfront-game-server:lan-v0.3.0 \
  ghcr.io/nghaiz/ironfront-game-server@sha256:c310da19a065c3dd3a39417d8ede252aa0f9e3c03cabf0cdc3a4a494f9627a40
sudo ctr -n k8s.io images check | grep ironfront   # expect: complete
```

**Save from a tag, never from a digest.** `docker save <ref>@sha256:…` writes an extra
anonymous wrapper index, and `ctr import` names it `import-<date>@sha256:…`. The kubelet
then normalises that to `docker.io/library/import-<date>@sha256:…`, which is not the string
containerd stored, and every container creation fails with:

```
Error: failed to check if this is a checkpoint image: failed to get image from
containerd "sha256:9d16b4a5…": image "docker.io/library/import-2026-08-28@sha256:b21ccb5e…": not found
```

That error names a checkpoint feature nobody asked for and a digest that appears nowhere in
the manifest, so it reads as anything but what it is. Deleting the stray reference is **not**
enough — containerd's CRI cache keeps the mapping, and restarting the kubelet does not clear
it (tried; it lives in containerd, and restarting *that* would bounce every container on the
node, including the other project's). Remove the image entirely and re-import an archive
saved from a tag, which carries exactly one manifest and one name.

## Two things to know before trusting a green

**`Running` is not `listening`.** There is deliberately no readinessProbe: a Unity UDP
listener cannot be probed honestly over TCP, and a probe that always passes would report
healthy on exactly the failure this server has had before — bound nothing, accepted nobody.
Verify with the two facts instead:

```bash
kubectl -n ironfront logs deploy/game-server | grep "player slots ready"
ss -lnup | grep 27015          # expect 0.0.0.0:27015, owned by Ironfront.Serve
```

**The kubelet may garbage-collect a side-loaded image.** It is not backed by a registry this
node can reach in reasonable time, so under disk pressure the eviction is not recoverable in
seconds. If the pod ever returns to `ErrImagePull`, re-run the side-load above.

## Reachability, measured rather than assumed

A UDP listener answers nothing to a malformed packet, so "no log line" proves nothing about
the path. Count datagrams at the kernel instead — send N from the client machine and read
the delta:

```bash
awk '/^Udp:/{n++; if(n==2) print $2}' /proc/net/snmp     # InDatagrams, before and after
```

200 packets sent from the Windows host on 2026-08-28 produced a delta of 239 (the surplus is
the server's own loopback traffic in the same window). That is the path proven, end to end.

## Security — read before forwarding a port

`IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS=1` with no shared secret means **any client can
join as any player id**. `EnvRegistry` states it plainly: *"DEVELOPMENT ONLY … a public
server running with it on is a server anyone can join as anyone."* That is fine on a NAT'd
VMware subnet and is not fine one router port-forward later. Before this port is reachable
from outside the LAN: set `IRONFRONT_SHARED_SECRET` on the server and every client, flip the
flag to `0`, and move to the compose topology with the master and TLS.
