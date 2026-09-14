# The Island sprint-fire failure is a drowning, not a sprint block

Four consecutive `p10-sprint` runs on Island ended with `serverAmmoInClip` pinned at 30 through
four seconds of held fire while the client predicted ~40 shots. The same programme on Dustbowl
emptied a magazine. This names the cause to the line.

**The sprint gate is innocent.** `EffectiveTriggerPolicy` is correct, the custody repair merged in
#276 is correct and must stay, and the clock hypothesis is falsified — see § "The clock question"
below. The server never refused a shot. **It was never asked for one.**

## The chain, end to end

1. `tools/lane-b/p10-sprint-driver.json` step 4 holds `moveZ: 1.0, sprint: true, fire: true` for
   six seconds (t=10 → t=16). The driver runs forward at sprint speed for 40-odd metres.
2. On **Island** that run leaves the spawn shelf and enters the sea.
   `Actor.Update` samples it — `Actor.cs:577`, `inWater = WaterLevel.InWater(position)`.
3. `Actor.cs:595-601`:

   ```csharp
   if (inWater && !fallenOver)
   {
       if (IsSeated()) LeaveSeat();
       FallOver();
   }
   ```

4. `Actor.FallOver()` → `Actor.cs:917`, `controller.DisableInput()` →
   `FpsActorController.DisableInput()` (`FpsActorController.cs:444-449`) sets
   `inputEnabled = false`.
5. **It never comes back.** The only getup is `Actor.cs:836`:

   ```csharp
   if (fallAction.TrueDone() && !flag && ragdoll.IsRagdoll() && !inWater)
   ```

   — `&& !inWater`. A body still in the water is refused the getup by design, so `GetUp()` /
   `InstantGetUp()` never run and `controller.EnableInput()` (`Actor.cs:693`, `Actor.cs:942`)
   never fires. The body drowns for the rest of the life.
6. `FpsActorController.cs:197` wires that flag straight into the send path:

   ```csharp
   clock.SimulationEnabled = () => inputEnabled && actor != null && !actor.dead && !actor.IsSeated();
   ```

7. `NetPredictionClock.Update` (`NetPredictionClock.cs:218-226`):

   ```csharp
   MoveInput input = InputSource();
   if (SimulationEnabled == null || SimulationEnabled()) _agent.Tick(in input, TickInterval);
   else input = default;                       // <-- every button zeroed
   ...
   OnTickSimulated?.Invoke(InputTick, input);  // and the zeroed frame is what gets sent
   ```

   `ClientPredictionStage.OnTickSimulated` packs that `default` frame and `SendPending()` puts it
   on the wire. **From the moment the body enters the water, every input frame the server receives
   carries zero buttons.** No Fire bit, for the remaining eleven seconds of the programme.

8. `NetClientLocalCombatDriver` does **not** read the zeroed frame. It reads
   `NetClientBindings.LocalPlayer.InputSource` directly (`NetClientLocalCombatDriver.cs:725`,
   `:733`, `:760`), which is still the scripted source with Fire held. So the client keeps
   predicting shots the server was never told about — `predictedShots: 40`,
   `ammoCorrections: 14`, local clip 29 against a server clip of 30.

That last step is the whole reported symptom, and step 7 is why the server's half of it is a flat
line rather than a refusal.

## The evidence in the artifacts

`localActor.y` at the sprint checkpoints, and `combat.localInputEnabled` after:

| run | y at `sprint-fire` (t=10) | y at `sprint-ended` (t=16) | `localInputEnabled` after | result |
|---|---|---|---|---|
| `rate-island-1` | 24.02 | **17.89** | false | FAIL |
| `rate-island-2` | 23.02 | **17.52** | false | FAIL |
| `rate-island-3` | 23.08 | **16.89** | false | FAIL |
| `rate-island-4` | 21.33 | **17.64** | false | FAIL |
| `final-sprint-island-shots` | 25.85 | **21.59** | **true** | PASS |
| `final-sprint-Dustbowl` | 10.02 | 12.93 | **true** | PASS |

Four runs, four different spawn points (x = 181.7 / 215.8 / 204.0 / 191.0), four different paths —
and all four terminate inside a **one-metre altitude band, 16.89 to 17.89**. Four coincidental
slopes do not agree to a metre; a water plane does. The ~1 m spread is a floating ragdoll bobbing.
The one passing Island run stopped at 21.59, about four metres above that band, still on land.

`localInputEnabled` is `true` at `sprint-fire` and `false` at `sprint-ended` in exactly the four
runs that reached the band, and `true` throughout in the two that did not. Six of six. In the
failing runs the body is also frozen to the centimetre across `sprint-ended` → `fire-clear` →
`settled`, local and authoritative alike, which is what `SimulationEnabled() == false` looks like:
`_agent.Tick` is not called at all and the frames carry no movement either.

`alive: true`, `health: 100`, `inputSuppressedByDeath: false`, `occupiedVehicleId: 0` throughout —
so this is not the death path (`Actor.Die`, `Actor.cs:981`), not the driver's own death suppression
(`NetClientLocalCombatDriver.cs:936`, which sets `IsInputSuppressedByDeath`), and not the seated
path. `FpsActorController.FixedUpdate` short-circuits on `NetContext.IsClient`
(`FpsActorController.cs:803-807`), so the airborne detector is already ruled out by its own guard.
`Actor.Update`'s water branch is the one live producer left, and the altitudes say it fired.

## `-LogShots` is a red herring

It does not change the outcome. **The spawn point does.** Island picks a different spawn each run,
and a six-second forward sprint reaches the water from some of them and not others. The
"4 of 4 without / 1 of 1 with" split is a five-sample read of a spawn lottery: the single
`-LogShots` run happened to start at (216.27, 25.85, 515.59), the highest of the six, and ran out
of programme while still on land. Nothing about per-shot logging touches
`Actor.Update`'s water sample.

This matters beyond this bug: a slow instrument was blamed for a result the instrument had no
causal path to, and that turned a reproducible map problem into an apparent Heisenbug.

## Why only the driver, and why the server stayed healthy

`Actor.Update` opens with `Actor.cs:591`:

```csharp
if (aiControlled && controller != null && !controller.enabled) return;
```

On the **server**, a networked player's body is `aiControlled` with a suspended controller, so
`Actor.Update` early-returns and the server-side body never runs the water branch at all. That is
why the server kept reporting `weaponId: 1` with a full 30-round clip and a raised weapon: its
half was fine and willing the entire time. The local player's own body is not `aiControlled`, so
it is the only one the branch reaches.

## The fix, and where it must land

**Not in the netcode.** The repair is one of:

- `Actor.Update`'s water branch must not take a *networked local player's* input away — the server
  owns that body's movement, and `FpsActorController.FixedUpdate` already carries the precedent
  and the reasoning for exactly this exclusion (`NetContext.IsClient` → `return`, with the remark
  describing the identical "fell over forever at 100 HP" failure); or
- the getup at `Actor.cs:836` must have a path out of the water for a live networked body, so that
  `EnableInput()` is eventually reached.

Both are in `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs` and
`FpsActorController.cs`, which this lane does not own. It is handed over rather than attempted.

A third option is worth weighing on its own merits: a body that swims out of its depth losing its
weapon is plausible *game design*, and if it is intended then the failure is only that
`p10-sprint-driver` runs a 40-metre sprint on a map where 40 metres reaches the sea. Constraining
the programme's heading or its duration on Island would make the check measure the sprint gate
again instead of the shoreline. That is a decision for the owner, not a defect to patch silently.

## The clock question, answered

The hypothesis was that `AdvanceSprintBlock` stamps from a wall clock at processing time rather
than from the input frame's own tick, so a drained backlog of stale sprinting frames re-arms the
block forever. **The premise is true and the consequence is not what happened here.**

`ServerCombatBridge.cs:104` computes the stamp's clock as:

```csharp
uint tick = _loop.CurrentTick;
float now = tick / (float)ProtocolConstants.SIM_TICK_RATE;
```

That is the **processing** tick, not the frame's own tick — so the block does end
`SPRINT_FIRE_BLOCK_SECONDS` after the last sprinting frame is *processed* rather than after it was
*produced*. The error is real but bounded: it equals the backlog depth, and
`InputAuthority.MaxInputBurst` (4, refilling at one per tick) meters a fast client rather than
letting it drain a deep queue, so the overhang is a few ticks — tens of milliseconds — not the four
seconds observed. It cannot produce this failure, and it is not speculatively "fixed" here: no
test fails for it, and correcting it would mean threading a frame tick through the policy both
sides share.

## What the server could not tell you, and now can

`ServerCombatAuthority` counted refusals (`SprintBlockedTriggers`) but never counted **arrivals**.
A run where the client sends no Fire bit and a run where the gate refuses every Fire bit both read
as zero shots fired, and the only instrument that separated them was `-LogShots` — slow enough to
be blamed for the result. `ServerCombatAuthority.TriggerFramesSeen` closes that: it counts accepted
frames carrying the raw Fire bit, before any gate.

On a failing Island run it stays flat across the whole `fire-clear` window, which says *the client
sent nothing* in one number and sends the reader to the client. On a genuine gate defect it climbs
while the shot counter does not.
