namespace Ironfront.Net.Replication
{
    /// <summary>
    /// Switches for each compression technique, so phase-04's experiment can turn them on one
    /// at a time and attribute the saving to each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C. Phase-04 task 1.
    /// </para>
    /// <para>
    /// <b>Two of these flags are not honoured by the shipped encoder, on purpose.</b> The v1
    /// wire format froze byte-aligned (protocol-spec.md section 4.3), so
    /// <see cref="UseBitPacking"/> and <see cref="UseCompactHeight"/> describe formats the
    /// server is not allowed to emit — turning them on in production would be the unannounced
    /// wire-format change phase 01 already declined to ship. They are read by the experiment
    /// codec in the test project, which encodes and decodes both sides itself, so the report
    /// gets a measured number for each technique without the server ever putting one on a
    /// socket.
    /// </para>
    /// <para>
    /// The rest — interest management, delta encoding, velocity culling, dropping stale dead
    /// actors — are decisions <i>within</i> the frozen format (which actors go in a snapshot,
    /// and which change-mask bits are set), so those are live on the shipped path and default
    /// to on.
    /// </para>
    /// </remarks>
    public sealed class ReplicationConfig
    {
        /// <summary>
        /// Pack fields to bit boundaries instead of byte boundaries.
        /// <b>Experiment only</b> — see the type remarks.
        /// </summary>
        public bool UseBitPacking { get; set; }

        /// <summary>Delta against the client's acked baseline rather than sending full snapshots.</summary>
        public bool UseDeltaEncoding { get; set; } = true;

        /// <summary>Filter and rate-limit actors per client by distance.</summary>
        public bool UseInterestManagement { get; set; } = true;

        /// <summary>
        /// Stop sending velocity for actors below <see cref="Interest.InterestLevel.Near"/>.
        /// The client estimates it from consecutive positions, which at 10 Hz and 4 Hz is
        /// what it is already doing to interpolate between them.
        /// </summary>
        public bool UseVelocityCulling { get; set; } = true;

        /// <summary>
        /// Send height in 12 bits rather than 16. <b>Experiment only</b> — it is a change to
        /// the position field's byte layout.
        /// </summary>
        public bool UseCompactHeight { get; set; }

        /// <summary>
        /// Stop sending pitch beyond <see cref="DistantPitchMetres"/>.
        /// <b>Experiment only</b> — yaw and pitch share one change-mask bit in the frozen
        /// format, so suppressing pitch alone is not expressible on the v1 wire.
        /// </summary>
        public bool UseDistantPitchCulling { get; set; }

        /// <summary>
        /// Drop an actor from snapshots once it has been dead for
        /// <see cref="DeadActorHoldSeconds"/>. Corpses are never synchronised anyway (AD-4) —
        /// the client owns its own ragdoll — so the entries after that are describing a body
        /// nobody is going to move again.
        /// </summary>
        public bool DropStaleDeadActors { get; set; } = true;

        /// <summary>Seconds a dead actor stays in the snapshot before being dropped.</summary>
        public float DeadActorHoldSeconds { get; set; } = 3f;

        /// <summary>Range past which pitch stops being visible. Experiment only.</summary>
        public float DistantPitchMetres { get; set; } = 50f;

        /// <summary>
        /// Everything the shipped server actually does. A new instance rather than a shared
        /// singleton, so a test that changes a flag cannot change it for every other test.
        /// </summary>
        public static ReplicationConfig Shipped => new ReplicationConfig();

        /// <summary>
        /// The phase-04 baseline row: full snapshots, no interest management, nothing culled.
        /// </summary>
        public static ReplicationConfig Baseline => new ReplicationConfig
        {
            UseBitPacking          = false,
            UseDeltaEncoding       = false,
            UseInterestManagement  = false,
            UseVelocityCulling     = false,
            UseCompactHeight       = false,
            UseDistantPitchCulling = false,
            DropStaleDeadActors    = false,
        };
    }
}
