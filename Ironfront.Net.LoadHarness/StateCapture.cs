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

        public ActorSample(in ActorSnapshotEntry entry)
        {
            ActorId = entry.ActorId;
            X = entry.PosX;
            Y = entry.PosY;
            Z = entry.PosZ;
            Health = entry.Health;
            Flags = entry.StateFlags;
        }
    }

    /// <summary>One vehicle, as this client decoded it.</summary>
    public readonly struct VehicleSample
    {
        public readonly ushort VehicleId;
        public readonly short X, Y, Z;
        public readonly uint Rotation;
        public readonly byte Health;

        public VehicleSample(in VehicleSnapshotEntry entry)
        {
            VehicleId = entry.VehicleId;
            X = entry.PosX;
            Y = entry.PosY;
            Z = entry.PosZ;
            Rotation = entry.Rotation;
            Health = entry.Health;
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

            var actorSamples = new ActorSample[actors.ActorCount];
            for (int i = 0; i < actors.ActorCount; i++)
                actorSamples[i] = new ActorSample(in actors.Actors[i]);

            var vehicleSamples = new VehicleSample[vehicles.VehicleCount];
            for (int i = 0; i < vehicles.VehicleCount; i++)
                vehicleSamples[i] = new VehicleSample(in vehicles.Vehicles[i]);

            _samples.Add(new StateSample
            {
                ServerTick = actors.ServerTick,
                AtMs = atMs,
                Actors = actorSamples,
                Vehicles = vehicleSamples,
            });
        }

        /// <summary>Appends this client's samples to a JSONL sink.</summary>
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
                        $"[{a.ActorId},{a.X},{a.Y},{a.Z},{a.Health},{(int)a.Flags}]");
                }

                line.Append("],\"vehicles\":[");
                for (int i = 0; i < sample.Vehicles.Length; i++)
                {
                    VehicleSample v = sample.Vehicles[i];
                    if (i > 0) line.Append(',');
                    line.Append(CultureInfo.InvariantCulture,
                        $"[{v.VehicleId},{v.X},{v.Y},{v.Z},{v.Rotation},{v.Health}]");
                }

                line.Append("]}");
                writer.WriteLine(line.ToString());
            }
        }
    }
}
