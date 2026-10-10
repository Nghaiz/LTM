using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>What the server made of one reported hit.</summary>
    public enum ReportVerdict : byte
    {
        Accepted = 0,

        /// <summary>No accepted pull of that weapon near that tick, or its round was reported already.</summary>
        NoSuchShot,

        /// <summary>The shooter named themselves.</summary>
        Self,

        /// <summary>The target is not a living body the server knows.</summary>
        NoTarget,

        /// <summary>Further than the weapon reaches.</summary>
        OutOfRange,

        /// <summary>Nowhere near where the pull was aimed.</summary>
        OffAim,

        /// <summary>Nowhere near where the target's body was at the tick the shooter was drawing.</summary>
        NotOnBody,

        /// <summary>A crew member in an enclosed seat, which only a piercing round reaches.</summary>
        EnclosedSeat,

        /// <summary>A wall between the eye and the point (the engine's check).</summary>
        Occluded,
    }

    /// <summary>
    /// The server's check of a hit the shooter's own game reported (14.0.6, "what you see is what
    /// you hit"): plausible for the pull it names, on the target's body as the server recorded it,
    /// and inside the weapon's reach. Engine-free; the walls are the caller's
    /// (<see cref="Chords"/> feeds its line-of-sight query).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it defends against, and what it does not.</b> It stops a client claiming a body it
    /// could not have hit: behind it, across the map, off the aim of the pull, or a box the target
    /// never stood in. It does not try to tell a steady hand from an aimbot -- an aimbot aims, and
    /// its rounds then really do hit -- which no server-side sweep ever did either.
    /// </para>
    /// <para>
    /// <b>Generous on purpose.</b> The shooter's screen drew the body interpolated (and past 100 m
    /// extrapolated) from the server's samples, animated, where the server keeps a box per pose.
    /// A tolerance tight enough to reject an honest shot is the bug this protocol exists to fix.
    /// The numbers are below, named, and every one has a test at its edge.
    /// </para>
    /// </remarks>
    public static class ReportedHitJudge
    {
        /// <summary>Metres past the weapon's range a reported round may still land (rounding, the eye-to-muzzle gap).</summary>
        public const float RangeSlackMetres = 5f;

        /// <summary>The shooter's spread, at most this many times the weapon's base spread (aiming, moving, a long burst).</summary>
        public const float SpreadAllowanceFactor = 3f;

        /// <summary>Fixed angular slack on top of the spread and the swing, radians (1 degree).</summary>
        public const float AimSlackRadians = 0.0175f;

        /// <summary>
        /// The most of <see cref="ReportedShot.AimSwing"/> that widens the aim, radians (20 degrees):
        /// a frame-to-frame swing wider than that is no hand on a mouse.
        /// </summary>
        public const float MaxAimSwingRadians = 0.35f;

        /// <summary>How far the muzzle a round leaves from may sit from the eye the server aims from, metres.</summary>
        public const float MuzzleOffsetMetres = 0.6f;

        /// <summary>
        /// <see cref="ReportedShot.OriginSlack"/> for a passenger: how far their own seat may stand
        /// from where the server holds it, metres (a jeep at 10 m/s, drawn 0.3 s behind).
        /// </summary>
        public const float PassengerOriginSlackMetres = 3f;

        /// <summary>
        /// The longest zero a scope may be set to, metres, capped by the weapon's own range: how high
        /// above the sight a round may pass.
        /// </summary>
        public const float MaxZeroMetres = 1500f;

        /// <summary>How far a reported point may lie from the recorded body and still be on it, metres.</summary>
        public const float BodyToleranceMetres = 0.75f;

        /// <summary>How far a reported headshot may lie from the recorded head box and still be one, metres.</summary>
        public const float HeadToleranceMetres = 0.45f;

        /// <summary>
        /// Seconds of the target's own movement added to both tolerances: a body past 100 m is drawn
        /// up to eight ticks past its newest sample.
        /// </summary>
        public const float MovementSlackSeconds = 0.3f;

        /// <summary>The most the movement slack adds, metres.</summary>
        public const float MaxMovementSlackMetres = 2.5f;

        /// <summary>
        /// Judges <paramref name="claim"/> against <paramref name="shot"/>. <paramref name="poses"/> are
        /// the target's recorded boxes at the ticks around the claim's render tick (at least one);
        /// <paramref name="targetSpeed"/> is its speed over them, metres a second.
        /// </summary>
        /// <param name="hitbox">The box the hit counts on: a head claim too far from the head is a body hit.</param>
        /// <param name="distance">Metres from the eye to the point, for drop-off.</param>
        public static ReportVerdict Judge(
            in ReportedShot shot, in WeaponConfig config, in ShotReportHit claim, ushort targetActorId,
            bool targetAlive, bool targetInEnclosedSeat, ReadOnlySpan<HitboxSet> poses, float targetSpeed,
            out HitboxType hitbox, out float distance)
        {
            hitbox = HitboxType.Body;
            distance = 0f;

            if (targetActorId == shot.Shooter) return ReportVerdict.Self;
            if (!targetAlive || poses.Length == 0) return ReportVerdict.NoTarget;
            if (targetInEnclosedSeat && !config.Piercing) return ReportVerdict.EnclosedSeat;

            var point = new Vec3(claim.PointX, claim.PointY, claim.PointZ);
            Vec3 toPoint = point - shot.Origin;
            distance = toPoint.Magnitude;
            if (!IsFinite(in point) || distance > config.Range + RangeSlackMetres) return ReportVerdict.OutOfRange;

            if (!IsOnAim(in shot, in config, in toPoint, distance)) return ReportVerdict.OffAim;

            float slack = Math.Min(MaxMovementSlackMetres, Math.Max(0f, targetSpeed) * MovementSlackSeconds);
            float head = float.MaxValue, body = float.MaxValue;
            for (int i = 0; i < poses.Length; i++)
            {
                HitboxSet pose = poses[i];
                head = Math.Min(head, DistanceTo(in pose.Head, in point));
                body = Math.Min(body, DistanceTo(in pose.Torso, in point));
                body = Math.Min(body, DistanceTo(in pose.Arms, in point));
                body = Math.Min(body, DistanceTo(in pose.Legs, in point));
            }

            if (Math.Min(head, body) > BodyToleranceMetres + slack) return ReportVerdict.NotOnBody;

            hitbox = claim.HitboxType == HitboxType.Head && head <= HeadToleranceMetres + slack
                ? HitboxType.Head
                : claim.HitboxType == HitboxType.Limb ? HitboxType.Limb : HitboxType.Body;
            return ReportVerdict.Accepted;
        }

        /// <summary>
        /// Whether a round of this pull could have reached the point: inside the spread, the
        /// muzzle's offset from the eye, and, for a round that flies an arc, anywhere between
        /// the drop under the sight and the rise of the longest zero.
        /// </summary>
        public static bool IsOnAim(in ReportedShot shot, in WeaponConfig config, in Vec3 toPoint, float distance)
        {
            if (distance < 1e-3f) return true;
            Vec3 aim = shot.Aim.Normalized;
            float cos = (toPoint.X * aim.X + toPoint.Y * aim.Y + toPoint.Z * aim.Z) / distance;
            if (cos > 1f) cos = 1f;
            if (cos < -1f) cos = -1f;
            double angle = Math.Acos(cos);

            // The swing: the shooter's game fired on its own frame, between two of the frames the
            // server holds, along the aim of that moment. A still hand earns nothing; a flick
            // earns as much as the mouse actually moved around the pull.
            double allowed = config.Spread * SpreadAllowanceFactor + AimSlackRadians
                             + Math.Min(MaxAimSwingRadians, Math.Max(0f, shot.AimSwing))
                             + Math.Atan2(MuzzleOffsetMetres + Math.Max(0f, shot.OriginSlack), distance);
            RoundBallistics round = config.Round;
            if (round.IsBallistic)
            {
                float drop = Math.Abs(round.DropBelowSight(distance));
                // A scope zeroed out to its weapon's range at most: a pistol is never zeroed for 1,500 m.
                float zero = Math.Min(MaxZeroMetres, config.Range);
                float rise = Math.Max(0f, distance * ZeroAngle(in round, zero) - round.DropBelowBore(distance));
                allowed += Math.Atan2(Math.Max(drop, rise), distance);
            }
            return angle <= allowed;
        }

        /// <summary>
        /// Points along the round's way from the eye to the reported point, for the caller's
        /// line-of-sight checks: a straight chord for a short shot, and for a long one the arc an
        /// aimed round sags under (it never rises above the line it was zeroed on by more than its
        /// own drop). Returns how many points were written, the eye and the point included.
        /// </summary>
        public static int Chords(in Vec3 origin, in Vec3 point, in RoundBallistics round, Span<Vec3> into)
        {
            if (into.Length < 2) return 0;
            float distance = Vec3.Distance(in origin, in point);
            if (!round.IsBallistic || distance < 150f || into.Length < 3)
            {
                into[0] = origin;
                into[1] = point;
                return 2;
            }

            // The arc between the two ends rises above their chord by the drop at mid-flight; an
            // arc through both ends with that sag at its middle is what the round flew past.
            float sag = round.DropBelowBore(distance) * 0.25f;
            int n = Math.Min(into.Length, 9);
            for (int i = 0; i < n; i++)
            {
                float s = i / (float)(n - 1);
                Vec3 along = origin + (point - origin) * s;
                into[i] = new Vec3(along.X, along.Y + 4f * sag * s * (1f - s), along.Z);
            }
            return n;
        }

        /// <summary>Metres from <paramref name="point"/> to <paramref name="box"/>, 0 inside it.</summary>
        public static float DistanceTo(in Aabb box, in Vec3 point)
        {
            if (box.IsEmpty) return float.MaxValue;
            float dx = Math.Max(0f, Math.Abs(point.X - box.Center.X) - box.Extents.X);
            float dy = Math.Max(0f, Math.Abs(point.Y - box.Center.Y) - box.Extents.Y);
            float dz = Math.Max(0f, Math.Abs(point.Z - box.Center.Z) - box.Extents.Z);
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>The bore's angle above the sight for a zero at <paramref name="metres"/>, radians.</summary>
        private static float ZeroAngle(in RoundBallistics round, float metres)
            => metres > 0f ? round.DropBelowBore(metres) / metres : 0f;

        private static bool IsFinite(in Vec3 v)
            => !float.IsNaN(v.X) && !float.IsInfinity(v.X)
               && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y)
               && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }
}
