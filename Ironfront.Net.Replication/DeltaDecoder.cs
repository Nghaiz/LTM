using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication
{
    /// <summary>Why <see cref="DeltaDecoder.Read"/> refused a snapshot.</summary>
    public enum SnapshotReadResult
    {
        /// <summary>Applied. <see cref="DeltaDecoder.Current"/> is the new world state.</summary>
        Applied = 0,

        /// <summary>Body was truncated or malformed. Dropped.</summary>
        Malformed = 1,

        /// <summary>
        /// A delta against a baseline this client does not have — it was lost, or aged out of
        /// history. Not an error: ack the newest tick actually held and wait for the server to
        /// fall back to a full snapshot.
        /// </summary>
        UnknownBaseline = 2,

        /// <summary>Older than one already applied. Dropped (protocol-spec.md section 5).</summary>
        Stale = 3,
    }

    /// <summary>
    /// The client half of delta encoding: rebuilds full world state from a full snapshot or a
    /// delta plus the baseline it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Trap 5, and it is the one that looks obviously fine in review.</b> When a delta
    /// arrives with the Position bit clear, that means "unchanged", so the position must be
    /// carried over from the baseline. The natural way to write the loop — build a fresh
    /// entry, fill in the fields the mask advertises — leaves every unmentioned field at its
    /// struct default, so an actor that stopped moving teleports to the world origin. The
    /// decoder therefore starts each entry as a copy of its baseline and overwrites only what
    /// the mask carries, which is why <see cref="ApplyEntry"/> takes the baseline entry by
    /// reference rather than constructing a new one.
    /// </para>
    /// <para>
    /// Keeps its own history so it can decode a delta naming any baseline it has recently
    /// acked, not just the newest.
    /// </para>
    /// </remarks>
    public sealed class DeltaDecoder
    {
        private readonly WorldSnapshot[] _history;
        private readonly ActorSnapshotEntry[] _scratch;
        private bool _hasApplied;

        public DeltaDecoder()
        {
            _history = new WorldSnapshot[DeltaEncoder.BaselineHistory];
            for (int i = 0; i < _history.Length; i++) _history[i] = new WorldSnapshot();

            _scratch = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];
            Current  = new WorldSnapshot();
        }

        /// <summary>The reconstructed world. Valid once <see cref="Read"/> has returned Applied.</summary>
        public WorldSnapshot Current { get; }

        /// <summary>
        /// The tick to put in the next C_ACK_BASELINE. 0 until the first snapshot lands.
        /// </summary>
        public uint AckTick => _hasApplied ? Current.ServerTick : 0u;

        /// <summary>Last <c>lastProcessedInputTick</c> the server reported. Drives reconciliation.</summary>
        public uint LastProcessedInputTick { get; private set; }

        public long AppliedCount { get; private set; }
        public long UnknownBaselineCount { get; private set; }
        public long StaleCount { get; private set; }

        /// <summary>Forgets everything. Used when the connection restarts.</summary>
        public void Reset()
        {
            _hasApplied = false;
            Current.Clear();
            LastProcessedInputTick = 0;
            for (int i = 0; i < _history.Length; i++) _history[i].Clear();
        }

        /// <summary>
        /// Decodes an S_SNAPSHOT body and, on success, updates <see cref="Current"/>.
        /// </summary>
        public SnapshotReadResult Read(ReadOnlySpan<byte> body)
        {
            if (!SnapshotMessage.TryParse(body, _scratch, out SnapshotHeader header, out int count))
                return SnapshotReadResult.Malformed;

            // Channel 1 is unreliable-sequenced, so the transport already drops packets older
            // than one delivered. Checking again here costs nothing and means a decoder fed
            // from a replay, a test, or a future batched channel cannot be walked backwards.
            if (_hasApplied && !SequenceMath.IsNewer32(header.ServerTick, Current.ServerTick))
            {
                StaleCount++;
                return SnapshotReadResult.Stale;
            }

            if (header.IsFullSnapshot)
            {
                ApplyFull(in header, count);
                return Finish(in header);
            }

            WorldSnapshot baseline = _history[header.BaselineTick % DeltaEncoder.BaselineHistory];
            if (!_hasApplied || baseline.ServerTick != header.BaselineTick)
            {
                UnknownBaselineCount++;
                return SnapshotReadResult.UnknownBaseline;
            }

            ApplyDelta(in header, count, baseline);
            return Finish(in header);
        }

        private SnapshotReadResult Finish(in SnapshotHeader header)
        {
            LastProcessedInputTick = header.LastProcessedInputTick;
            _hasApplied = true;
            AppliedCount++;

            // File it so a later delta may name it as a baseline. The server only deltas
            // against ticks this client acked, and it only acks ticks it applied, so the two
            // histories stay in step.
            _history[Current.ServerTick % DeltaEncoder.BaselineHistory].CopyFrom(Current);

            return SnapshotReadResult.Applied;
        }

        private void ApplyFull(in SnapshotHeader header, int count)
        {
            Current.Clear();
            Current.ServerTick = header.ServerTick;

            for (int i = 0; i < count; i++) Current.Add(in _scratch[i]);
        }

        private void ApplyDelta(in SnapshotHeader header, int count, WorldSnapshot baseline)
        {
            // Rebuild into Current from scratch. Actors present in the baseline but absent
            // from this snapshot are simply not re-added, which is how a despawn arrives:
            // the snapshot's actor list is authoritative about who exists, and the changeMask
            // is only authoritative about which of their fields moved.
            Current.Clear();
            Current.ServerTick = header.ServerTick;

            for (int i = 0; i < count; i++)
            {
                ref ActorSnapshotEntry incoming = ref _scratch[i];

                ActorSnapshotEntry resolved =
                    baseline.TryFind(incoming.ActorId, out ActorSnapshotEntry previous)
                        ? ApplyEntry(in previous, in incoming)
                        : incoming;

                // The stored entry carries every field, not the sparse wire mask, so it can
                // serve as a baseline itself next tick.
                resolved.ChangeMask = SnapshotField.FullNoSeat;
                Current.Add(in resolved);
            }
        }

        /// <summary>
        /// Overlays the fields <paramref name="incoming"/> actually carries onto
        /// <paramref name="baseline"/>. Everything else is inherited.
        /// </summary>
        public static ActorSnapshotEntry ApplyEntry(
            in ActorSnapshotEntry baseline, in ActorSnapshotEntry incoming)
        {
            ActorSnapshotEntry result = baseline;
            result.ActorId = incoming.ActorId;

            SnapshotField mask = incoming.ChangeMask;

            if ((mask & SnapshotField.Position) != 0)
            {
                result.PosX = incoming.PosX;
                result.PosY = incoming.PosY;
                result.PosZ = incoming.PosZ;
            }

            if ((mask & SnapshotField.Rotation) != 0)
            {
                result.Yaw   = incoming.Yaw;
                result.Pitch = incoming.Pitch;
            }

            if ((mask & SnapshotField.Velocity) != 0)
            {
                result.VelX = incoming.VelX;
                result.VelY = incoming.VelY;
                result.VelZ = incoming.VelZ;
            }

            if ((mask & SnapshotField.StateFlags) != 0) result.StateFlags = incoming.StateFlags;
            if ((mask & SnapshotField.Health)     != 0) result.Health     = incoming.Health;

            if ((mask & SnapshotField.Weapon) != 0)
            {
                result.WeaponId   = incoming.WeaponId;
                result.AmmoInClip = incoming.AmmoInClip;
            }

            if ((mask & SnapshotField.Team) != 0) result.Team = incoming.Team;

            if ((mask & SnapshotField.SeatInfo) != 0)
            {
                result.VehicleId = incoming.VehicleId;
                result.SeatIndex = incoming.SeatIndex;
            }

            return result;
        }
    }
}
