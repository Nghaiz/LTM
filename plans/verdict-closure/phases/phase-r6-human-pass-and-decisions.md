# Phase R6 — The frames nobody has watched, one runtime decision, and three parkings put in writing

- **Track:** [`plan.md`](../plan.md) · **Effort:** S (1–2 d)
- **Depends on:** [`phase-r1-programme-set.md`](phase-r1-programme-set.md) and
  [`phase-r4-prediction-and-hitbox.md`](phase-r4-prediction-and-hitbox.md). Watching frames from a
  run whose engagement never happened, or whose hitboxes are about to change, is watching the wrong
  frames.
- **Closes:** **X-38**, **A-2** (its `_actor` half), **D-2**; re-affirms **X-14**, **C-5**, **C-12**
  in writing (**V-D4**)

---

## 1. Task R6.1 — X-38: 21 frames per run, and nobody has looked at one (S)

[`phase-3d-lane-b.md`](../../debt-closure/phases/phase-3d-lane-b.md) § 5 is explicit that an
unartifacted green is a failed row, and its own item 5 asks for *"a human pass over the captured
frames … recorded as human verdicts against named artifacts, **which is a deliverable and not a
disclaimer**"*.

**The frames exist and nobody has looked at one.** `artifacts/lane-b/x25-torso-aim-02/*.png` and its
two siblings hold **7 frames per client, 21 per run**, captured at every checkpoint.

Check 8 (*"no perceptible input lag; convergence without visible snapping"*) and check 9 (*"breaks
no unlisted cosmetic"*) are **human judgment by construction** — no counter in the checkpoint record
can answer either, which is exactly why B-8's numeric half being green does not settle it.

**The failure mode this guards against** is a verdict quietly upgrading from UNVERDICTED to PASS
because the numeric half was green and the frames were assumed fine.

**Work.** A human watches the frames from a post-R1, post-R4 run and records a verdict per check,
per client, naming the frame files. A verdict of *"cannot tell from a still"* is a legitimate
outcome and is recorded as such — it is a finding about the capture, not a pass.

**Acceptance:** **B-8** and **B-9** carry human verdicts against named PNG paths. Neither reads PASS
on the strength of a counter.

## 2. Task R6.2 — A-2: the `_actor` half, which is a runtime decision and not authoring (M)

Three of `RemoteActorView`'s four fields are authored (`_animator`, `_upperBody`, `_muzzleAnchor`),
pinned by `AssetWiringDetectors.RemoteActorPrefabIsAuthored`. **`_actor` is deliberately not**, and
the ragdoll rig is not either.

**The reason, and it has not changed:** the field needs an `Actor` component on the proxy, and
`Actor` registers itself with `ActorManager` (`Actor.cs:186`) — so a body the *server* owns would
become a client-side gameplay entity. That is a runtime-semantics decision.

Until it lands, a remote death slides to the floor at a fixed pose and remote hands are empty;
`RemoteActorView` announces both absences once at runtime, by design.

**It is pinned in both directions already.** `KnownUnauthoredFields` carries `_actor` with its
reason, prints it on every run, and `KnownUnauthoredFields_HasNoStaleEntries` **hard-fails** if
`_actor` is ever assigned without the entry being deleted. So this cannot close silently, and it
cannot be half-done.

**Work — decide, then do one of:**

- **Author it**, with a mechanism that keeps the proxy out of `ActorManager` — a registration opt-out
  on `Actor`, or a proxy-specific subclass that does not self-register. The `KnownUnauthoredFields`
  entry is deleted in the same commit and the companion assertion is what proves it.
- **Record it won't-do**, with the reason and a reopening condition — and the entry stays, because
  the entry *is* the record.

Either way the decision is written down. What is forbidden is leaving it as an inherited status
nobody re-reads, which is the shape that produced this whole track.

**Acceptance:** the row reads CLOSED or DECIDED, never `PARTIALLY CLOSED`; the gate's entry agrees
with whichever was chosen; the companion assertion is green.

## 3. Task R6.3 — D-2: five tests, and a count that was wrong in both directions (S)

`phase-v7-projectiles.md:394-416` names **22** tests. Each was checked by name repo-wide:

- **15 present verbatim** (`ProjectileTests.cs` ×11, `DeployableTests.cs` ×4)
- **2 present renamed** (`AProjectileSpawnRoundTripsAtTwentyBytes` → `PacketHexSampleTests.cs:472,486`;
  `OfflineProjectileBehaviourIsUnchanged…` → `OfflineBehaviourChangeTests.cs:40`, renamed because
  there were three changes, not two)
- **5 genuinely unwritten**, all grenade/throw: `AGrenadeDetonatesOnTheSameTickOnBothSides`,
  `AGrenadeDetonationPositionComesFromTheServerNotThePrediction`,
  `AGrenadeAppliesItsBlastDamageExactlyOnce`, `AThrowReleasesOnTheSameTickOnServerAndClient`,
  `AClientSpawnThrowableSpawnsNothing` — zero coverage under any other name.

`phase-v7-projectiles.md:517` is wrong in **both** directions: its arithmetic sums to nine, and the
guided-missile pair it names as missing exists at `ProjectileTests.cs:500`.

**The decision this row owes is not "write them".** All five exercise `Weapon`, `ThrowableWeapon`,
`GrenadeProjectile` and `Projectile` — every one still in `Assembly-CSharp`, which no `.asmdef` may
name. **P-D9** already records them won't-do, and § 6.1.1's reopening condition was rewritten on
2026-08-26 after it **fired on asmdef-seam C4 and its conclusion turned out to be false**: C4 gave
`Net/Client` an asmdef, and not one of the five became writable, because the subjects were never in
`Net/Client`.

**Work.** Correct the count at `phase-v7-projectiles.md:517` (nine → the real split), record that
five are unwritten rather than ten, and confirm the rewritten reopening condition names the
**subjects** rather than a folder. Then close the row as DECIDED.

**Acceptance:** the V7 record's arithmetic matches the repo; D-2 is DECIDED with the five named; the
reopening condition is keyed to `Weapon` / `ThrowableWeapon` / `GrenadeProjectile` / `Projectile`
leaving `Assembly-CSharp`.

## 4. Task R6.4 — the three parkings, re-affirmed rather than inherited (S)

Each of these is open **by decision**. Phase 8's audit found them indistinguishable from orphans in
the `closes in` column, which is how a decision decays into debt. One line each, in the row's own
status, saying that nothing is owed and why:

| Row | Parking, re-affirmed |
|---|---|
| **X-14** | A networked human cannot change weapon server-side. Closing it needs (a) client-side prediction of the switch or it lags a round trip, and (b) a UI story for the rejected case — both product decisions, neither expressible as a `.cs` change. **Its other half is already gated:** `ClientMessageType.LoadoutSelect` is a named exemption in `ClientSenderCoverageRunner.KnownUnsentMessages` citing this row, so the opcode is reported on every CI run and the entry hard-fails if a sender lands without this row being reconsidered |
| **C-5** | `GameManager`'s five loose booleans. Out of scope by **P-D10** |
| **C-12** | Grenades and deployables are never ballistically stepped — pinned deliberate by `ABouncingOrRigidbodyProjectileIsNotBallisticallyStepped`. Out of scope by **P-D10**, and the pin is live |

**This task writes three sentences and changes no code.** It exists because the alternative is a
fourth audit re-discovering them.

**Acceptance:** all three rows' `closes in` cells read a parking rather than a phase name, and each
status says in one line why nothing is owed.

## 5. Acceptance criteria

1. **B-8** and **B-9** carry human verdicts against named PNG paths, from a post-R1, post-R4 run
   (**X-38**).
2. `A-2` reads CLOSED or DECIDED — never `PARTIALLY CLOSED` — and the `KnownUnauthoredFields` entry
   agrees with the decision, companion assertion green.
3. `phase-v7-projectiles.md`'s test arithmetic matches the repo; **D-2** is DECIDED with the five
   named and the reopening condition keyed to the subjects.
4. **X-14**, **C-5** and **C-12** each carry a written parking, not an inherited status.
5. **Every row in the ledger now has a living owner or a written parking** — checked by reading the
   `closes in` column end to end, and stated as a count in the report.
6. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0;
   `tools/recount_debt_ledger.py --check` exits 0.
7. Any check that still cannot be graded is reported ungradeable with the row that blocks it. A
   human verdict of "cannot tell from a still" is recorded as a finding, not rounded to a pass.
