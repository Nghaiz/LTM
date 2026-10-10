using System;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// The original game's recoil, as it acts on the rounds: the kick of the first-person weapon,
    /// which a round fired mid-burst leaves along. The same two springs and the same formula as the
    /// recovered <c>PlayerFpParent.ApplyRecoil</c> and <c>Spring</c>, fed with each weapon's own
    /// authored <c>kickback</c> and <c>randomKick</c> -- nothing here is tuned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner's list of 2026-10-11, item 8: "recoil as in the original, not invented".</b> The
    /// original sent each round along the kicked muzzle: the first shot of a burst flies true, the
    /// ones after it leave where the jump has thrown the barrel (an RK-44 burst wanders about 2
    /// degrees, a pistol tapped fast half a degree, a bolt-action sniper has settled long before the
    /// next shot), and the barrel springs back as soon as the trigger rests. v4.6.1 had instead
    /// climbed the aim itself by an invented amount per shot and kept 40 % of it, so players learned
    /// to aim at the neck to hit the head.
    /// </para>
    /// <para>
    /// <b>Only the kick.</b> The original's rotation spring also took the mouse's sway, which made a
    /// round lag a fast turn and land off the crosshair (the complaint 14.0.6 closed); that part is
    /// not here, so a round still flies where the crosshair is, give or take the kick.
    /// </para>
    /// <para>
    /// Engine-free, so the client fires with it and the server allows for it (<see cref="WorstDeviationDegrees"/>)
    /// from one model.
    /// </para>
    /// </remarks>
    public sealed class RecoilKick
    {
        // PlayerFpParent: new Spring(150f, 10f, -Vector3.one * 0.2f, Vector3.one * 0.2f, 8) and
        // new Spring(70f, 6f, -Vector3.one * 15f, Vector3.one * 15f, 8).
        private const float PositionStiffness = 150f, PositionDrag = 10f, PositionLimit = 0.2f;
        private const float RotationStiffness = 70f, RotationDrag = 6f, RotationLimit = 15f;
        private const int Iterations = 8;

        private readonly float[] _position = new float[3];
        private readonly float[] _positionVelocity = new float[3];
        private readonly float[] _rotation = new float[3];
        private readonly float[] _rotationVelocity = new float[3];

        /// <summary>How far the kick has turned the barrel up from the aim, degrees (positive is up).</summary>
        public float PitchUpDegrees => -_rotation[0];

        /// <summary>How far it has turned the barrel sideways, degrees (positive is right).</summary>
        public float YawDegrees => _rotation[1];

        /// <summary>The whole turn off the aim, degrees.</summary>
        public float DeviationDegrees => (float)Math.Sqrt(_rotation[0] * _rotation[0] + _rotation[1] * _rotation[1]);

        /// <summary>
        /// One shot's kick: <c>kickback * back + random * randomKick</c>, with <paramref name="randomX"/>..
        /// <paramref name="randomZ"/> a point in the unit sphere (Unity's <c>insideUnitSphere</c>).
        /// </summary>
        public void Shot(float kickback, float randomKick, float randomX, float randomY, float randomZ)
        {
            float impulseX = randomX * randomKick;
            float impulseY = randomY * randomKick;
            float impulseZ = -kickback + randomZ * randomKick;

            _positionVelocity[0] += impulseX;
            _positionVelocity[1] += impulseY;
            _positionVelocity[2] += impulseZ;

            // new Vector3(impulse.z, impulse.x, -impulse.x), scaled by how far the gun already sits back.
            float back = (float)Math.Sqrt(_position[0] * _position[0] + _position[1] * _position[1] + _position[2] * _position[2]);
            float scale = (0.1f + back / PositionLimit) * 100f;
            _rotationVelocity[0] += impulseZ * scale;
            _rotationVelocity[1] += impulseX * scale;
            _rotationVelocity[2] += -impulseX * scale;
        }

        /// <summary>Lets <paramref name="seconds"/> pass: the springs bring the gun back.</summary>
        public void Step(float seconds)
        {
            if (!(seconds > 0f)) return;
            Spring(_position, _positionVelocity, PositionStiffness, PositionDrag, PositionLimit, seconds);
            Spring(_rotation, _rotationVelocity, RotationStiffness, RotationDrag, RotationLimit, seconds);
        }

        /// <summary>The gun at rest: a new life, a new weapon.</summary>
        public void Reset()
        {
            Array.Clear(_position, 0, 3);
            Array.Clear(_positionVelocity, 0, 3);
            Array.Clear(_rotation, 0, 3);
            Array.Clear(_rotationVelocity, 0, 3);
        }

        /// <summary>
        /// The most the kick turns the barrel off the aim at the moment of a shot, degrees, over
        /// <paramref name="seconds"/> of fire as fast as <paramref name="cooldown"/> allows, with the
        /// random part pushed the worst way it can be. What the server's hit judge allows a reported
        /// round beyond the aim (<see cref="WeaponConfig.RecoilAllowance"/>).
        /// </summary>
        public static float WorstDeviationDegrees(float kickback, float randomKick, float cooldown, float seconds = 3f)
        {
            if (!(kickback > 0f) && !(randomKick > 0f)) return 0f;
            float interval = Math.Max(cooldown, MinShotIntervalSeconds);
            const float frame = 1f / 144f;
            float worst = 0f;
            // Straight back, fully sideways, and both at once: the random part's worst directions.
            float[][] pushes = { new[] { 0f, 0f, -1f }, new[] { 1f, 0f, 0f }, new[] { 0.7071f, 0f, -0.7071f } };
            foreach (float[] push in pushes)
            {
                var kick = new RecoilKick();
                float nextShot = 0f;
                for (float t = 0f; t < seconds; t += frame)
                {
                    if (t >= nextShot)
                    {
                        worst = Math.Max(worst, kick.DeviationDegrees);
                        kick.Shot(kickback, randomKick, push[0], push[1], push[2]);
                        nextShot = t + interval;
                    }
                    kick.Step(frame);
                }
            }
            return worst;
        }

        /// <summary>The fastest a semi-automatic trigger is pulled, seconds between shots.</summary>
        public const float MinShotIntervalSeconds = 0.05f;

        private static void Spring(float[] position, float[] velocity, float stiffness, float drag, float limit, float seconds)
        {
            float step = seconds / Iterations;
            for (int i = 0; i < Iterations; i++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    velocity[axis] -= (position[axis] * stiffness + velocity[axis] * drag) * step;
                    float next = position[axis] + velocity[axis] * step;
                    position[axis] = next < -limit ? -limit : next > limit ? limit : next;
                }
            }
        }
    }
}
