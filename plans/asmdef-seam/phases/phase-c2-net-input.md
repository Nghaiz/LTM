# Phase C2 — `Net/Input` behind a control-surface binding

- **Track:** [`plan.md`](../plan.md) · **Effort:** M (3 d)
- **Depends on:** [`plans/debt-closure/phases/phase-3e-run-and-ledger.md`](../../debt-closure/phases/phase-3e-run-and-ledger.md)
- **Unblocks:** [`phase-c4-net-client.md`](phase-c4-net-client.md) — which must not start before this lands

---

## 1. Scope

Eight files. **~8 distinct legacy types**, the two heavy ones being `Helicopter` (16 references) and
`FpsActorController` (15). Small enough to be one phase, heavy enough that the bindings are the work
and the folder move is a footnote.

## 2. The pattern, already in the repo

`Net/Server/Bindings/` — `IAiDriver`, `ICapturePointDirectory`, `ISpawnPointDirectory`. The sealed
side declares an interface; a legacy `MonoBehaviour` implements it; the sealed side never names the
legacy type. Copy this shape, do not invent a second one.

`Helicopter` and `FpsActorController` are both *things a control input is applied to*, so they take
one interface between them — a control surface — rather than one each. **Getting this wrong is
cheap now and expensive in C4**, which shares `FpsActorController`.

## 3. Work

1. Enumerate every legacy type `Net/Input`'s eight files name. The count is ~8; enumerate it rather
   than trusting the number.
2. Declare the binding interfaces on the sealed side, in the smallest set that covers the
   enumeration. A per-type interface for each of eight types is a failure of this step.
3. Implement them on the legacy components.
4. Add the asmdef. Set `autoReferenced: false`.
5. Unity compile over MCP. `dotnet build` is not the grade (`plan.md` § 4).
6. Add the `check-net-layering.ps1` rule for the new seam, **observed RED** against a deliberate
   violation before it ships.

## 4. Acceptance criteria

1. `Net/Input` compiles as its own assembly with **zero** `Assembly-CSharp` references.
2. Every crossing goes through an interface owned by the sealed side.
3. `Helicopter` and `FpsActorController` share one control-surface abstraction, not two.
4. The layering rule for this seam was observed RED against a deliberate violation before landing.
5. Graded by a Unity compile over MCP, with the output kept.
6. No gameplay behaviour changed. This is a move plus an indirection, and a diff showing otherwise
   is a finding.

## 5. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Eight one-type interfaces instead of one abstraction | 3 | 4 | 12 | § 3.2 names it as a failure of the step; AC-3 grades the shared one |
| `dotnet build` used as the grade | 3 | 5 | **15** | AC-5; `plan.md` § 4 |
| Behaviour drifts during the move | 2 | 4 | 8 | AC-6 — the diff is read for behaviour, not only for compilation |
