# Step 03 — Guard the singletons for headless

**Feeds** Dev A phase-00 task 5 · **Session size** medium · **Editor needed after** Dev A runs headless

> Goal: the dedicated-server build stops throwing `NullReferenceException` at every UI singleton,
> because the code asks whether it is a client before reaching for one.

---

## Why it matters beyond headless

Phase-00 objective 4 calls a crashing headless build *"a project-blocking risk"* — architecture
decision AD-2 assumes one codebase produces both the client and the server. But the reason to do it
now is narrower: `Assembly-CSharp/` currently references `NetContext` **zero times**. Until it does,
the game has no idea which half of the netcode it is running in, and every later step has to invent
that answer locally.

This step is where the original game starts knowing what it is.

## The list

`phase-00-foundation.md` task 5 enumerates the 21 singletons and the guard each needs. Three
categories:

| Category | Guard | Examples |
|---|---|---|
| Present on the server | none needed | `ActorManager`, `GameManager`, `PathfindingManager`, `CoverManager`, `LevelBounds`, `DistanceField` |
| Client-only, gameplay | `if (NetContext.IsClient)` | `FpsActorController.instance`, `PlayerFpParent.instance` |
| Client-only, UI | `#if !UNITY_SERVER` | `IngameUi`, `IngameMenuUi`, `LoadoutUi`, `ScoreUi`, `MinimapUi`, `MinimapCamera`, `SceneryCamera`, `DecalManager`, `OptionsUi` (plus server defaults) |

Take the call-site list from step 01's deliverable rather than re-deriving it; that is what step 01
gathers it for. If step 01 has not been done, derive it with a grep and say so in the PR.

## The trap worth naming

**`#if !UNITY_SERVER` and `if (NetContext.IsClient)` are not interchangeable.** The first is
compile-time and removes the code from the server binary entirely — correct for UI, which the server
build has no reason to contain. The second is runtime and keeps one binary able to be either —
correct for gameplay singletons that a listen-server or an in-Editor test genuinely needs on both
sides.

Using the compile-time form on a gameplay path produces a server that cannot host a local client, and
that failure appears weeks later as "the loopback test stopped working". Follow the plan's column;
it already made this call per singleton.

`OptionsUi` is the one with a third behaviour: the server needs the *values* without the UI, so it
needs a default provider rather than a guard.

## What this step proves, and what it does not

**Proves:** it compiles for both configurations, and `dotnet build` stays warning-free.

**Cannot prove:** that the headless build survives. Phase-00 criterion 2 is *"runs for 10 minutes
without crashing, grep `Exception` in the log returns 0"* and criterion 3 is *"bots spawn and move"* —
both need a Unity build.

**Dev A checks:** `tools/build-server.ps1`, then run the output with `-batchmode -nographics
-logFile s.log` for ten minutes and grep the log. A `NullReferenceException` naming a singleton is a
guard this step missed; send the name back rather than patching locally.

## Done when

- Every client-only singleton in the task-5 table is guarded with the form that table specifies
- `Assembly-CSharp/` references `NetContext` — the count is no longer zero
- Merged and green, with the headless run named in the PR body as outstanding
