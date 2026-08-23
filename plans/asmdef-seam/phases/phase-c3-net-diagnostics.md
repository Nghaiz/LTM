# Phase C3 — `Net/Diagnostics` sealed, and out of the player build

- **Track:** [`plan.md`](../plan.md) · **Effort:** S (1–2 d)
- **Depends on:** [`phase-c2-net-input.md`](phase-c2-net-input.md)

---

## 1. Scope

Eleven files, **15 distinct legacy types**, no single heavy one. The overlays — `VehicleReplicationOverlay`,
`TransportDebugOverlay`, `MovementShadowCompare` — plus the scripted-input driver lane B added.

**These are test-only**, which changes the phase's shape: the seam is worth less here than the
*exclusion* is. Diagnostics compiled into a shipped player is code paths and allocation nobody asked
for, in a build nobody profiles for it.

## 2. Work

1. Enumerate the 15 legacy types. Several are likely reachable through bindings C2 already declared —
   reuse them rather than declaring near-duplicates.
2. Asmdef with a **platform/define constraint** that keeps it out of player builds.
3. Unity compile over MCP, in **both** configurations: with diagnostics included, and with it
   excluded. A seam that only compiles in one of its two configurations is half-verified.
4. A gate that fails if the exclusion is undone. **Observed RED with the constraint removed** before
   it ships — `plan.md` success criterion 5 is otherwise unenforceable, and a build-config guarantee
   nothing checks is exactly the shape that decays silently.

## 3. The thing to watch

Lane B's runner **depends on these overlays** —
[`phase-3d-lane-b.md`](../../debt-closure/phases/phase-3d-lane-b.md) § 3 lists
`ClientVehicleStage.DrivenStats`, connection/RTT state and `MovementShadowCompare` as its
instrumentation. Excluding diagnostics from player builds must not exclude it from the **harness**
build, or this phase silently breaks every future lane-B run.

Re-run lane B once after the change. That is the check.

## 4. Acceptance criteria

1. `Net/Diagnostics` compiles as its own assembly with zero `Assembly-CSharp` references.
2. It is excluded from player builds, and a gate fails when the exclusion is removed — observed RED.
3. Unity compile is green in **both** configurations.
4. Lane B runs unchanged after the change, with the run recorded. Its overlays are still reachable.
5. Interfaces declared in C2 were reused where they fit; new ones are justified individually.

## 5. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The exclusion silently breaks lane B | 3 | 5 | **15** | § 3; AC-4 requires an actual lane-B run, not an argument |
| Only the "included" configuration is compiled | 3 | 3 | 9 | AC-3 names both |
| The exclusion gate is written un-failable | 2 | 4 | 8 | AC-2 requires the RED observation |
