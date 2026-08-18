using System;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Where an already-framed reliable payload goes. Phase-V8 task 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport and the session list live in Unity (<c>ServerTickLoop.BroadcastReliable</c>),
    /// and every event writer in this library deliberately stops at "here are the bytes". This
    /// one interface is what lets a sender that decides <i>when</i> to write — rather than only
    /// <i>how</i> — be tested in CI: the alternative was a Unity component holding the framing,
    /// which is exactly the arrangement that left <c>S_EXPLOSION</c> with a codec and no sender
    /// for four phases.
    /// </para>
    /// <para>
    /// Deliberately not a <c>SendTo</c>/earshot surface. Vehicle spawn and despawn are facts
    /// every client needs whether or not it can see the pad — a client that misses the spawn has
    /// no vehicle to apply the snapshots that follow it to.
    /// </para>
    /// </remarks>
    public interface IReliablePayloadSender
    {
        /// <summary>Sends one framed payload to every connected client.</summary>
        void BroadcastReliable(ReadOnlySpan<byte> payload, byte channel);
    }
}
