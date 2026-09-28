using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// One line-of-sight question from <see cref="LagCompensator.Occlusion"/>: is there cover
    /// between where a shot left and the point it hit?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two travels are what a vehicle needs to be judged where the shooter saw it.</b>
    /// The segment lies in the rewound world: the point is on the target as it was at the
    /// rewind tick, and a passenger's origin was moved back the same way. The engine's
    /// colliders stand where they are NOW. For static geometry that makes no difference; for a
    /// hull it does -- a jeep doing 15 m/s has moved 3 m since the tick being judged, so its
    /// present hull is neither where its crew was hit nor where the passenger fired from.
    /// </para>
    /// <para>
    /// A hull moves rigidly with the bodies seated in it, so testing the PRESENT hull against
    /// the segment moved by its crew's travel is the same question as testing the rewound hull
    /// against the segment itself. Translation only, as the travels are.
    /// </para>
    /// </remarks>
    public readonly struct OcclusionQuery
    {
        public OcclusionQuery(
            in Vec3 origin, in Vec3 point, float distance,
            ushort victimActorId, ushort shooterActorId,
            in Vec3 victimTravel, in Vec3 shooterTravel)
        {
            Origin = origin;
            Point = point;
            Distance = distance;
            VictimActorId = victimActorId;
            ShooterActorId = shooterActorId;
            VictimTravel = victimTravel;
            ShooterTravel = shooterTravel;
        }

        /// <summary>Where the shot left, in the rewound world for a passenger.</summary>
        public Vec3 Origin { get; }

        /// <summary>The point on the victim's rewound hitbox the ray struck.</summary>
        public Vec3 Point { get; }

        /// <summary>Origin to point, in metres.</summary>
        public float Distance { get; }

        /// <summary>The actor that was hit. Its own body is never its cover (ledger X-26).</summary>
        public ushort VictimActorId { get; }

        /// <summary>The actor that fired. Its own body is never cover for its shots either.</summary>
        public ushort ShooterActorId { get; }

        /// <summary>
        /// How far the victim has moved since the pose the shot was judged against: present
        /// minus rewound. Zero when the present pose was used.
        /// </summary>
        public Vec3 VictimTravel { get; }

        /// <summary>
        /// How far the shooter has moved since the origin the shot left from. Zero unless the
        /// origin was rewound, which only a passenger's is.
        /// </summary>
        public Vec3 ShooterTravel { get; }
    }
}
