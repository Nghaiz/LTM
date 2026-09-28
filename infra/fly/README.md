# fly.io — master server only

`deploy.sh` puts the **master server** on fly.io. The **game server is deliberately absent**,
and the section below is the reason rather than an omission.

The full stack (master + two game servers) was meant to live in [`infra/compose/`](../compose/)
on the Azure VM described in issue #78. **That VM is not answering** — checked 2026-09-14,
`20.214.142.73` refuses 27000, 27015 and 27016 alike, and issue #127 (its remaining checklist)
still references `ghcr.io/sagitoaz/...`, from before the repository moved to `Nghaiz/LTM`. Treat
#78 and #127 as history until somebody re-provisions it; this Fly app is the master that is
actually up.

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

### DECIDED 2026-08-31: won't-do, with a reopening condition

**The fly.io game server will not be built.** An item that is neither done nor decided reappears
in every audit, which is how this one reached P9 having been re-investigated three times.

**Why not, stated as a cost rather than as an obstacle.** Step (1) is a small change — a
bind-address setting on `UdpPeer`, defaulting to `IPAddress.Any` so nothing else moves. Step (2)
is not a change at all: a dedicated IPv4 on Fly is a **paid** resource, recurring, bought to
reach a deployment target the project does not need. The compose VM already carries the game
server, `27015/udp` has been confirmed open on it by pulling the published image by digest, and
one map with one deployment is the scope this project holds itself to. Spending money to add a
second way to run the same server is the definition of the scope creep `plan.md` § 5 rule 6
exists to refuse.

**What stays.** `infra/fly/` keeps the **master** server, which is deployed and working (#174) —
TCP 27000, an SQLite volume, digest-pinned, one machine. Fly's UDP limitation is specific to UDP;
none of it touches the master.

**Reopening condition — any one of these, and this decision is void:**

1. A second region or a second game-server deployment becomes a real requirement rather than a
   nice-to-have, with a named reason.
2. Fly announces UDP over public IPv6, which removes step (2) entirely and leaves only the cheap
   half.
3. The compose VM stops being available and a replacement is needed on short notice — in which
   case steps (1) to (3) above are the procedure, already written, and the only new decision is
   accepting the IPv4 cost.

**Do not re-derive this.** The three unblock steps above are correct and are the work if it is
ever taken up; what was missing was a decision, and this is it.

---

## Operational notes

- **Metrics are on loopback on purpose.** The payload is unauthenticated and reports player
  counts and game-server health. Do not add a `[[services]]` block for 27001.
  `MasterServerConfig.cs:270` parses the value with `IPAddress.Parse`, so only a literal IP is
  valid here.

  **This line used to say `fly ssh console -C 'curl -s 127.0.0.1:27001'`, and that cannot
  work for two independent reasons** — found by running it against the live app on 2026-09-14:

  1. **The endpoint is raw TCP, not HTTP.** The master says so itself at boot:
     `metrics endpoint on 127.0.0.1:27001 — try: nc 127.0.0.1 27001`. An HTTP GET gets nothing.
  2. **There is no `curl` in the image.** `exec: "curl": executable file not found in $PATH`.

  A `fly proxy 27001:27001` plus an HTTP fetch fails for reason 1 as well. What works is a raw
  TCP read through the proxy:

  ```bash
  fly proxy 27001:27001 --app kien-master-2026 &
  nc 127.0.0.1 27001            # or: python -c "import socket;s=socket.create_connection(('127.0.0.1',27001));print(s.recv(65536).decode())"
  ```

  The same raw-TCP shape caught out the protocol-10 staging deploy; it is a property of the
  endpoint, not of a platform.
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
- **Currently deployed** (2026-09-14T09:47Z, machine version 4, from the merge of #284):
  ```
  ghcr.io/nghaiz/ironfront-master@sha256:828425bc019c13d99c00089e52cd00074f8636b2c102fafa238e81a1a3788fad
  ```
  The machine carries `org.opencontainers.image.revision=c4d158334588b1793ec39027f3a647aad3e4ee64`,
  and its boot line reads `Ironfront Master Server — protocol v10`. Read both back rather than
  trusting this paragraph once it ages:

  ```bash
  fly image show --app kien-master-2026        # revision label
  fly logs --app kien-master-2026 --no-tail | grep -i "protocol v"
  gh api user/packages/container/ironfront-master/versions     --jq '.[] | select(.metadata.container.tags[]? == "develop") | .name'
  ```

  The previous entry here named the #174 build of 2026-08-25 and was three weeks stale, which
  is exactly how the app came to be serving **protocol 9 to protocol-10 clients** without
  anybody noticing: `fly status` said `started`, the TCP health check said passing, and neither
  is a claim about the wire version.
