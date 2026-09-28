using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>One actor a hitscan shot may resolve against.</summary>
    /// <remarks>
    /// <see cref="Present"/> is the pose right now. It is the fallback when history has no
    /// frame at the rewind tick — see <see cref="LagCompensator"/> — and it is what bots and
    /// zero-RTT shooters are tested against, since rewinding zero ticks resolves to the
    /// present anyway.
    /// </remarks>
    public readonly struct HitscanTarget
    {
        public readonly ushort ActorId;
        public readonly bool IsAlive;
        public readonly HitboxSet Present;

        /// <summary>
        /// Sitting in an enclosed seat -- a tank's or a helicopter's crew -- which only a piercing
        /// round reaches.
        /// </summary>
        /// <remarks>
        /// The original's rule, <c>Actor.DamageAttributed</c>: <c>IsSeated() &amp;&amp;
        /// seat.enclosed</c> refuses every non-piercing hit. The server's hitscan path owns
        /// health itself and never runs that method, so a rifle round that found a gap in a hull
        /// killed the pilot behind it (playtest 2026-09-28, bug 5 audit).
        /// </remarks>
        public readonly bool InEnclosedSeat;

        public HitscanTarget(ushort actorId, bool isAlive, in HitboxSet present)
            : this(actorId, isAlive, in present, inEnclosedSeat: false)
        {
        }

        public HitscanTarget(ushort actorId, bool isAlive, in HitboxSet present, bool inEnclosedSeat)
        {
            ActorId = actorId;
            IsAlive = isAlive;
            Present = present;
            InEnclosedSeat = inEnclosedSeat;
        }
    }

    /// <summary>What a hitscan shot found.</summary>
    public readonly struct HitResult
    {
        public readonly bool Hit;
        public readonly ushort TargetActorId;
        public readonly HitboxType HitboxType;
        public readonly Vec3 Point;
        public readonly float Distance;

        /// <summary>The tick the world was evaluated at. Equals the fire tick when RTT is 0.</summary>
        public readonly uint ResolvedAtTick;

        /// <summary>
        /// True when the target's pose came from the live world because history had no frame
        /// at the rewind tick.
        /// </summary>
        public readonly bool UsedPresentFallback;

        public HitResult(
            bool hit, ushort targetActorId, HitboxType hitboxType, in Vec3 point,
            float distance, uint resolvedAtTick, bool usedPresentFallback)
        {
            Hit = hit;
            TargetActorId = targetActorId;
            HitboxType = hitboxType;
            Point = point;
            Distance = distance;
            ResolvedAtTick = resolvedAtTick;
            UsedPresentFallback = usedPresentFallback;
        }

        public static HitResult Miss(uint resolvedAtTick)
            => new HitResult(false, 0, HitboxType.Body, Vec3.Zero, 0f, resolvedAtTick, false);

        public bool IsHeadshot => Hit && HitboxType == HitboxType.Head;
    }
}
