using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication
{
    /// <summary>
    /// One tick of vehicle state, already quantized, in a reusable fixed-size buffer.
    /// The vehicle counterpart of <see cref="WorldSnapshot"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The entries are quantized, and that is the whole design</b> — the same reason
    /// <see cref="WorldSnapshot"/> gives. Change detection compares quantized values because
    /// there is nothing else to compare, so a vehicle idling on a slope whose float position
    /// jitters below the 6.25 cm position resolution keeps its Position bit clear. Storing
    /// floats here would compile, run, produce correct output, and save no bandwidth at all.
    /// </para>
    /// <para>
    /// Sized to <see cref="ProtocolConstants.MAX_VEHICLES"/> and never resized. At 16 entries
    /// this is a fraction of the actor buffer, so a server holding 33 of them per client
    /// (32 baselines plus the live one) allocates them once at startup.
    /// </para>
    /// </remarks>
    public sealed class VehicleWorldSnapshot
    {
        /// <summary>The server tick this state was captured at.</summary>
        public uint ServerTick;

        /// <summary>Live entries in <see cref="Vehicles"/>. Everything past this is stale.</summary>
        public int VehicleCount;

        /// <summary>Fixed-capacity backing store. Index with <see cref="VehicleCount"/>.</summary>
        public readonly VehicleSnapshotEntry[] Vehicles =
            new VehicleSnapshotEntry[ProtocolConstants.MAX_VEHICLES];

        /// <summary>Capacity, for callers that want to bounds-check before adding.</summary>
        public int Capacity => Vehicles.Length;

        /// <summary>Drops every vehicle. <see cref="VehicleCount"/> is the fence.</summary>
        public void Clear()
        {
            VehicleCount = 0;
            ServerTick   = 0;
        }

        /// <summary>
        /// Appends a vehicle. Returns false when full rather than throwing: at
        /// <see cref="ProtocolConstants.MAX_VEHICLES"/> a busy map is a normal operating
        /// condition, not an exception.
        /// </summary>
        public bool Add(in VehicleSnapshotEntry entry)
        {
            if (VehicleCount >= Vehicles.Length) return false;
            Vehicles[VehicleCount++] = entry;
            return true;
        }

        /// <summary>Finds a vehicle by id. A linear scan over at most 16 contiguous structs.</summary>
        public bool TryFind(ushort vehicleId, out VehicleSnapshotEntry entry)
        {
            for (int i = 0; i < VehicleCount; i++)
            {
                if (Vehicles[i].VehicleId != vehicleId) continue;
                entry = Vehicles[i];
                return true;
            }

            entry = default;
            return false;
        }

        /// <summary>Index of a vehicle, or -1. For in-place updates.</summary>
        public int IndexOf(ushort vehicleId)
        {
            for (int i = 0; i < VehicleCount; i++)
                if (Vehicles[i].VehicleId == vehicleId) return i;

            return -1;
        }

        /// <summary>
        /// Overwrites this snapshot with another, to file live state into the baseline history
        /// without allocating.
        /// </summary>
        public void CopyFrom(VehicleWorldSnapshot other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            ServerTick   = other.ServerTick;
            VehicleCount = other.VehicleCount;
            Array.Copy(other.Vehicles, Vehicles, other.VehicleCount);
        }
    }
}
