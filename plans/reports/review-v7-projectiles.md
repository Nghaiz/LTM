# Adversarial Review — Replication V7 (Projectiles)

_Review in progress — findings appended as they are confirmed._

Branch `feat/replication-v7-projectiles` @ `555af85`, 3 commits ahead of `develop`.

## Confirmed so far

1. **[CRITICAL] `ServerProjectileBridge` is unwired at HEAD; the wiring exists only as an
   uncommitted working-tree change.** `git show HEAD:.../ServerTickLoop.cs | grep -c ProjectileBridge`
   → `0`. `git status` shows ` M ServerTickLoop.cs` and `?? ProjectileCatalogInstaller.cs`.
2. **[CRITICAL] Even with that uncommitted wiring, `Launch()` and `Deploy()` have ZERO callers
   repo-wide.** Nothing ever creates a server projectile → no `S_PROJECTILE_SPAWN` is ever sent.
3. **[CRITICAL] Hitscan bullets are announced with `ProjectileId = 0`, and the client tracker
   discards every id `< FirstId(1)` as `OutOfRangeIds` ("a wiring fault").** Default config is
   `HitscanBullets = true`.
4. **[HIGH] `ClientProjectileTracker.Apply` re-seats on id alone, never comparing `Kind`.**
5. **[HIGH] `Ballistics` is not "the one place" — `Projectile.Update` re-implements the integrator
   inline.**
6. **[MED] `Apply` retires a live projectile when a re-announce arrives > 60 ticks late**, which
   contradicts the "despawns late, never never" guarantee.
7. **[MINOR] Protocol-spec changelog amendment row is separated from its table by a blank line** →
   renders as a second, header-less table.
