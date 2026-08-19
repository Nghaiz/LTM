# Phase 3A — The sixteen slots the server said it had

- **Track:** [`plan.md`](../plan.md) · **Parent:** [`phase-3-harness.md`](phase-3-harness.md) · **Effort:** M (3d)
- **Depends on:** nothing. This is the first thing that runs.
- **Unblocks:** phase-3 acceptance criterion 1, and therefore 3D and 3E.
- **Evidence:** [`2026-08-20-brainstorm-phase-3-completion.md`](../reports/2026-08-20-brainstorm-phase-3-completion.md) §§ 2–4

---

## 1. Goal

A dedicated server admits `Config.MaxConnections` networked players. Today it admits **one**, and
says otherwise in its own startup log.

This is not harness scaffolding. The project targets complete multiplayer on every feature, so a
one-player limit is a production defect and is fixed on the shipped server path.

## 2. What is actually broken

`ServerActorRegistry.TryClaimPlayerSlot` (`:122-137`) walks `_actors` for a body that is
`AvailableForPlayers && !IsClaimed`. Exactly one such body exists in the entire project:

| Asset | `_availableForPlayers` |
|---|---|
| `Assets/Prefab/Player Fps Actor.prefab` | 1 |
| `Assets/Prefab/Ai Character Optimizations.prefab` | 0 |
| `Assets/Prefab/Ai Character Optimizations 1.prefab` | 0 |

`Dustbowl.unity` carries **zero** `NetServerActor` components; the single body is instantiated at
runtime by `GameManager.cs:88` for the local player. Connection two onward fails the claim, and
`ServerTickLoop.OnClientConnected` (`:1266-1276`) answers with
`Transport.Disconnect(connectionId, DisconnectReason.ServerFull)` — a byte the client reads back
verbatim (`Connection.cs:400` writes it, `:212` reads it).

`NetServerBootstrap.cs:202` meanwhile logs `"{Config.MaxConnections} slots"` with
`_maxConnections = 16` (`:64`). Sixteen transport slots, one player slot, nothing comparing them.

## 3. Resolved decisions

| Decision | Resolution |
|---|---|
| Body prefab | `Ai Character Optimizations.prefab` — already carries `NetServerActor`, no camera / controller / prediction stack. AI driver disabled on claim. |
| Server shape | **Dedicated-only.** Nobody plays on the server process. |
| `GameManager`'s local body on the server path | Must **not** be claimable — otherwise the first remote player inherits the host's body. |
| `NetVerificationHarness.OpenSecondSlot()` | Deleted after a Pre-Delete Reference Check. |

`Player Fps Actor.prefab` was rejected as the pool body for the reason `OpenSecondSlot`'s own remark
gives: it carries camera, controller and prediction stack, and stripping them is the fragile step.

## 4. Work

### 4.1 `ServerPlayerSlotPool` (new)

**File:** `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerPlayerSlotPool.cs`

Owned by `NetServerBootstrap`. On server start, instantiates `Config.MaxConnections` bodies from the
AI character prefab with `_availableForPlayers = true` and the AI driver disabled, and registers
them (`NetServerActor.OnEnable` → `ServerActorRegistry.Register`, `:281`).

**Count comes from `Config.MaxConnections` — the same field `NetServerBootstrap.cs:197` hands the
transport.** One source, so the 16-vs-1 disagreement cannot reappear by drift.

`ProtocolConstants.MAX_ACTORS` caps the registry (`ServerActorRegistry.cs:68`); the pool must fit
under it alongside bots, and fail loudly rather than silently short-spawning if it does not.

**Eager, not lazy** — all bodies exist from server start. This is forced by pin 3: a lazily-grown
pool has no count to compare against `Config.MaxConnections` until it is full, which is exactly when
the comparison no longer catches anything. The cost is `MaxConnections` idle bodies on a server that
has no rendering; if that proves measurable, it is a Phase 4 measurement, not a planning guess.

### 4.2 Suppress the host body on the server path

`GameManager.cs:88` instantiates `Player Fps Actor` unconditionally. **Decision: on the dedicated
path it is not spawned at all**, rather than spawned and left unclaimable — a body that exists and
is merely not claimable still ticks, renders, and consumes one of the `MAX_ACTORS` registry slots
the pool is sized against.

### 4.3 Retire `OpenSecondSlot()`

Pre-Delete Reference Check first: `grep -rn "OpenSecondSlot"` across runtime, tests and editor.
Current reading is one hit, its own definition (`NetVerificationHarness.cs:168`). Remove the method
and `FindAiController` if that becomes unreferenced.

## 5. Pins — three, each red for its own reason

Per `pinned-baseline-test-companion.md` these assert the healthy state, not a baseline. Per
[[mutation-test-every-gate]] each is proven by mutating the real artifact until it goes red — one
mutation per fault claimed, recorded in the phase report.

| Pin | Goes RED when | Mutation that proves it |
|---|---|---|
| a second connection claims a slot | pool < 2 | set pool size to 1 |
| connection `MaxConnections + 1` is refused `ServerFull` | pool exceeds transport capacity | set pool size to `MaxConnections + 1` |
| claimable-body count `==` `Config.MaxConnections` | the two numbers diverge again | change `_maxConnections` without the pool following |

Pin 3 is the one that stops `NetServerBootstrap.cs:202` from advertising capacity again.

**There is currently zero test coverage of `TryClaimPlayerSlot`** — `grep -rln
"TryClaimPlayerSlot"` returns only its definition, `ServerTickLoop`, and the editor harness. These
pins are new ground, not an extension.

## 6. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerPlayerSlotPool.cs      (new)
Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerBootstrap.cs        (wire the pool)
Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/GameManager.cs          (§ 4.2)
Ironfront_Reborn/Assets/Editor/NetVerificationHarness.cs               (§ 4.3 deletion)
Ironfront_Reborn/Assets/Tests/EditMode/**                               (§ 5 pins)
plans/debt-closure/reports/                                             (phase report)
```

Does not touch `Ironfront.Net.Transport/**` or `Ironfront.Net.Protocol/**`. The transport was never
wrong.

## 7. Acceptance criteria

1. `dotnet test` green; Unity EditMode suite green; solution builds under `TreatWarningsAsErrors`.
2. All three § 5 pins pass, and the phase report records the mutation that made each one red.
3. `--smoke` (2 clients, 30 s) connects and both processes exit 0 — phase-3 AC-1, if § 8 does not
   also hold.
4. `grep -rn "OpenSecondSlot"` returns zero hits, or the phase report states why one remains.
5. The claimable-body count and the number in the startup log are read from one source.

## 8. Known unknown

`BadSignature` is a **separate** failure of a **different** client and is Phase 3B's subject. If it
survives this phase, AC-1 stays red and 3B runs before 3D. That is recorded here rather than
discovered later.

## 9. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| AI prefab body lacks a component combat/vehicle paths assume | 3 | 4 | 12 | Pin a claimed body through drive → damage → death (ledger **B-11** wants this anyway) |
| Pool + bots exceed `MAX_ACTORS` | 3 | 3 | 9 | Fail loudly at start with both numbers; never short-spawn |
| Suppressing the host body breaks offline play | 3 | 4 | 12 | Gate on the server path only; run an offline smoke before merge |
| A pin passes vacuously | 2 | 5 | 10 | § 5 mutation column is the gate, not the assertion |

## 10. Handoff

To **3B**: whether `BadSignature` survives, with the Editor log.
To **3D/3E**: a server that admits N players — the precondition every Lane B check assumes.
