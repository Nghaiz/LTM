namespace Ironfront.Net.Transport
{
    /// <summary>
    /// Placeholder so the assembly is not empty. OWNER: Dev B — delete this file when the
    /// first real transport type lands.
    /// </summary>
    /// <remarks>
    /// Expected contents of this project (architecture.md section 5):
    /// UdpSocketPeer, Connection, ReliabilityLayer, ChannelSet, Fragmentation,
    /// CongestionControl, BufferPool, Simulation/NetworkSimulator.
    /// <para>
    /// Sequence comparisons must go through
    /// <see cref="Ironfront.Net.Protocol.SequenceMath"/>; a raw <c>&gt;</c> on a u16
    /// sequence is risk B2 and works for exactly 36 minutes before it breaks.
    /// </para>
    /// </remarks>
    internal static class TransportPlaceholder
    {
        internal const string Owner = "Dev B";
    }
}
