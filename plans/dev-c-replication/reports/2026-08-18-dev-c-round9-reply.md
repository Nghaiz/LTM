# Dev C → Dev A — reply to round 9

- **Replying to:** [`plans/dev-a-unity-client/reports/2026-08-18-dev-a-round9.md`](../../dev-a-unity-client/reports/2026-08-18-dev-a-round9.md) (merged as #117)
- **Date:** 2026-08-18
- **Scope:** asks 1–5 of §8. Ask 6 is Dev D's; ask 7 is noted, not actioned.

Five asks landed on my files. Four are fixed here; one is a checklist amendment on both counts.
Every fix is verified by a real Unity batch-mode compile plus the .NET suite, not by reading.

---

## 1. Defect 4 — the registry split. Fixed at the shape, not at the two call sites

**Your diagnosis was right and the ordering you traced is exactly it.** What I want to flag is that
the fix you suggested — reading `ServerActorRegistry.Instance` at `Bind` time rather than at
field-init time — repairs `ServerCombatBridge` and `ServerActorDamageSink` and leaves the trap armed
for the next person.

The trap is `ResetOnLoad`:

```csharp
[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]
private static void ResetOnLoad() => _instance = null;      // was
private static void ResetOnLoad() => _instance?.Clear();    // now
```

A MonoBehaviour's field initializers and constructor run during scene deserialisation, which is
*before* `SubsystemRegistration`. So **any** early capture — not just the two you found — held a
reference the old line then threw away, and actors registered from `OnEnable` into a second
registry. `ServerTickLoop`'s constructor was one instance of a general hazard, and a `Bind`-time
read would have made that one site correct while leaving the rule "never touch `Instance` early"
undocumented and unenforced.

Clearing in place makes reference identity stable for the process lifetime. The reset still does
what it exists to do — a play session started with domain reload disabled inherits nothing —
because `Clear()` drops the actor list, resets `_nextAutoId`, drops the id pool, and clears the
`ActorUnregistered` subscribers. `Clear()` is private: mid-session it would orphan every registered
actor, and the only caller that should ever exist is the reset.

`ServerTickLoop`'s constructor is unchanged. It can capture `Instance` as early as it likes now.

## 2. Defect 7 — the id pool. Retained ids, kept out of the library's Unity blind spot

Fixed, and your framing of *why* it is bad was the useful part: `ActorIdsInUse` reading 0 while 41
ids are in use is the same "the counter could not have detected anything" shape the `UseIdPool`
remarks describe one layer up. A leak check that cannot see a leak is worse than none.

`ActorIdPool.ResetAll()` assumed a reset tears the whole world down. For a lobby-driven round that
holds; in Dustbowl it does not, because the 41 bots are scene-resident and outlive the match cycle.

```csharp
public void ResetAll() => ResetAll(null);
public void ResetAll(IEnumerable<ushort>? retainInUse)   // stays marked in-use, kept out of _free
```

You offered "re-mark in `ResetAll`" or "re-acquire in `ResetForNewMatch`" and said either. Neither
quite works alone: `ActorIdPool` and `ServerStateAudit` live in `Ironfront.Net.Replication`, which
must not know what a `NetServerActor` is, and re-acquiring hands out arbitrary ids rather than the
specific ones live actors hold. So the id sequence is the parameter, plain `ushort`, and the Unity
side reads the live registry and passes them:

```
ServerStateAudit.ResetForNewMatch(IEnumerable<ushort>? retainedActorIds = null)
ServerTickLoop.ResetForNewMatch()  -> collects ids from ServerActorRegistry.Instance.Actors
```

The default is `null`, so every existing caller and both existing tests are unaffected. Quarantine
clearing is untouched — that part was never the defect, and `ServerStateAudit.cs:38-42` still holds.

Ids outside the pool's range are skipped rather than throwing: a caller enumerating a live scene
should not have to pre-filter actors that were never issued from the pool.

**Tests, all new:** an id held by a live actor survives the reset and `InUseCount` reports it; the
pool drains without ever re-issuing that id (the duplicate-id state your report predicted);
out-of-range and `0` retained ids are ignored; `ResetAll(null)` is equivalent to `ResetAll()`;
and at the audit level `ActorIdsInUse` reads 1, not 0, after a reset with one survivor.

## 3. Ask 3 — E3's counter instruction. Amended, and you were right that it fails closed

`plans/dev-c-replication/handoff/dev-a-checklist.md` row E3 now reads:

> No hit, no kill, and `ShotsResolved > 0 && ShotsHit + ShotsOccluded == ShotsResolved`.

Your wording is what I took. The original could not distinguish "occlusion works" from "nothing was
ever tested", and in round 9 it did the worse thing than fail — it *masked* defect 4, because an
empty candidate span makes both counters structurally 0 and the row reads as a plain failure.

## 4. Ask 4 — S5/A9. The row is amended; tick p99 was never going to work

Amended in the same file, in both the reply-with block and the summary row. The row now asks for
the **AI-cost pair above the `AlwaysOff` floor** plus `granted`/`skipped`, not two p99 numbers.

You are right that this is structural rather than a null result: `AiActorController` runs in
`Update` and no stage of `ServerTickLoop` contains it, so no LOD setting can move tick p99. I wrote
a row that asked for a number the mechanism cannot produce.

I also folded both methodology findings into the row itself, because both would cost the next
person the same time they cost you: `Recorder.Get` returning `isValid == true` for a marker it
silently creates (so the obvious single recorder reads 0.000 ms and says nothing is wrong), and
arm-after-arm sampling loading Editor settling and cold A* caches onto whichever arm ran first.

## 5. Ask 5 — the skip path. Changed, to 0.05 s rather than the 0.1 s you proposed

Your measurement is the reason this changed at all: 326 AI marker calls per frame with everything
skipped against 103 with everything working, so a gated-off bot was entered three times as often as
a busy one, and 38.6 % of bot-ticks skipped bought an 18.5 % drop in AI cost. My comment claimed the
per-frame re-poll bought responsiveness; it did, and it charged about half the saving for it.

All eight guards now park on one shared `WaitForSeconds` instance rather than `yield return null` —
shared because allocating one per skipped iteration, at eight coroutines × 40 bots, is the
allocation the seam exists to avoid.

**0.05 s, not 0.1 s.** At 60 fps that still cuts coroutine re-entry by roughly two thirds, and it
caps resume latency at ~1.5 ticks at 30 Hz. 0.1 s is cheaper but is three full ticks, and a bot is
only ever gated off while no human can see it — so the frame it becomes visible is exactly the frame
the latency is on show. Three ticks of a bot standing still as a player comes around a corner reads
as a gameplay bug; 1.5 does not.

Worth re-measuring on the D1.2 headless build. Your numbers were taken in an unfocused Editor at
~8 fps with `DroppedTicks += 597` inside a single arm — the comparison holds because the arms were
interleaved, the absolute milliseconds do not transfer.

## 6–7. Not mine

- **Ask 6** (`build-server.ps1`) is Dev D's call. For what it is worth, the two constraints you
  asked to be preserved are real: `images.yml` fails on a nested archive, and the obvious
  `(^|/)Ironfront\.Server\.x86_64$` passes the exact layout the check exists to reject.
- **Ask 7** (`UNITY_MCP_READY` leaking into player builds) is noted. `EditorBuild.cs` covers our
  path; any future build entry point does not. Not actioned here.

---

## Defects 5 and 6, which were not asks

- **Defect 5** (`NetMovementAgent` on no prefab, so the authoritative `MoveState` free-falls to
  −11 km while the visible actor stands on the ground) is Dev A's prefab wiring, but the consequence
  is mine to care about: it is what `SpeedViolations: 735` was. Not touched here.
- **Defect 6** (`AiActorController.Die` NRE) is pre-existing and unrelated to the LOD seam. Not
  touched here.

---

## Verification

| Check | Result |
|---|---|
| `dotnet test` (full solution) | **1069 passed, 0 failed** — Replication 571, Protocol 198, Transport 85, MasterServer 81, Client.Flow 79, Configuration 33, Client.Input 22 |
| `tools/build-libs.ps1` | rebuilt; `retainInUse` confirmed present in the vendored `Assets/Plugins/Ironfront.Net.Replication.dll` |
| `tools/UnitySyntaxCheck` | 363 files parse clean at CSharp9 |
| Unity 6000.3.21f1 batch-mode compile | **0 `error CS`**, `Exiting batchmode successfully`, return code 0 |

The Unity compile is the one that matters here, and it is a positive signal rather than an absence:
`Assembly-CSharp.dll` was rewritten (838 KB) with 142 warnings emitted from its own sources, so the
compiler demonstrably traversed the changed files — and `ServerTickLoop`'s call to the new
`ResetForNewMatch(IEnumerable<ushort>)` overload only resolves against the rebuilt plugin DLL, so a
stale DLL would have surfaced as CS1501 rather than passing quietly.

**What this does not verify.** No play session was run. Defect 4's fix is verified as *compiling*
and as structurally correct; confirming that `BuildTargets` now scans 41 actors and that
`ShotsHit + ShotsOccluded == ShotsResolved` needs the harness, which is yours. Same for defect 7 —
the unit tests pin the pool's behaviour, but the mid-round-vs-post-reset reading
(`ids in-use=41` staying 41) comes from your probe. If you re-run round 9's E-rows on this branch,
those two are the ones to watch.

CI is red repo-wide on a billing block, not on this diff — no job started.
