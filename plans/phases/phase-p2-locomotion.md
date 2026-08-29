# Phase P2 — remote bodies that walk

- **Plan:** [`../plan.md`](../plan.md) · **Closes:** the sliding-bodies defect · **Size:** M
- **Filed:** 2026-08-29, by playing the game and then reading the source.

---

## 1. The finding

`RemoteActorView.Apply` drives seven animator parameters:

```
crouch · prone · sprint · aiming · dead · ragdolled   (bools, RemoteActorView.cs:258-265)
pitch                                                  (float, :312)
```

The local body's animator is driven by two more, and by these two alone for locomotion:

```
movement x · movement y                                (Actor.cs:706-707)
```

**`RemoteActorView` never sets either.** Every remote body — every networked player, and every bot
a client sees through the replication path — therefore plays its idle or its stance clip while its
transform translates. That is the "trượt cứng đơ, chân không nhúc nhích" exactly: not a missing
animation clip, a missing parameter write.

**Scope of the negative claim:** `SetFloat` across `Assets/Scripts/Net/**` returns one call site,
`_hashPitch`. `movement x` / `movement y` appear in `Assets/Scripts/` at `Actor.cs:706-707` and
nowhere else.

---

## 2. The decision this phase must take first

`movement x` / `movement y` are the local body's **input** axes, in its own local space. A remote
body has no input — it has interpolated positions. Two ways to produce the pair, and they are not
equivalent:

| Approach | Cost | Risk |
|---|---|---|
| **Derive from the interpolated transform** — velocity between snapshots, projected into the body's local frame | No wire change, no protocol version bump, no server work | The value is a *consequence* of interpolation, so it inherits interpolation's jitter; needs smoothing, and a stationary body being pushed reads as walking |
| **Add the pair to the snapshot** | Exact — it is the same number the owner's own animator used | Costs wire bytes on the hottest message on the protocol, and `SnapshotField` is **8/8 full** (recorded when X-43 was closed by a session-side clip memory rather than a wire field, decision **O-D4**) |

**Take the first.** The wire is full, the value is cosmetic, and a cosmetic value that costs a
protocol version bump is the wrong trade. Record the decision and its reopening condition: if a
later phase needs the *owner's intent* rather than its displacement — a body strafing while
sliding on ice, an animation that must lead the movement — the derived value cannot express it and
the wire question reopens.

**Verify the 8/8 claim against `SnapshotField` before writing this down.** It is quoted from a
decision made on 2026-08-28 and this plan's own rule 3 says a carried-forward sentence is stale
until re-read.

---

## 3. Tasks

### 3.1 — Read the animator's real parameter set (S)

Open `Remote Actor Proxy.prefab`'s `Animator` in the Editor and enumerate the controller's
parameters and its locomotion blend tree. Two things must be established before any code:

- The parameter **names and types** on the remote proxy's controller are the same as the local
  actor's. They are two different controllers; the local one's names are known, the proxy's are not.
- The blend tree's **axis convention** — which of x / y is forward, and what magnitude corresponds
  to walk vs run. `Actor.cs:706-707` feeds it from the local movement vector; read what that vector
  is normalised to before deriving a matching one.

If the proxy's controller has no locomotion blend tree at all, that is a **larger finding than this
phase**: say so, stop, and report it rather than authoring a blend tree inside an animation-parameter
phase.

### 3.2 — Derive the pair in `RemoteActorView` (M)

From the interpolated position delta, projected into the body's local frame using the yaw the view
already applies. **No allocation** — this runs per interpolated actor per frame, and the class's own
remark says so in terms.

Handle explicitly, rather than letting the arithmetic decide:
- **A dead or ragdolled body** — do not feed locomotion into a corpse.
- **A body in a seat** — a driven vehicle moves its passenger; the passenger is not walking.
- **A teleport / respawn** — one frame's delta across a respawn is a very large velocity. The view
  already knows when it snaps; reuse that signal rather than thresholding a magnitude.
- **A stationary body** — must produce exactly zero, not a small jitter that reads as shuffling.

### 3.3 — A detector, observed RED (S)

The failure this must catch is *the parameter is never written*, which is what shipped. An
assertion that a remote body moved a known distance produces a non-zero locomotion magnitude, and
a stationary one produces zero. **Mutate it both ways**: delete the write (must go RED), and pin
the value to a constant (must also go RED — a constant satisfies "non-zero").

### 3.4 — Look at it (S)

Two clients, one walking, one watching, screenshots at three checkpoints. The legs move or they do
not, and no assertion answers that. This is the only acceptance criterion here that a green build
cannot fake.

---

## 4. Acceptance

| # | Criterion |
|---|---|
| 1 | The proxy controller's parameter names and blend-tree axis convention are recorded, read from the asset rather than assumed |
| 2 | The derive-vs-wire decision is written down with its reopening condition, and the 8/8 `SnapshotField` claim is re-verified rather than quoted |
| 3 | `movement x` / `movement y` are written for remote bodies, with dead / seated / respawn / stationary each handled explicitly |
| 4 | Zero allocation on the per-frame path |
| 5 | The detector is observed RED by both mutations before the fix |
| 6 | Screenshots from a two-client run show a remote body's legs moving |

---

## 5. Out of scope

- **The ragdoll rig and remote weapon models** (ledger **A-2**, `_actor`). WON'T-DO with a written
  reopening condition that the gate prints on every run. A remote death sliding to the floor at a
  fixed pose is a *different* defect from a remote walk not animating, and this phase does not
  touch it.
- **The local player's own animation.** `Actor.cs` already drives it; nothing here changes
  `Assembly-CSharp`.
