using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Draws the local player's predicted body between its last two 30 Hz ticks, so the camera,
    /// the weapon and the body's shadow glide instead of stepping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the view stepped.</b> <see cref="NetPredictionClock"/> moves the body from
    /// <c>Update</c> once every 1/30 s, and nothing drew it anywhere but where the latest tick left
    /// it. The camera is a child of that body, so on a 144 Hz screen it held still for four or five
    /// frames and then jumped 12 cm walking or 22 cm sprinting. The original game moves its player
    /// in <c>FixedUpdate</c> at 60 Hz, which is why it reads as smooth and this read as juddering
    /// (owner report 2026-09-29: "giật"). The clock has published <see cref="NetPredictionClock.Alpha"/>
    /// for exactly this since it was written; nothing ever read it.
    /// </para>
    /// <para>
    /// <b>Only the drawing moves.</b> <see cref="LateUpdate"/> puts the body at
    /// <c>Lerp(LastTickFrom, LastTickTo, Alpha)</c> after every script's <c>Update</c> has run, and
    /// the first <see cref="FixedUpdate"/> or <see cref="Update"/> of the next frame puts it back,
    /// before physics, before the clock ticks and before any correction lands. Prediction,
    /// reconciliation, shooting and the capsule itself therefore only ever see the simulated
    /// position. The price is the textbook one: the view trails the simulation by up to one tick
    /// (33 ms), never ahead of it, so it cannot show a wall the body has not reached.
    /// </para>
    /// <para>
    /// <b>Moves made between ticks are kept, not smoothed.</b> A correction, a respawn landing or
    /// a stand-up moves the body outside a tick. The view shifts by the same amount at once, as it
    /// did before this component existed; a jump longer than <see cref="SnapDistanceMetres"/> is a
    /// teleport and the smoothing restarts from the new position. A write that lands while the view
    /// offset is applied survives the restore too, because the restore only removes the offset.
    /// </para>
    /// <para>
    /// <b>Nothing is smoothed while the clock holds the body still</b> — dead, seated, parked — so
    /// a ragdoll or a seat never has its transform moved from under it.
    /// </para>
    /// <para>
    /// <b>Authored on <c>Player Fps Actor.prefab</c> beside the clock, and idle until the clock
    /// runs.</b> The prefab ships the clock disabled and a networked deploy enables it; offline the
    /// first-person controller moves the body in <c>FixedUpdate</c> as the original did, the clock
    /// never ticks, and this component never moves anything.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-32000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetPredictionClock))]
    public sealed class PredictedViewInterpolator : MonoBehaviour
    {
        /// <summary>
        /// A move between ticks longer than this is a teleport, and the view restarts from the new
        /// position rather than sliding across the gap.
        /// </summary>
        public const float SnapDistanceMetres = 1f;

        private NetPredictionClock _clock;
        private Vector3 _from;
        private Vector3 _to;
        private bool _moving;
        private int _seenTickCount = -1;

        private bool _applied;
        private Vector3 _simulated;
        private Vector3 _drawn;

        /// <summary>How far the drawn body sits from the simulated one this frame, in metres.</summary>
        public Vector3 ViewOffset => _applied ? _drawn - _simulated : Vector3.zero;

        private void Awake()
        {
            _clock = GetComponent<NetPredictionClock>();
        }

        // Whichever of these runs first in a frame puts the body back; the other finds nothing to do.
        // FixedUpdate precedes the physics step, and this class runs before every other script.
        private void FixedUpdate() => Restore();

        private void Update() => Restore();

        private void OnDisable() => Restore();

        private void LateUpdate()
        {
            Restore();

            if (_clock == null || !_clock.isActiveAndEnabled) return;

            if (_clock.TickCount != _seenTickCount)
            {
                _seenTickCount = _clock.TickCount;
                _from = _clock.LastTickFrom;
                _to = _clock.LastTickTo;
                _moving = _clock.LastTickMovedBody;
            }

            Vector3 simulated = transform.position;
            Vector3 drift = simulated - _to;
            if (drift.sqrMagnitude > SnapDistanceMetres * SnapDistanceMetres)
            {
                _from = simulated;
                _to = simulated;
            }
            else
            {
                _from += drift;
                _to += drift;
            }

            if (!_moving) return;
            if (_clock.SimulationEnabled != null && !_clock.SimulationEnabled()) return;

            Vector3 drawn = Vector3.Lerp(_from, _to, _clock.Alpha);
            if ((drawn - simulated).sqrMagnitude < 1e-10f) return;

            _simulated = simulated;
            _drawn = drawn;
            _applied = true;
            transform.position = drawn;
        }

        /// <summary>
        /// Takes the view offset back off, keeping any move another script made while it was on.
        /// </summary>
        private void Restore()
        {
            if (!_applied) return;

            _applied = false;
            transform.position = _simulated + (transform.position - _drawn);
        }
    }
}
