using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Client;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>One actor, as this client decoded it.</summary>
    /// <remarks>
    /// Quantized values, deliberately: <see cref="ActorSnapshotEntry.PosX"/> is what came off
    /// the wire, and de-quantizing here would introduce a float the server never sent. Two
    /// clients agreeing is then an exact integer comparison rather than an epsilon nobody
    /// chose — which is the whole of check 7.
    /// </remarks>
    public readonly struct ActorSample
    {
        public readonly ushort ActorId;
        public readonly short X, Y, Z;
        public readonly byte Health;
        public readonly ActorStateFlags Flags;

        /// <summary>The server tick this client last received a position for this actor on.</summary>
        /// <remarks>
        /// Not the tick the sample was captured at, and the gap between the two is X-35. Two
        /// clients differing at the same capture tick have diverged only if these agree; if
        /// they do not, one of them simply holds an older copy of a world that is still moving.
        /// </remarks>
        public readonly uint UpdatedAtTick;

        public ActorSample(in ActorSnapshotEntry entry, uint updatedAtTick)
        {
            ActorId = entry.ActorId;
            X = entry.PosX;
            Y = entry.PosY;
            Z = entry.PosZ;
            Health = entry.Health;
            Flags = entry.StateFlags;
            UpdatedAtTick = updatedAtTick;
        }
    }

    /// <summary>One vehicle, as this client decoded it.</summary>
    public readonly struct VehicleSample
    {
        public readonly ushort VehicleId;
        public readonly short X, Y, Z;
        public readonly uint Rotation;
        public readonly byte Health;

        /// <summary>
        /// Dead / burning / in water / airborne, straight off the wire.
        /// </summary>
        /// <remarks>
        /// <b>Added by R5 because check 11's third verb has no message.</b> There is no
        /// <c>S_VEHICLE_BURNING</c> — <c>VehicleStateFlags.Burning</c> is a snapshot field and
        /// the snapshot is the only place a burn can be seen, so a capture that dropped the
        /// flags byte could not answer the question no matter how long the run was. Actors
        /// carried theirs from the start (<see cref="ActorSample.Flags"/>); the vehicle half was
        /// the gap.
        /// </remarks>
        public readonly VehicleStateFlags Flags;

        /// <summary>The server tick this client last received a position for this vehicle on.</summary>
        /// <remarks><see cref="ActorSample.UpdatedAtTick"/> — same rule, and X-35's worked example.</remarks>
        public readonly uint UpdatedAtTick;

        public VehicleSample(in VehicleSnapshotEntry entry, uint updatedAtTick)
        {
            VehicleId = entry.VehicleId;
            X = entry.PosX;
            Y = entry.PosY;
            Z = entry.PosZ;
            Rotation = entry.Rotation;
            Health = entry.Health;
            Flags = entry.Flags;
            UpdatedAtTick = updatedAtTick;
        }
    }

    /// <summary>
    /// One client's decoded world at one server tick — the unit of evidence for every
    /// cross-client agreement question.
    /// </summary>
    public sealed class StateSample
    {
        public uint ServerTick { get; init; }

        /// <summary>Harness wall clock at capture, in milliseconds since the run started.</summary>
        public double AtMs { get; init; }

        public ActorSample[] Actors { get; init; } = Array.Empty<ActorSample>();
        public VehicleSample[] Vehicles { get; init; } = Array.Empty<VehicleSample>();
    }

    /// <summary>
    /// Copies the decoders' current state out on every applied snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Copies, because the decoders are reused in place.</b>
    /// <see cref="DeltaDecoder.Current"/> is one long-lived
    /// <see cref="WorldSnapshot"/> that the next snapshot overwrites — holding the reference
    /// and reading it later reads the present, and every sample would silently be identical.
    /// Same rule the transport states for its pooled receive buffers, one layer up.
    /// </para>
    /// <para>
    /// <b>It reads the shipped decoders and never parses a byte.</b> That is acceptance
    /// criterion 4, and it is why this type takes a <see cref="ClientMessageRouter"/> rather
    /// than a payload.
    /// </para>
    /// </remarks>
    public sealed class StateCapture
    {
        private readonly List<StateSample> _samples = new List<StateSample>();

        public IReadOnlyList<StateSample> Samples => _samples;

        /// <summary>Snapshots whose tick was not newer than the last captured one.</summary>
        /// <remarks>
        /// Non-zero is not automatically a fault — the actor and vehicle streams apply
        /// separately, so a vehicle snapshot at an already-captured tick lands here. It is
        /// reported so a reader can tell a re-capture from a missing one.
        /// </remarks>
        public long DuplicateTickCount { get; private set; }

        private uint _lastTick;
        private bool _hasCaptured;

        public void Capture(ClientMessageRouter router, double atMs)
        {
            if (router == null) throw new ArgumentNullException(nameof(router));

            WorldSnapshot actors = router.Decoder.Current;
            VehicleWorldSnapshot vehicles = router.VehicleDecoder.Current;

            if (_hasCaptured && actors.ServerTick <= _lastTick)
            {
                DuplicateTickCount++;
                return;
            }

            _lastTick = actors.ServerTick;
            _hasCaptured = true;

            // The provenance is read by SLOT, alongside the entry it describes, which is why
            // this loop indexes rather than foreaches. Both come from the shipped decoders; the
            // harness still parses no bytes of its own.
            var actorSamples = new ActorSample[actors.ActorCount];
            for (int i = 0; i < actors.ActorCount; i++)
                actorSamples[i] = new ActorSample(
                    in actors.Actors[i], router.Decoder.PositionUpdatedAt(i));

            var vehicleSamples = new VehicleSample[vehicles.VehicleCount];
            for (int i = 0; i < vehicles.VehicleCount; i++)
                vehicleSamples[i] = new VehicleSample(
                    in vehicles.Vehicles[i], router.VehicleDecoder.PositionUpdatedAt(i));

            _samples.Add(new StateSample
            {
                ServerTick = actors.ServerTick,
                AtMs = atMs,
                Actors = actorSamples,
                Vehicles = vehicleSamples,
            });
        }

        /// <summary>Appends this client's samples to a JSONL sink.</summary>
        /// <remarks>
        /// <para>
        /// <b>Row shape changed with X-35:</b> each actor and vehicle tuple gained a trailing
        /// <c>updatedAtTick</c>, so captures written before 2026-08-27 have one fewer column
        /// and cannot be classified into divergence and staleness at all. A reader must key off
        /// the tuple length rather than assume; an older capture supports the old total only.
        /// </para>
        /// <para>
        /// <b>And again with X-34:</b> the VEHICLE tuple gained a <c>flags</c> column BEFORE
        /// <c>updatedAtTick</c>, so a vehicle row is now 8 wide against the actor row's 7. The
        /// flags column had to go in the middle rather than at the end because
        /// <c>updatedAtTick</c> is the provenance stamp and reads last on both entity types;
        /// splitting that convention to spare one reader an edit would cost every future reader
        /// the rule. Key off the tuple length: 6 is pre-X-35, 7 is X-35, 8 is X-34 onward.
        /// </remarks>
        public void WriteJsonl(System.IO.TextWriter writer, int clientIndex)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            var line = new StringBuilder(1024);
            foreach (StateSample sample in _samples)
            {
                line.Length = 0;
                line.Append(CultureInfo.InvariantCulture,
                    $"{{\"client\":{clientIndex},\"t\":{sample.ServerTick},");
                line.Append(CultureInfo.InvariantCulture,
                    $"\"atMs\":{sample.AtMs.ToString("0.#", CultureInfo.InvariantCulture)},");

                line.Append("\"actors\":[");
                for (int i = 0; i < sample.Actors.Length; i++)
                {
                    ActorSample a = sample.Actors[i];
                    if (i > 0) line.Append(',');
                    line.Append(CultureInfo.InvariantCulture,
                        $"[{a.ActorId},{a.X},{a.Y},{a.Z},{a.Health},{(int)a.Flags},{a.UpdatedAtTick}]");
                }

                line.Append("],\"vehicles\":[");
                for (int i = 0; i < sample.Vehicles.Length; i++)
                {
                    VehicleSample v = sample.Vehicles[i];
                    if (i > 0) line.Append(',');
                    line.Append(CultureInfo.InvariantCulture,
                        $"[{v.VehicleId},{v.X},{v.Y},{v.Z},{v.Rotation},{v.Health},"
                        + $"{(int)v.Flags},{v.UpdatedAtTick}]");
                }

                line.Append("]}");
                writer.WriteLine(line.ToString());
            }
        }
    }
}
