namespace Ironfront.Net.Replication
{
    /// <summary>
    /// Placeholder so the assembly is not empty. OWNER: Dev C — delete this file when the
    /// first real replication type lands.
    /// </summary>
    /// <remarks>
    /// Expected contents of this project (architecture.md section 5):
    /// SnapshotBuilder, DeltaEncoder, InterestManager, Messages/ — plus
    /// Serialization/BitWriter.cs and Serialization/BitReader.cs, which belong to Dev B.
    /// <para>
    /// The wire layout for snapshots already exists in
    /// <see cref="Ironfront.Net.Protocol.SnapshotMessage"/>; this project owns the
    /// question of WHICH actors and WHICH fields go into a snapshot, not how they are
    /// laid out in bytes.
    /// </para>
    /// </remarks>
    internal static class ReplicationPlaceholder
    {
        internal const string Owner = "Dev C";
    }
}
