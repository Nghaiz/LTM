# Report — adversarial audit of the server authority layer

**Date** 2026-08-16 · **Scope** `Assets/Scripts/Net/Server/**`, `Ironfront.Net.Replication/Server/**`, `Combat/**`
**Landed as** #81 (six defects), #82 (five subsystems) · **Filed** #83 (testability), #84 (flaky CI)

---

## What was audited and why

Two defects were reported by hand: `weaponId` always 0, and join admissions paired by
position. Both looked isolated. They were not — they are two instances of one pattern, and
sweeping the same files for that pattern found nine more.

**The pattern: a field, a hook or a writer that exists, is documented, is often covered by
its own unit tests, and that no production code path ever reaches.** Not a logic error. Not
something a reviewer catches by reading a diff, because each half of the code is correct in
isolation; what is missing is the line that joins them, and a missing line has no diff.

None of the eleven fails a test, throws, or logs. That is the whole difficulty.

---

## The eleven

| # | Defect | What a player saw | Landed |
|---|---|---|---|
| 1 | Admissions paired positionally, mis-pair confirmed as permanent | crash → cannot rejoin **at all**, not 60 s | #81 |
| 2 | `weaponId` never written | remote players hold nothing | #81 |
| 3 | Yaw never written server-side | remote players face their spawn heading while shooting elsewhere | #81 |
| 4 | `LagCompensator.Occlusion` never assigned | bullets pass through every wall | #81 |
| 5 | Input drain unbounded per tick | speed hack, `SpeedViolations` reads 0 | #81 |
| 6 | `S_DESPAWN_ACTOR` never sent; slot not reset | leaver becomes a shootable mannequin; next joiner spawns as a corpse | #81 |
| 7 | Respawn in place | spawn-camping is free | #82 |
| 8 | Hitbox history written under one tick number per catch-up | lag compensation silently off, load-correlated | #82 |
| 9 | `ForgetActor` wired for players only | trap-2 tables leak for every bot | #82 |
| 10 | `ActorIdPool` never called | quarantine never runs; `ActorIdsInUse` structurally 0 | #82 |
| 11 | `serverId` permanently 0 | another server's ticket is admitted | #82 |

Two were left deliberately: `MatchController.WorldResetRequested` has no subscriber (its
comment says "the spawner subscribes"; no spawner exists), and `ServerMasterReporter
.CollectScores` always returns empty (checklist A13).

---

## What the documentation got wrong, twice

Worth recording separately, because in both cases the comment was more reassuring than the
code and that is what kept the defect alive.

**`TicketValidator`** put the cost of a mis-pair at *"lapses on the ticket's own 60-second
expiry"*. But a mis-paired claim goes through `ConfirmConnected`, which writes
`long.MaxValue`, and `ExpireClaims` only drops claims that have a deadline. The real owner
could not rejoin until the mis-paired connection dropped, or the server restarted. A bounded
inconvenience in the docs; an unbounded one in the code.

**`NetServerBootstrap`** stated that *"ServerMasterReporter re-registers a stricter validator
once it has an id."* Nothing did. There was one construction site in the repo, it passed the
literal `0`, and the `WrongServer` rejection was dead code for the life of the project.

A comment describing an integration nobody wrote reads exactly like a comment describing one
that works.

---

## The fix that was wrong, and the test that said so

For the input flood (#5) the obvious fix is to clamp the tick's total displacement — the
per-step clamp bounds one frame and re-baselines every call, so bounding the tick looks like
restoring the promise the class doc already makes.

It went red. `SnapshotFlowIntegrationTests` over an impaired link reported 157 and 270 speed
violations, on an **honest** client. A client recovering from packet loss sends bunched
frames representing ticks it really did intend to move for, and a distance clamp punishes it
identically to a flooder.

What separates the two is **rate, not distance**. Honest input averages one frame per tick
however unevenly it is delivered. So the accepted fix meters frames with a budget refilling
one per tick, banking at most `MaxMissedInputTicks + 1` — the largest gap the coast path
already tolerates. Surplus stays in the ring rather than being dropped, so a throttled client
loses no intent.

The integration test earned its keep here. It is the only test in the suite that would have
caught this, and it caught it in the first run.

---

## Why CI was green throughout

Four of the eleven are MonoBehaviour code in `Assembly-CSharp`, and `Assets/` has no asmdef.
`build-test` compiles the libraries from source and never touches the Unity tree;
`UnitySyntaxCheck` is a Roslyn parser that resolves no types. So the Unity half of the server
has **no test coverage of any kind** — not thin coverage, none.

That is not fixable by adding the Test Framework. `Assets/Scripts/Net/Server` depends on
`Actor`, `Weapon` and `IngameUi`, all in `Assembly-CSharp`, and an asmdef cannot reference a
predefined assembly — so no test assembly can see `NetServerActor` until those three are
behind interfaces the Net layer owns. Filed as #83 with a proposed shape.

The workaround used in both PRs: **push the decision down into the netstandard library and
leave Unity as a dumb adapter.** Everything that moved down got real tests (`InputAuthority`,
`TicketValidator`, `ServerStateAudit`). Everything that had to stay up got a structural
argument and a manual Editor recipe. That split is honest, and it is also the reason nine
new tests exist for eleven fixes rather than eleven.

---

## Verification

- **999 tests pass**, 0 fail, after #82.
- Nine new tests, all in the netstandard library where CI can run them.
- `UnitySyntaxCheck`: 363 files parse clean at C# 9.
- `SpecChecker`: 65 constants match `protocol-spec.md`.
- Plugin DLLs rebuilt and committed in both PRs — a stale DLL is how #77 nearly shipped a
  Unity tree that would not compile.

### Not verified

- No Editor session was run. The four Unity-side fixes in #81 and the four in #82 are argued
  structurally, not observed.
- The occlusion linecast now runs once per resolved hit candidate. Unmeasured on the target
  2-vCPU VM. If it shows up in a profile the answer is a distance cutoff, not removing it.
- Respawn repositioning assumes `ActorManager.spawnPoints` is populated in the shipping
  scenes. A scene with none leaves the player where they were, as before.

---

## The lesson worth keeping

A test double looser than the real collaborator was the root cause recorded in the last
adversarial review. This one has a sibling: **a subsystem with passing unit tests and no
caller looks exactly like a working subsystem.** `ActorIdPool` has thorough tests. So does
`ServerEventWriter.WriteDespawn`. Both were dead.

Unit tests prove a unit does what it claims. They say nothing about whether anyone asks. The
check that would have caught six of these eleven is mechanical and cheap: for each public
writer, hook and setter in the server layer, grep for a caller outside its own tests. Worth
running before the next phase closes rather than after.
