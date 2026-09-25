# Authoritative Throwable Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give frag, spearhead, ammo box, and medipack one Ravenfield-faithful, server-authoritative inventory and release lifecycle with deterministic client reconciliation and correct projectile visuals.

**Architecture:** An engine-free `ThrowableLifecycle` owns accept, delayed release, refill, cancel, and rollback transitions inside `Ironfront.Net.Replication`. The server advances pending releases on simulation ticks and converts transition results into Unity launch requests; the client installs snapshot truth and replays unacknowledged throwable commands. Unity weapon fields are mirrors at network roles and retain their original mutation only offline.

**Tech Stack:** C#/.NET 8 libraries and xUnit, Unity 6 C# scripts and EditMode tests, YAML Unity scenes/prefabs, GSP protocol v11.

**Spec:** `docs/superpowers/specs/2026-09-25-authoritative-throwable-lifecycle-design.md`

## Global Constraints

- Preserve Ravenfield Beta 5 quantities and authored release delays exactly.
- Preserve ordinary-gun cooldown, clip, reserve, recoil, reload, and trigger behavior.
- The server is the only gameplay writer in a network match; Unity counters are mirrors there.
- Keep offline single-player behavior on the original `Weapon`/`ThrowableWeapon` path.
- Do not reset, overwrite, or silently absorb unrelated dirty-worktree changes.
- No production change is written before its focused test has failed for the expected reason.
- Protocol peers must reject v10/v11 mismatch rather than attempt degraded synchronization.

## Review Focus

- A held or duplicated fire input while release is pending must create exactly one projectile and spend exactly one use; covered in Task 2.
- A switch/death/reset one tick before release must restore the held use and emit no projectile; covered in Tasks 2 and 4.
- A snapshot that acknowledges the trigger before the release must retain predicted pending state instead of restoring a second throwable; covered in Task 5.
- A server launch failure must roll inventory back and log/count the failure; covered in Task 4.
- Frag and spearhead share a component but must remain different from announcement through scene instantiation; covered in Task 7.

---

### Task 1: Pin protocol-11 pending-release semantics

**Files:**
- Modify: `Ironfront.Net.Protocol/ProtocolConstants.cs`
- Modify: `Ironfront.Net.Protocol/Enums/GameplayEnums.cs`
- Modify: `plans/00-shared/protocol-spec.md`
- Modify: `Ironfront.Net.Protocol.Tests/Conformance/SnapshotTests.cs`
- Create: `Ironfront.Net.Protocol.Tests/Conformance/WeaponStateFlagTests.cs`

**Interfaces:**
- Produces: `WeaponStateFlags.PendingRelease = 1 << 1` and `ProtocolConstants.PROTOCOL_VERSION = 11`.
- Consumes: the existing five-byte `SnapshotField.Weapon` encoding; no packet grows.

- [ ] **Step 1: Write the failing protocol tests**

```csharp
[Fact]
public void PendingReleaseHasAStableWireBit()
{
    Assert.Equal(2, (byte)WeaponStateFlags.PendingRelease);
}

[Fact]
public void SnapshotRoundTripsReloadAndPendingReleaseTogether()
{
    ActorSnapshotEntry entry = FullWeaponEntry();
    entry.WeaponStateFlags = WeaponStateFlags.Reloading | WeaponStateFlags.PendingRelease;
    ActorSnapshotEntry decoded = RoundTrip(entry);
    Assert.Equal(entry.WeaponStateFlags, decoded.WeaponStateFlags);
}

[Fact]
public void ThrowableLifecycleShipsAsProtocolEleven()
    => Assert.Equal(11, ProtocolConstants.PROTOCOL_VERSION);
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test Ironfront.Net.Protocol.Tests/Ironfront.Net.Protocol.Tests.csproj --filter "WeaponStateFlagTests|SnapshotRoundTripsReloadAndPendingReleaseTogether" --no-restore`

Expected: failure because `PendingRelease` is absent and the version is 10.

- [ ] **Step 3: Add the flag and versioned specification row**

```csharp
[Flags]
public enum WeaponStateFlags : byte
{
    None = 0,
    Reloading = 1 << 0,
    PendingRelease = 1 << 1,
}
```

Set `PROTOCOL_VERSION` to 11 and add a v11 spec-history row explaining that the byte width is
unchanged but the reserved bit gained mandatory behavior.

- [ ] **Step 4: Run protocol tests GREEN**

Run: `dotnet test Ironfront.Net.Protocol.Tests/Ironfront.Net.Protocol.Tests.csproj --no-restore`

- [ ] **Step 5: Commit only Task 1 files**

```bash
git add Ironfront.Net.Protocol/ProtocolConstants.cs Ironfront.Net.Protocol/Enums/GameplayEnums.cs plans/00-shared/protocol-spec.md Ironfront.Net.Protocol.Tests/Conformance/SnapshotTests.cs Ironfront.Net.Protocol.Tests/Conformance/WeaponStateFlagTests.cs
git commit -m "feat(protocol): describe pending throwable release"
```

### Task 2: Build the pure throwable state machine

**Files:**
- Create: `Ironfront.Net.Replication/Combat/ThrowableLifecycle.cs`
- Modify: `Ironfront.Net.Replication/Combat/WeaponModel.cs`
- Modify: `Ironfront.Net.Replication/Combat/WeaponCatalog.cs`
- Create: `Ironfront.Net.Replication.Tests/ThrowableLifecycleTests.cs`

**Interfaces:**
- Produces: `ThrowableLifecycle.TryBegin`, `TryRelease`, `Cancel`, `RollbackRelease`, and `ThrowableTransition`.
- Produces: `WeaponConfig.ReleaseDelayTicks` and pending fields on `WeaponRuntimeState`.
- Consumes: `ActorAmmoSource`, `SpareAmmo`, `WeaponDelivery`, `Vec3`, and simulation ticks.

- [ ] **Step 1: Write RED ground-truth tests for all four throwable types**

```csharp
[Theory]
[InlineData(WeaponIds.FRAG, 1, 1, 2, 29)]
[InlineData(WeaponIds.SPEARHEAD, 1, 2, 3, 29)]
[InlineData(WeaponIds.AMMO_BAG, 1, -1, 1, 10)]
[InlineData(WeaponIds.MEDIPACK, 1, -1, 1, 10)]
public void CataloguePinsRavenfieldThrowableFacts(
    byte id, byte clip, short reserve, int total, ushort releaseTicks)
{
    WeaponConfig config = WeaponCatalog.For(id);
    Assert.Equal(clip, config.ClipSize);
    Assert.Equal(reserve, config.SpareAmmo);
    Assert.Equal(total, clip + Math.Max(0, reserve));
    Assert.Equal(releaseTicks, config.ReleaseDelayTicks);
}
```

Use `ceil(seconds * SIM_TICK_RATE)`: `ceil(0.952444 * 30) = 29` and
`ceil(0.3186882 * 30) = 10`.

- [ ] **Step 2: Run the catalogue test and verify RED**

Run: `dotnet test Ironfront.Net.Replication.Tests/Ironfront.Net.Replication.Tests.csproj --filter CataloguePinsRavenfieldThrowableFacts --no-restore`

Expected: compile failure because `ReleaseDelayTicks` is absent.

- [ ] **Step 3: Add release delay to config/catalogue without changing non-throwables**

```csharp
public readonly ushort ReleaseDelayTicks;
public bool HasDelayedRelease => Delivery == WeaponDelivery.Projectile && ReleaseDelayTicks > 0;
```

Default the constructor parameter to zero. Set 29 for frag/spearhead and 10 for ammo box/medipack.

- [ ] **Step 4: Write RED transition tests**

```csharp
[Fact]
public void FragCommitsAtReleaseAndRefillsFromReserveExactlyOnce()
{
    Harness h = Harness.For(WeaponIds.FRAG);
    Assert.Equal(ThrowableRejection.None, h.Begin(inputTick: 100, serverTick: 500));
    Assert.True(h.State.PendingRelease);
    Assert.Equal(1, h.State.AmmoInClip);

    Assert.False(h.Release(serverTick: 528).Released);
    ThrowableTransition released = h.Release(serverTick: 529);

    Assert.True(released.Released);
    Assert.Equal(1, h.State.AmmoInClip);
    Assert.Equal(0, h.ReserveRounds);
    Assert.False(h.Release(serverTick: 530).Released);
}

[Fact]
public void FinalFragLeavesZeroLoadedAndZeroReserve()
{
    Harness h = Harness.For(WeaponIds.FRAG);
    h.ThrowAt(500);
    h.ThrowAt(600);
    Assert.Equal(0, h.State.AmmoInClip);
    Assert.Equal(0, h.ReserveRounds);
}
```

Add equivalent spearhead, no-resupply, duplicate-begin, cooldown, cancel, and rollback tests.

- [ ] **Step 5: Run transition tests and verify RED**

Run: `dotnet test Ironfront.Net.Replication.Tests/Ironfront.Net.Replication.Tests.csproj --filter ThrowableLifecycleTests --no-restore`

Expected: compile failure because `ThrowableLifecycle` is absent.

- [ ] **Step 6: Implement the minimal state machine**

```csharp
public static ThrowableRejection TryBegin(
    ref WeaponRuntimeState state, in WeaponConfig config,
    uint inputTick, uint serverTick, in Vec3 aim)
{
    if (!config.HasDelayedRelease) return ThrowableRejection.NotDelayed;
    if (state.PendingRelease) return ThrowableRejection.AlreadyPending;
    if (!state.Unholstered) return ThrowableRejection.Holstered;
    if (state.Reloading) return ThrowableRejection.Reloading;
    if (state.AmmoInClip == 0) return ThrowableRejection.NoAmmo;

    state.PendingRelease = true;
    state.PendingReleaseTick = serverTick + config.ReleaseDelayTicks;
    state.PendingInputTick = inputTick;
    state.PendingAim = aim;
    return ThrowableRejection.None;
}
```

`TryRelease` must first verify the due tick with wrap-safe sequence math, then stage the inventory
change, take only enough reserve to refill the clip, and return a transition containing the before
state so `RollbackRelease` can restore both weapon and pool when launch creation fails.

- [ ] **Step 7: Run focused and full replication tests GREEN**

Run: `dotnet test Ironfront.Net.Replication.Tests/Ironfront.Net.Replication.Tests.csproj --no-restore`

- [ ] **Step 8: Commit Task 2**

```bash
git add Ironfront.Net.Replication/Combat/ThrowableLifecycle.cs Ironfront.Net.Replication/Combat/WeaponModel.cs Ironfront.Net.Replication/Combat/WeaponCatalog.cs Ironfront.Net.Replication.Tests/ThrowableLifecycleTests.cs
git commit -m "feat(replication): model delayed throwable lifecycle"
```

### Task 3: Make snapshots carry the full throwable phase

**Files:**
- Modify: `Ironfront.Net.Replication/SnapshotBuilder.cs`
- Modify: `Ironfront.Net.Replication/DeltaEncoder.cs`
- Modify: `Ironfront.Net.Replication/DeltaDecoder.cs`
- Modify: `Ironfront.Net.Replication.Tests/SnapshotAndDeltaTests.cs`
- Modify: `Ironfront.Net.Replication.Tests/WeaponSnapshotSourceTests.cs`

**Interfaces:**
- Consumes: pending state from `WeaponRuntimeState`.
- Produces: one atomic weapon field containing weapon id, loaded count, reserve, and both phase flags.

- [ ] **Step 1: Write RED snapshot/delta tests**

```csharp
[Fact]
public void PendingReleaseAloneMarksTheWeaponFieldChanged()
{
    ActorSnapshotEntry before = WeaponEntry(flags: WeaponStateFlags.None);
    ActorSnapshotEntry after = WeaponEntry(flags: WeaponStateFlags.PendingRelease);
    Assert.True((DeltaEncoder.ComputeChangeMask(in before, in after) & SnapshotField.Weapon) != 0);
}

[Fact]
public void ResolveWeaponFieldsPublishesPendingRelease()
{
    WeaponRuntimeState state = LoadedFrag();
    state.PendingRelease = true;
    WeaponSnapshotFields fields = SnapshotBuilder.ResolveWeaponFields(in state, in Frag, in Ammo);
    Assert.True((fields.StateFlags & WeaponStateFlags.PendingRelease) != 0);
}
```

- [ ] **Step 2: Verify RED, then publish the flag atomically**

Change `ResolveWeaponFields` to compose flags:

```csharp
WeaponStateFlags flags = WeaponStateFlags.None;
if (weapon.Reloading) flags |= WeaponStateFlags.Reloading;
if (weapon.PendingRelease) flags |= WeaponStateFlags.PendingRelease;
```

- [ ] **Step 3: Run snapshot and full replication suites GREEN**

Run: `dotnet test Ironfront.Net.Replication.Tests/Ironfront.Net.Replication.Tests.csproj --filter "Snapshot|Delta|WeaponSnapshot" --no-restore`

Run: `dotnet test Ironfront.Net.Replication.Tests/Ironfront.Net.Replication.Tests.csproj --no-restore`

- [ ] **Step 4: Commit Task 3**

```bash
git add Ironfront.Net.Replication/SnapshotBuilder.cs Ironfront.Net.Replication/DeltaEncoder.cs Ironfront.Net.Replication/DeltaDecoder.cs Ironfront.Net.Replication.Tests/SnapshotAndDeltaTests.cs Ironfront.Net.Replication.Tests/WeaponSnapshotSourceTests.cs
git commit -m "feat(replication): snapshot pending throwable state"
```

### Task 4: Advance and launch pending throwables on the server tick

**Files:**
- Modify: `Ironfront.Net.Replication/Combat/ServerCombatAuthority.cs`
- Modify: `Ironfront.Net.Replication/Server/ClientSession.cs`
- Modify: `Ironfront.Net.Replication.Tests/ServerCombatAuthorityTests.cs`
- Modify: `Ironfront.Net.Replication.Tests/ClientSessionWeaponMemoryTests.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Server/Bindings/IGameplayActorSource.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerActor.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerCombatBridge.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/ServerThrowableLifecycleTests.cs`

**Interfaces:**
- Produces: `ServerCombatBridge.AdvancePendingActions(uint tick)` called exactly once per simulation tick.
- Produces: `IGameplayActorSource.ReleaseCarriedThrowable(...)` returning success/failure.
- Consumes: `ThrowableTransition` and the existing projectile announcer.

- [ ] **Step 1: Write RED authority tests**

Test that projectile fire creates pending state without `Fired`, that a later tick creates one release,
and that ordinary projectile launchers remain immediate. Test switch/death cancellation through
`ClientSession`.

```csharp
[Fact]
public void DelayedProjectileBeginsNowAndReleasesOnItsScheduledTick()
{
    CombatTickResult begin = StepFrag(fire: true, inputTick: 10, serverTick: 100);
    Assert.True(begin.ReleaseBegan);
    Assert.False(begin.Fired);

    ThrowableTransition release = _authority.AdvancePendingRelease(
        ref _weapon, in _frag, serverTick: 129, in _ammo);
    Assert.True(release.Released);
}
```

- [ ] **Step 2: Verify RED and split begin from release**

`ServerCombatAuthority.Step` calls `ThrowableLifecycle.TryBegin` for delayed projectile configs.
Immediate rockets/shells retain `ResolveLaunch`. Add `AdvancePendingRelease` as a clockless wrapper.

- [ ] **Step 3: Write RED Unity seam tests**

Use a fake `IGameplayActorSource` to assert one release call, rollback on `false`, and cancellation on
death/switch. Assert `AdvancePendingActions` works when no new input frame arrives.

- [ ] **Step 4: Implement server-tick advancement and transactional launch**

At each tick, iterate live sessions, resolve actor/config/ammo, and advance pending state before building
snapshots. On release:

```csharp
ThrowableTransition transition = _authority.AdvancePendingRelease(...);
if (!transition.Released) return;
if (!actor.ReleaseCarriedThrowable(transition.Aim))
{
    ThrowableLifecycle.RollbackRelease(ref session.Weapon, in transition, in ammo);
    FailedThrowableLaunches++;
}
PublishWeaponState(session, actor, in config, in ammo);
```

- [ ] **Step 5: Remove server scheduling ownership from `ThrowableWeapon.Update`**

The network server path must expose an explicit release method and must not keep a second `releaseTick`.
Offline keeps the Animator event path unchanged.

- [ ] **Step 6: Run replication and Unity EditMode seam tests GREEN**

Run: `dotnet test Ironfront.Net.Replication.Tests/Ironfront.Net.Replication.Tests.csproj --no-restore`

Run the repository's documented Unity EditMode command for
`ServerThrowableLifecycleTests` and existing `NetServerActorSeamTests`.

- [ ] **Step 7: Commit Task 4**

Stage only the listed files and commit `feat(server): release throwables transactionally`.

### Task 5: Reconcile client throwable state by acknowledged input

**Files:**
- Create: `Ironfront.Net.Replication/Client/PredictedWeaponCommandBuffer.cs`
- Modify: `Ironfront.Net.Replication/Client/ClientCombatState.cs`
- Modify: `Ironfront.Net.Replication.Tests/ClientCombatTests.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientLocalCombatDriver.cs`

**Interfaces:**
- Produces: `ClientCombatState.ApplyTrigger(..., uint inputTick, uint localTick)` and
  `ApplySnapshot(..., uint lastProcessedInputTick, uint serverTick)`.
- Consumes: `WeaponStateFlags.PendingRelease` and the input tick already supplied by snapshot headers.

- [ ] **Step 1: Write RED reconciliation tests**

```csharp
[Fact]
public void AckBeforeReleaseDoesNotRestoreASecondFrag()
{
    ClientCombatState state = FragClient();
    state.PredictTrigger(inputTick: 100, localTick: 500, aim: Vec3.Forward);

    state.ApplySnapshot(
        FragEntry(ammo: 1, reserve: 1, pending: true),
        nowSeconds: 1f, lastProcessedInputTick: 100, serverTick: 501);

    Assert.True(state.IsReleasePending);
    Assert.Equal(1, state.TotalThrowableUsesAvailable);
}
```

Add tests for an unacknowledged command replayed after an older snapshot, a release snapshot settling
loaded/reserve, refused trigger removal, wraparound ticks, and ordinary-gun threshold preservation.

- [ ] **Step 2: Verify RED and implement the fixed-capacity command buffer**

Store only weapon commands newer than the last acknowledged tick. Rebuild predicted throwable state by
installing the full snapshot state and replaying the survivors through `ThrowableLifecycle`. Do not use
`ReconcileAmmo` for configs with `HasDelayedRelease`.

- [ ] **Step 3: Pass real ticks from the local driver**

Use the same client input tick exposed by the input sender/prediction stage. Pass
`lastProcessedInputTick` and `serverTick` from `OnSnapshotApplied`; stop discarding both parameters.

- [ ] **Step 4: Run client combat tests GREEN**

Run: `dotnet test Ironfront.Net.Replication.Tests/Ironfront.Net.Replication.Tests.csproj --filter ClientCombatTests --no-restore`

- [ ] **Step 5: Commit Task 5**

Commit `feat(client): replay throwable prediction from snapshot truth` with only Task 5 files.

### Task 6: Make Unity inventory fields mirrors in network roles

**Files:**
- Modify: `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Weapon.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ThrowableWeapon.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/NetBindings/LocalPlayerRigBinding.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerActor.cs`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/NetworkThrowableMirrorTests.cs`

**Interfaces:**
- Consumes: reconciled loaded/reserve/phase projection.
- Produces: view-model animation notifications with no gameplay mutation.

- [ ] **Step 1: Write RED source/seam tests**

Assert that a client animation event cannot decrement ammo/reserve, that the network server explicit
release cannot schedule a second release, and that offline `SpawnThrowable` still calls the original
`Shoot`/`Reload` path.

- [ ] **Step 2: Guard legacy mutation by network role**

```csharp
public void SpawnThrowable()
{
    if (!NetContext.IsOffline) return;
    ReleaseThrowableOffline();
}
```

Expose separate, narrowly named adapter methods for starting presentation and performing the server's
already-approved release. Do not let either method call `Weapon.ReloadDone` as a network gameplay
decision.

- [ ] **Step 3: Apply loaded and reserve together in `LocalPlayerRigBinding`**

Replace the `clipSettled` partial-write behavior for delayed throwables with one projection write. HUD
updates happen after both fields are assigned.

- [ ] **Step 4: Run Unity EditMode tests and asset gates GREEN**

Run the documented Unity EditMode suite and the repository asset-wiring test command.

- [ ] **Step 5: Commit Task 6**

Commit `refactor(unity): mirror network throwable inventory`.

### Task 7: Prove projectile identity and prefab completeness

**Files:**
- Modify: `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ProjectileNetAnnouncer.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientProjectilePresenter.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ProjectileCatalogInstaller.cs`
- Modify: `Ironfront_Reborn/Assets/Scenes/Dustbowl.unity`
- Modify: `Ironfront_Reborn/Assets/Scenes/Island.unity`
- Modify: `Ironfront.Net.Protocol.Tests/Conformance/ProjectileKindTests.cs`
- Modify: `Ironfront_Reborn/Assets/Tests/EditMode/AssetWiringDetectors.cs`

**Interfaces:**
- Consumes: committed weapon id at the release boundary.
- Produces: a total weapon-to-projectile-kind mapping and complete scene arrays.

- [ ] **Step 1: Write RED mapping and scene-gate tests**

```csharp
[Theory]
[InlineData(WeaponIds.FRAG, ProjectileKind.Grenade)]
[InlineData(WeaponIds.SPEARHEAD, ProjectileKind.Spearhead)]
[InlineData(WeaponIds.AMMO_BAG, ProjectileKind.AmmoBag)]
[InlineData(WeaponIds.MEDIPACK, ProjectileKind.Medipack)]
public void EveryThrowableHasItsOwnNetworkKind(byte weaponId, ProjectileKind expected)
    => Assert.Equal(expected, ProjectileNetAnnouncer.KindForThrowableWeapon(weaponId));
```

The asset gate must enumerate every enum value and fail when either scene array is short, null, or maps
the wrong prefab GUID.

- [ ] **Step 2: Verify RED and centralize the mapping**

Keep component classification for generic projectiles, but for the four throwable weapon ids use one
explicit total switch. Both announcer and tests call the same public/internal mapping.

- [ ] **Step 3: Correct scene arrays and diagnostic text**

Ensure both scenes contain index 7 for spearhead and accurate tooltip/error lists. Unknown kinds remain
unrenderable and never fall back to frag.

- [ ] **Step 4: Run protocol and Unity asset tests GREEN**

Run: `dotnet test Ironfront.Net.Protocol.Tests/Ironfront.Net.Protocol.Tests.csproj --filter ProjectileKindTests --no-restore`

Run the Unity asset-wiring suite.

- [ ] **Step 5: Commit Task 7**

Commit `fix(client): preserve throwable projectile identity`.

### Task 8: Add acceptance telemetry and run the complete verification matrix

**Files:**
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/LaneBCheckpointRecorder.cs` (or the current file containing the combat checkpoint model)
- Modify: `tools/playtest-local.ps1`
- Modify: `docs/replication-troubleshooting.md`
- Create: `plans/reports/2026-09-25-throwable-lifecycle-verification.md`

**Interfaces:**
- Consumes: authoritative and predicted loaded/reserve/pending values plus projectile counters.
- Produces: reproducible lane-B evidence for every throwable and cancellation edge.

- [ ] **Step 1: Write/update the checkpoint schema test first**

Require these fields: `ammoInClip`, `serverAmmoInClip`, `spareAmmoKind`, `spareAmmoRounds`,
`releasePending`, `serverReleasePending`, `predictedCommands`, `projectilesSpawned`,
`failedThrowableLaunches`, and `unrenderableKinds`.

- [ ] **Step 2: Add four exhaustive scripted programmes**

Each programme selects one throwable, exhausts every use, waits through release, and records every
stable transition. Add switch-before-release and death-before-release programmes for frag.

- [ ] **Step 3: Run formatting/static checks**

Run: `git diff --check`

Run the repository source/asset gates documented by the existing CI scripts.

- [ ] **Step 4: Run all .NET tests**

Run: `dotnet test Ironfront.sln --no-restore --verbosity minimal`

Record every failing test by name; do not omit pre-existing failures.

- [ ] **Step 5: Run Unity EditMode tests and build**

Run the documented Unity test command, then `tools/build-player.ps1` using the repository's configured
Unity editor. Verify the produced `Assembly-CSharp.dll` timestamp and zero compile errors.

- [ ] **Step 6: Run lane-B with normal network and loss simulation**

Run the throwable set once with simulator off and once with the repository's loss/reorder preset. Grade
the state sequence and projectile visibility on driver plus two observers.

- [ ] **Step 7: Write the verification report**

Include exact commands, commit/build hashes, per-throwable state sequences, projectile kinds/counts,
screenshots, counters, and any residual limitation. A passing report requires no ghost projectile, no
lost use on cancellation, no inventory divergence at settled checkpoints, and zero unrenderable kinds.

- [ ] **Step 8: Commit Task 8**

Commit `test: verify authoritative throwable lifecycle`.
