using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication
{
    /// <summary>
    /// One tick of world state, already quantized, held in a reusable fixed-size buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The entries are quantized, and that is the whole design.</b> Storing
    /// <see cref="ActorSnapshotEntry"/> (i16 positions, u16 yaw) rather than floats means the
    /// delta encoder's change detection compares quantized values because there is nothing
    /// else to compare. That closes trap 4 from phase-01 structurally: a stationary actor
    /// whose float position jitters by 0.0001 m from physics quantizes to the same i16 every
    /// tick, so its Position bit stays clear and the delta stays small. Had this class stored
    /// floats, the same code would compile, run, produce correct output, and silently save no
    /// bandwidth at all — a bug with no symptom other than a number in a report being worse
    /// than expected.
    /// </para>
    /// <para>
    /// Sized to <see cref="ProtocolConstants.MAX_ACTORS"/> and never resized, so a server
    /// holding 33 of these (32 baselines plus the live one) allocates them once at startup
    /// and never again.
    /// </para>
    /// </remarks>
    public sealed class WorldSnapshot
    {
        /// <summary>The server tick this state was captured at.</summary>
        public uint ServerTick;

        /// <summary>Live entries in <see cref="Actors"/>. Everything past this is stale.</summary>
        public int ActorCount;

        /// <summary>Fixed-capacity backing store. Index with <see cref="ActorCount"/>, not Length.</summary>
        public readonly ActorSnapshotEntry[] Actors = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];

        /// <summary>Capacity, for callers that want to bounds-check before adding.</summary>
        public int Capacity => Actors.Length;

        /// <summary>Drops every actor. Does not clear the array — <see cref="ActorCount"/> is the fence.</summary>
        public void Clear()
        {
            ActorCount = 0;
            ServerTick = 0;
        }

        /// <summary>
        /// Appends an actor. Returns false when full rather than throwing: at
        /// <see cref="ProtocolConstants.MAX_ACTORS"/> a full server is a normal operating
        /// condition, not an exception.
        /// </summary>
        public bool Add(in ActorSnapshotEntry entry)
        {
            if (ActorCount >= Actors.Length) return false;
            Actors[ActorCount++] = entry;
            return true;
        }

        /// <summary>
        /// Finds an actor by id.
        /// </summary>
        /// <remarks>
        /// A linear scan over at most 64 contiguous structs, called once per actor per
        /// snapshot. A dictionary would be asymptotically better and measurably slower here,
        /// and would allocate.
        /// </remarks>
        public bool TryFind(ushort actorId, out ActorSnapshotEntry entry)
        {
            for (int i = 0; i < ActorCount; i++)
            {
                if (Actors[i].ActorId != actorId) continue;
                entry = Actors[i];
                return true;
            }

            entry = default;
            return false;
        }

        /// <summary>Index of an actor, or -1. For in-place updates.</summary>
        public int IndexOf(ushort actorId)
        {
            for (int i = 0; i < ActorCount; i++)
                if (Actors[i].ActorId == actorId) return i;

            return -1;
        }

        /// <summary>
        /// Overwrites this snapshot with another. Used to file the live state into the
        /// baseline history without allocating.
        /// </summary>
        public void CopyFrom(WorldSnapshot other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            ServerTick = other.ServerTick;
            ActorCount = other.ActorCount;
            Array.Copy(other.Actors, Actors, other.ActorCount);
        }
    }
}
