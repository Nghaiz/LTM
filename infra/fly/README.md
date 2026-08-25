# fly.io — master server only

`deploy.sh` puts the **master server** on fly.io. The **game server is deliberately absent**,
and the section below is the reason rather than an omission.

The full stack (master + two game servers) lives in [`infra/compose/`](../compose/) on the
Azure VM described in issue #78. Nothing here replaces it.

| File | What it is |
|---|---|
| `master.toml` | Fly app config for the master: TCP 27000, SQLite volume at `/data`, metrics on loopback |
| `deploy.sh` | Creates app + volume on `FIRST_RUN=1`, otherwise deploys one digest-pinned image |

```bash
# once
FIRST_RUN=1 ./infra/fly/deploy.sh
fly secrets set IRONFRONT_SHARED_SECRET="<key>" --app kien-master-2026

# every deploy
IRONFRONT_MASTER_IMAGE=ghcr.io/nghaiz/ironfront-master@sha256:... ./infra/fly/deploy.sh
```

The digest comes from the summary of an `images` workflow run. `deploy.sh` refuses to run
without it, because `.github/workflows/images.yml` pushes branch, semver and timestamp tags
and **never pushes `latest`** — a config naming `:latest` fails with `manifest-unknown`.

---

## Why the game server is not deployed here

Two independent constraints, both from Fly's own
[UDP and TCP docs](https://fly.io/docs/networking/udp-and-tcp/):

> "You'll need a dedicated IPv4 address for your app to accept UDP packets. **We don't support
> UDP over public IPv6.**"

> "You usually need to explicitly bind your UDP service to `fly-global-services`. Sorry, but
> `0.0.0.0`, `*`, and `INADDR_ANY` generally won't do."

Against that:

1. **The design is IPv6-only.** That is the premise of the whole fly.io proposal, and it is
   the one configuration in which Fly carries no UDP at all. An IPv4 address has to be
   allocated (`fly ips allocate-v4`), and a dedicated v4 is a paid resource.
2. **`UdpPeer.cs` binds `IPAddress.Any`.** See
   [`Ironfront.Net.Transport/UdpPeer.cs:92`](../../Ironfront.Net.Transport/UdpPeer.cs#L92) —
   `_socket.Bind(new IPEndPoint(IPAddress.Any, bindPort))`, with no bind-address knob. That is
   precisely the binding Fly says will not receive packets.

A `gameserver.toml` that ignores both would deploy, pass its health check, register with the
master, and then silently receive nothing — the same shape of failure `EnvRegistry.cs` already
warns about for a missing scene: *"the process sits healthy and unreachable."* That is worse
than having no file, so the file was withdrawn rather than shipped.

**To unblock, in this order:**

1. Give `UdpPeer` a bind-address setting (default `IPAddress.Any`, so nothing else changes) and
   resolve `fly-global-services` for the Fly path.
2. Allocate a dedicated IPv4 on the game-server app and accept the cost.
3. Set `IRONFRONT_GAMESERVER_SCENE` and `IRONFRONT_GAMESERVER_PUBLIC_IP` in the app config.
   Without the first, no scene loads and nothing binds the port. Without the second,
   `GameServerConfig.cs:109` falls back to `IPAddress.Any` and the master advertises `0.0.0.0`
   to clients.

Until (1) and (2) are done, the game server belongs on the compose VM.

---

## Operational notes

- **Metrics are on loopback on purpose.** The payload is unauthenticated and reports player
  counts and game-server health. Read it from inside the VM:
  `fly ssh console --app kien-master-2026 -C 'curl -s 127.0.0.1:27001'`. Do not add a
  `[[services]]` block for 27001. `MasterServerConfig.cs:270` parses the value with
  `IPAddress.Parse`, so only a literal IP is valid here.
- **TLS terminates at Fly's edge.** `handlers = ["tls"]` on port 27000, so
  `IRONFRONT_TLS_CERT_PATH` is empty and the master serves plaintext inside the container. A
  game server dialling this master sets `IRONFRONT_GAMESERVER_MASTER_TLS=1` against
  `kien-master-2026.fly.dev` and validates Fly's certificate — it does **not** use the
  self-signed pin flow in [`infra/tls/`](../tls/), which is for the compose VM.
- **One machine, enforced by `--ha=false`.** Fly provisions a standby otherwise, and two
  masters share neither the SQLite volume nor the connection state.
- **The GHCR package is public, so Fly needs no registry credentials.** Checked 2026-08-25:
  `gh api user/packages/container/ironfront-master` reports `visibility=public`. Note the
  sibling `ironfront-gameserver` (no hyphen) is a private, abandoned 2026-08-18 build — the live
  package is `ironfront-game-server`, with the hyphen.
- **Latest master digest on `develop`** (2026-08-25T15:19:38VN, from the merge of #174):
  ```
  ghcr.io/nghaiz/ironfront-master@sha256:5c1770f87e2ff8ff14f1a46d2c09649965fa86d29fa46cdeb482a5f4131da23c
  ```
  Re-read it rather than copying this line once it ages:
  `gh api user/packages/container/ironfront-master/versions --jq '.[0].name'`.
