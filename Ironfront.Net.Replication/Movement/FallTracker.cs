using System;

namespace Ironfront.Net.Replication.Movement
{
    /// <summary>
    /// Follows one walking body between the ground it last stood on and the ground it lands on,
    /// and says how fast it hit: the speed <see cref="Combat.FallDamage"/> is paid on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>From the height fallen, not from the body's velocity.</b> A grounded body is pulled
    /// down at <see cref="MovementCore.StickToGroundForce"/> (10 m/s) so it hugs slopes and
    /// stairs; step off a 0.5 m kerb and its velocity already reads about 10.4 m/s, the speed of a
    /// 4.6 m fall. A body that leaves the ground at <c>v0</c> upward and lands <c>h</c> metres lower
    /// arrives at <c>√(v0² + 2gh)</c>, whatever the controller's bookkeeping says, so that is what
    /// this reports: from the height of the last ground it stood on, and the upward speed it left
    /// that ground with (a jump's, or none). A walking body's own run is not counted: it lands on
    /// its feet and runs on, and only its fall is stopped.
    /// </para>
    /// <para>
    /// <b>Fed once per step with the ground check the movement uses</b>: the server's tick
    /// (<c>ServerPlayer</c>) and the offline player's frame (<c>FpsActorController</c>). Every
    /// teleport of the body -- a seat exit, a respawn, a containment clamp -- must
    /// <see cref="Rebase"/> or <see cref="Forget"/>, or the next landing is measured from where
    /// the body was before it.
    /// </para>
    /// </remarks>
    public sealed class FallTracker
    {
        private float _originY = float.NaN;
        private float _takeoffUpSpeed;
        private bool _wasGrounded;

        /// <summary>The height the current fall is measured from, or NaN while unknown.</summary>
        public float OriginY => _originY;

        /// <summary>
        /// Forgets where the body last stood: the next observation starts a new measurement and
        /// can pay nothing. For a body that died, or was put somewhere new while dead.
        /// </summary>
        public void Forget()
        {
            _originY = float.NaN;
            _takeoffUpSpeed = 0f;
            _wasGrounded = false;
        }

        /// <summary>
        /// The body was just put at height <paramref name="y"/> at rest -- out of a seat, back
        /// inside the map -- and any fall from here starts here.
        /// </summary>
        public void Rebase(float y)
        {
            _originY = y;
            _takeoffUpSpeed = 0f;
            _wasGrounded = false;
        }

        /// <summary>
        /// One step's observation: whether the body touches the ground, its height, and its
        /// vertical speed (m/s, up positive). Returns the speed it landed at, m/s
        /// (<see cref="Combat.FallDamage.LandingSpeed"/>), when this step ends a fall, otherwise
        /// zero.
        /// </summary>
        public float Observe(bool grounded, float y, float verticalSpeed)
        {
            if (float.IsNaN(_originY))
            {
                _originY = y;
                _takeoffUpSpeed = 0f;
                _wasGrounded = grounded;
                return 0f;
            }

            if (!grounded)
            {
                // The step the body leaves the ground: a jump leaves upward, a walk off an edge
                // leaves with the stick-to-ground pull, which is no speed at all.
                if (_wasGrounded) _takeoffUpSpeed = Math.Max(0f, verticalSpeed);
                _wasGrounded = false;
                return 0f;
            }

            float landedAt = _wasGrounded
                ? 0f
                : Combat.FallDamage.LandingSpeed(_originY - y, _takeoffUpSpeed, -MovementCore.Gravity);
            _originY = y;
            _takeoffUpSpeed = 0f;
            _wasGrounded = true;
            return landedAt;
        }
    }
}
