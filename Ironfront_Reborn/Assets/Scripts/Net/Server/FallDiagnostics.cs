using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Watches one server-side body for the descent described by ledger <b>X-82</b>, and when it
    /// starts, records the four facts that tell a stuck collision system apart from a body with
    /// genuinely nothing underneath it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is known, and what is not.</b> Bodies leave the world in about 3% of recorded
    /// lane-B client-runs (7 of 222). The descent is a dead-constant 0.5166-0.5234 m per tick
    /// over twelve consecutive samples on two independent actors, which is 15.5 m/s and equals
    /// <c>InputAuthority.MaxMovePerTick</c> — <c>sqrt(6.5^2 + 10^2) * 1.3 = 15.5049</c> —
    /// exactly. So the fall is not gravity; it is a velocity large enough that the ANTI-CHEAT
    /// displacement clamp is what sets its speed. X and Z stay frozen within about a metre and
    /// health stays 100 the whole way down.
    /// </para>
    /// <para>
    /// <b>The two existing detectors were in the build for every falling artifact and both
    /// stayed silent</b> (X-15's "no NetMovementAgent" since 2026-08-22, X-19's "moved with no
    /// collision" since 2026-08-23; zero hits across every lane-B <c>server.log</c>). That is
    /// evidence, not absence: the movement agent was attached and the
    /// <see cref="CharacterController"/> was enabled, so <c>controller.Move</c> was really being
    /// called and really was not finding the floor.
    /// </para>
    /// <para>
    /// <b>This is a measurement, not a fix, and it is deliberately not accompanied by one.</b>
    /// The cause is unknown. Shipping a speculative repair beside a detector is the worst of
    /// both: if the guess happens to mask the symptom, the detector never fires, and the row is
    /// closed on a coincidence. The one measurement that settles X-82 is named in the ledger and
    /// is exactly what <see cref="Sample"/> records.
    /// </para>
    /// <para>
    /// <b>On by default, because the fall cannot be predicted.</b> It happens in roughly one run
    /// in thirty and in four different map locations, so an env-gated diagnostic would have to be
    /// switched on by somebody who already knew which run to watch. When nothing is falling this
    /// costs one float comparison per player per tick; the expensive part — a raycast and a
    /// string — runs only while a body is actually descending, and stops after
    /// <see cref="MaxReportedTicks"/>.
    /// </para>
    /// </remarks>
    internal sealed class FallDiagnostics
    {
        /// <summary>
        /// Consecutive descending, ungrounded ticks before this starts reporting.
        /// </summary>
        /// <remarks>
        /// A jump, a step off a crate and a slope all produce a few descending ticks and are not
        /// interesting. Five at 30 Hz is a sixth of a second — long enough to skip those, short
        /// enough that the first reported tick is still near the top of the fall, which is the
        /// part that says what went wrong. By the time a body is 500 m down, every sample looks
        /// the same.
        /// </remarks>
        private const int TicksBeforeReporting = 5;

        /// <summary>
        /// How many ticks of one fall are reported before it goes quiet.
        /// </summary>
        /// <remarks>
        /// Forty at 30 Hz is about 1.3 s, which at 15.5 m/s covers the first twenty metres. The
        /// recorded falls run for a minute or more; logging all of it would be thousands of lines
        /// that all say the same thing, and the log a reader has to scroll past is the log they
        /// stop reading.
        /// </remarks>
        private const int MaxReportedTicks = 40;

        /// <summary>How far below the body the probe ray looks for a surface.</summary>
        /// <remarks>
        /// Five metres, and started 0.1 m ABOVE the body's own position so the ray cannot begin
        /// inside the floor it is looking for. A hit at ~0.1 m with collision flags of
        /// <c>None</c> means the ground is right there and <c>Move</c> is not seeing it; no hit
        /// at all means the body genuinely has nothing under it. Those are different bugs, and
        /// no existing detector can tell them apart.
        /// </remarks>
        private const float ProbeDistanceMetres = 5f;

        private static bool _reportedPhysicsSettings;

        private int _descendingTicks;
        private int _reportedTicks;
        private float _previousY;
        private bool _hasPrevious;

        /// <summary>
        /// Records one tick. Cheap and silent unless this body is in a sustained descent.
        /// </summary>
        /// <param name="actorId">The falling actor, for correlating against the wire.</param>
        /// <param name="agent">The movement agent that just moved it.</param>
        /// <param name="state">The authoritative state after the move.</param>
        public void Sample(ushort actorId, NetMovementAgent agent, in MoveState state)
        {
            if (agent == null) return;

            float y = agent.transform.position.y;

            if (!_hasPrevious)
            {
                _previousY = y;
                _hasPrevious = true;
                return;
            }

            bool descending = y < _previousY && !agent.IsGrounded;
            _previousY = y;

            if (!descending)
            {
                _descendingTicks = 0;
                _reportedTicks = 0;
                return;
            }

            _descendingTicks++;
            if (_descendingTicks < TicksBeforeReporting) return;
            if (_reportedTicks >= MaxReportedTicks) return;

            _reportedTicks++;
            Report(actorId, agent, in state, y);
        }

        private void Report(ushort actorId, NetMovementAgent agent, in MoveState state, float y)
        {
            ReportPhysicsSettingsOnce();

            CharacterController controller = agent.GetComponent<CharacterController>();
            Vector3 position = agent.transform.position;

            // From just above the body's own origin, so the ray cannot start inside the surface
            // it is testing for. Unmasked ON PURPOSE, unlike the spawn snap: the question here is
            // "is there anything at all under this body", and excluding layers would let the
            // answer be "no" for a reason this diagnostic invented.
            bool hit = Physics.Raycast(
                position + Vector3.up * 0.1f, Vector3.down, out RaycastHit info, ProbeDistanceMetres);

            Debug.LogWarning(
                $"[fall] actor {actorId} tick {_descendingTicks} y={y:F3} "
                + $"pos=({position.x:F2},{position.y:F2},{position.z:F2}) "
                + $"velY={state.Velocity.Y:F3} grounded={agent.IsGrounded} "
                + $"flags={agent.LastCollisionFlags} "
                + $"ctrlEnabled={(controller != null && controller.enabled)} "
                + $"ctrlHeight={(controller != null ? controller.height : -1f):F3} "
                + $"ctrlRadius={(controller != null ? controller.radius : -1f):F3} "
                + $"ctrlCenterY={(controller != null ? controller.center.y : -1f):F3} "
                + $"bypassed={agent.CollisionBypassedMoves} "
                + (hit
                    ? $"probe=HIT dist={info.distance:F3} collider='{info.collider.name}' "
                      + $"layer={info.collider.gameObject.layer}"
                    : $"probe=MISS within {ProbeDistanceMetres} m")
                + " -- ledger X-82");
        }

        /// <summary>
        /// Prints the physics settings the X-82 leads turn on, once per process.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This settles a lead by measurement rather than by reading a serialized file.</b>
        /// The recorded lead is that <c>Physics.SyncTransforms()</c> is called nowhere in
        /// <c>Assets/</c> or <c>Ironfront.Net.Replication/</c> while
        /// <c>NetMovementAgent.Teleport</c> writes <c>transform.position</c> directly — which
        /// only matters if <c>autoSyncTransforms</c> is off, since with it on the write is pushed
        /// to the physics scene immediately.
        /// </para>
        /// <para>
        /// <c>DynamicsManager.asset</c> cannot answer that: it is at
        /// <c>serializedVersion: 2</c> and carries no <c>m_AutoSyncTransforms</c> key at all, so
        /// the effective value comes from an importer default that depends on the version the
        /// project was created in. Reading the live property is the only honest way to know, and
        /// it costs one line in a log that is already being written because a body is falling.
        /// </para>
        /// </remarks>
        private static void ReportPhysicsSettingsOnce()
        {
            if (_reportedPhysicsSettings) return;
            _reportedPhysicsSettings = true;

            Debug.LogWarning(
                $"[fall] physics: autoSyncTransforms={Physics.autoSyncTransforms} "
                + $"gravity={Physics.gravity} "
                + $"queriesHitTriggers={Physics.queriesHitTriggers} "
                + $"defaultContactOffset={Physics.defaultContactOffset:F4} "
                + "-- ledger X-82, the SyncTransforms lead");
        }
    }
}
