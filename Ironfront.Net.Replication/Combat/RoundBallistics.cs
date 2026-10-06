using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// How one kind of bullet flies: its muzzle velocity, the air's drag on it, and the range
    /// its sights are zeroed at. The same flight for the server's judgement of a player's shot,
    /// for a bot's round, and for every client's drawing of either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner request 2026-10-06</b>: rifles, pistols, shotguns and sniper rifles must each
    /// fly their own trajectory under gravity, not a dead-straight line -- "a sniper aimed right
    /// is sure to hit a target 300 m away; a shotgun cannot" -- by the real physics, without
    /// buffing or nerfing any weapon. Gravity is the same 9.81 m/s² for every bullet, as it is
    /// in the world; what differs is how fast each leaves the muzzle and how quickly the air
    /// slows it, so each weapon drops differently over the same distance.
    /// </para>
    /// <para>
    /// <b>The flight, in closed form</b>, so every peer computes the same point for the same
    /// time whatever its frame rate. Along the launch direction quadratic air drag
    /// (dv/dt = −k·v²) gives s(t) = ln(1 + k·v₀·t)/k and v(t) = v₀/(1 + k·v₀·t); gravity adds
    /// ½·g·t² straight down. That is the flat-fire approximation small-arms ballistics tables use:
    /// it neglects the drag on the small vertical speed, which at rifle angles moves the point of
    /// impact by millimetres. <see cref="DragPerMetre"/> is k, fitted to each cartridge's real
    /// velocity loss (v falls by e^(−k·x) over x metres).
    /// </para>
    /// <para>
    /// <b>Zeroing.</b> A real rifle's barrel points slightly above its sight line, so the bullet
    /// rises through the line of sight and falls back onto it at the zero range: the shooter aims
    /// straight at a target there, holds a little high beyond it. <see cref="LaunchDirection"/>
    /// tilts the aim up by exactly the drop at <see cref="ZeroMetres"/>.
    /// </para>
    /// </remarks>
    public readonly struct RoundBallistics
    {
        /// <summary>Standard gravity, m/s².</summary>
        public const float Gravity = 9.81f;

        /// <summary>Speed leaving the muzzle, m/s.</summary>
        public readonly float MuzzleVelocity;

        /// <summary>Quadratic drag constant k, per metre: the speed falls by e^(−k·x) over x metres.</summary>
        public readonly float DragPerMetre;

        /// <summary>The range, metres, at which the round crosses the sight line on its way down; 0 for none.</summary>
        public readonly float ZeroMetres;

        public RoundBallistics(float muzzleVelocity, float dragPerMetre, float zeroMetres)
        {
            MuzzleVelocity = muzzleVelocity;
            DragPerMetre = dragPerMetre;
            ZeroMetres = zeroMetres;
        }

        /// <summary>Whether this describes a flight at all: a round with no muzzle velocity is a straight ray.</summary>
        public bool IsBallistic => MuzzleVelocity > 0f;

        /// <summary>Metres flown along the launch direction after <paramref name="seconds"/>.</summary>
        public float AlongBore(float seconds)
        {
            if (!(seconds > 0f)) return 0f;
            if (!(DragPerMetre > 0f)) return MuzzleVelocity * seconds;
            return (float)(Math.Log(1.0 + DragPerMetre * MuzzleVelocity * seconds) / DragPerMetre);
        }

        /// <summary>Speed along the launch direction after <paramref name="seconds"/>, m/s.</summary>
        public float SpeedAt(float seconds)
        {
            if (!(seconds > 0f)) return MuzzleVelocity;
            return MuzzleVelocity / (1f + DragPerMetre * MuzzleVelocity * seconds);
        }

        /// <summary>Seconds to fly <paramref name="metres"/> along the launch direction.</summary>
        public float TimeToTravel(float metres)
        {
            if (!(metres > 0f) || !IsBallistic) return 0f;
            if (!(DragPerMetre > 0f)) return metres / MuzzleVelocity;
            return (float)((Math.Exp(DragPerMetre * metres) - 1.0) / (DragPerMetre * MuzzleVelocity));
        }

        /// <summary>How far below the bore line the round has fallen after flying <paramref name="metres"/>.</summary>
        public float DropBelowBore(float metres)
        {
            float t = TimeToTravel(metres);
            return 0.5f * Gravity * t * t;
        }

        /// <summary>The angle, radians, the barrel points above the sight line so the round meets it at the zero range.</summary>
        public float ZeroElevation => ZeroMetres > 0f && IsBallistic
            ? (float)Math.Atan(DropBelowBore(ZeroMetres) / ZeroMetres)
            : 0f;

        /// <summary>
        /// How far below the line of sight the round passes <paramref name="metres"/> out: negative
        /// before the zero range (it is still rising through the sight line), zero at it, growing
        /// past it. What a shooter -- or a bot -- holds over by.
        /// </summary>
        public float DropBelowSight(float metres)
            => DropBelowBore(metres) - metres * (float)Math.Tan(ZeroElevation);

        /// <summary>
        /// The direction the round leaves the muzzle when the sights look along
        /// <paramref name="aim"/>: tilted up by <see cref="ZeroElevation"/> in the vertical plane.
        /// </summary>
        public Vec3 LaunchDirection(in Vec3 aim)
        {
            Vec3 forward = aim.Normalized;
            float elevation = ZeroElevation;
            if (elevation == 0f) return forward;

            // World up with the aim taken out of it: "up" as the shooter sees it. Straight up or
            // down there is no such direction and nothing to tilt.
            float along = forward.Y;
            Vec3 lift = new Vec3(-forward.X * along, 1f - along * along, -forward.Z * along);
            if (lift.SqrMagnitude < 1e-6f) return forward;
            lift = lift.Normalized;

            float cos = (float)Math.Cos(elevation);
            float sin = (float)Math.Sin(elevation);
            return (forward * cos + lift * sin).Normalized;
        }

        /// <summary>
        /// Where a round launched from <paramref name="muzzle"/> along <paramref name="launch"/>
        /// (a unit vector, <see cref="LaunchDirection"/>) is after <paramref name="seconds"/>.
        /// </summary>
        public Vec3 PositionAt(in Vec3 muzzle, in Vec3 launch, float seconds)
        {
            float s = AlongBore(seconds);
            float fall = 0.5f * Gravity * seconds * seconds;
            return new Vec3(muzzle.X + launch.X * s, muzzle.Y + launch.Y * s - fall, muzzle.Z + launch.Z * s);
        }

        /// <summary>The round's velocity after <paramref name="seconds"/>.</summary>
        public Vec3 VelocityAt(in Vec3 launch, float seconds)
        {
            float v = SpeedAt(seconds);
            return new Vec3(launch.X * v, launch.Y * v - Gravity * seconds, launch.Z * v);
        }
    }
}
