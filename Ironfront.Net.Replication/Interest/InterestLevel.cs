namespace Ironfront.Net.Replication.Interest
{
    /// <summary>
    /// How much of a client's bandwidth one actor is worth this snapshot.
    /// architecture.md section 7.3.
    /// </summary>
    /// <remarks>
    /// Ordered so that <c>&gt;=</c> comparisons read the way they are meant to: "at least
    /// Mid" is <c>level &gt;= InterestLevel.Mid</c>. Both the hitbox-history relevance filter
    /// (risk R6) and the bot AI LOD scheduler are written against that comparison, so the
    /// ordering is load-bearing rather than cosmetic.
    /// </remarks>
    public enum InterestLevel : byte
    {
        /// <summary>Not sent at all. Only beyond <see cref="InterestManager.CullRadius"/>.</summary>
        Culled = 0,

        /// <summary>Sent at 4 Hz. Enough for a minimap marker and a distant silhouette.</summary>
        Far = 1,

        /// <summary>Sent at 10 Hz.</summary>
        Mid = 2,

        /// <summary>Sent every snapshot (20 Hz). Close enough to be shot at.</summary>
        Near = 3,
    }
}
