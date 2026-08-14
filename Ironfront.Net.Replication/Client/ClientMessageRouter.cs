using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Parses one inbound payload batch and dispatches each message to the client's handlers.
    /// The mirror of <c>ServerMessageRouter</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C.
    /// </para>
    /// <para>
    /// <b>It decodes and dispatches; it does not act.</b> Snapshots go into the
    /// <see cref="Decoder"/> and then into the <see cref="Interpolator"/>; everything else is
    /// raised as an event for the Unity layer to turn into a spawned prefab, a killfeed line or
    /// a capture-point bar. Keeping the acting out of here is what lets the whole client
    /// replication path be tested without an engine — which matters more here than on the
    /// server, because the client half is what M1 criterion 7 is graded on and the Editor is the
    /// one machine the team cannot get time on freely.
    /// </para>
    /// <para>
    /// <b>Malformed input is counted, never thrown.</b> This runs on bytes from the network. An
    /// exception per corrupt packet would turn a lossy link into a crash loop, and 5% loss is
    /// the *stated* condition of criterion 7. Every handler is a <c>TryParse</c>, and anything
    /// that fails increments <see cref="MalformedMessages"/> and moves to the next message in
    /// the batch — one bad message does not discard the good ones beside it.
    /// </para>
    /// <para>
    /// <b>Unknown message types are counted, not errors.</b> A newer server may send a type this
    /// build does not know. Skipping it and continuing is what makes the batch forward
    /// compatible; <see cref="UnknownMessages"/> being non-zero is the signal that the two ends
    /// are on different builds.
    /// </para>
    /// </remarks>
    public sealed class ClientMessageRouter
    {
        /// <summary>Applies snapshot deltas. Its <c>Current</c> is the newest world state.</summary>
        public DeltaDecoder Decoder { get; } = new DeltaDecoder();

        /// <summary>Buffers snapshots so remote actors can be drawn between them.</summary>
        public SnapshotInterpolator Interpolator { get; } = new SnapshotInterpolator();

        /// <summary>Snapshots applied to the world state.</summary>
        public long SnapshotsApplied { get; private set; }

        /// <summary>
        /// Snapshots dropped because their baseline is a tick this client no longer holds.
        /// </summary>
        /// <remarks>
        /// Non-zero means the client must ask for a full snapshot: the delta chain is broken and
        /// every later delta will fail the same way until a new baseline arrives. Silently
        /// counting without reacting is why this is surfaced as a property rather than a log
        /// line — the Unity layer decides when to request the resync.
        /// </remarks>
        public long UnknownBaselines { get; private set; }

        /// <summary>Messages that failed to parse.</summary>
        public long MalformedMessages { get; private set; }

        /// <summary>Messages whose type this build does not handle.</summary>
        public long UnknownMessages { get; private set; }

        /// <summary>An actor entered this client's interest set.</summary>
        public event Action<SpawnActorMessage>? OnSpawnActor;

        /// <summary>An actor left it, or died out of view.</summary>
        public event Action<DespawnActorMessage>? OnDespawnActor;

        /// <summary>Match phase, timer or score changed.</summary>
        public event Action<MatchStateMessage>? OnMatchState;

        /// <summary>A capture point's owner or progress changed.</summary>
        public event Action<CapturePointMessage>? OnCapturePoint;

        /// <summary>An explosion to render. Cosmetic — damage is the server's.</summary>
        public event Action<ExplosionMessage>? OnExplosion;

        /// <summary>
        /// A snapshot was applied. Carries the server tick and the newest input tick the server
        /// had processed, which is exactly what <see cref="PredictionReconciler.Reconcile"/>
        /// needs.
        /// </summary>
        public event Action<uint, uint>? OnSnapshotApplied;

        /// <summary>Drops all decoded state. Call on disconnect or when rejoining.</summary>
        public void Reset()
        {
            Decoder.Reset();
            Interpolator.Reset();
            SnapshotsApplied = 0;
            UnknownBaselines = 0;
            MalformedMessages = 0;
            UnknownMessages = 0;
        }

        /// <summary>Routes every message in one payload batch.</summary>
        /// <returns>How many messages were understood and applied.</returns>
        public int Route(ReadOnlySpan<byte> payload)
        {
            var reader = new PayloadFrameReader(payload);
            if (!reader.IsValid)
            {
                MalformedMessages++;
                return 0;
            }

            int handled = 0;

            while (reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> body))
            {
                switch ((ServerMessageType)msgType)
                {
                    case ServerMessageType.Snapshot:
                        if (RouteSnapshot(body)) handled++;
                        break;

                    case ServerMessageType.SpawnActor:
                        if (SpawnActorMessage.TryParse(body, out SpawnActorMessage spawn))
                        {
                            OnSpawnActor?.Invoke(spawn);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.DespawnActor:
                        if (DespawnActorMessage.TryParse(body, out DespawnActorMessage despawn))
                        {
                            OnDespawnActor?.Invoke(despawn);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.MatchState:
                        if (MatchStateMessage.TryParse(body, out MatchStateMessage match))
                        {
                            OnMatchState?.Invoke(match);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.CapturePoint:
                        if (CapturePointMessage.TryParse(body, out CapturePointMessage point))
                        {
                            OnCapturePoint?.Invoke(point);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.Explosion:
                        if (ExplosionMessage.TryParse(body, out ExplosionMessage explosion))
                        {
                            OnExplosion?.Invoke(explosion);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    default:
                        UnknownMessages++;
                        break;
                }
            }

            return handled;
        }

        private bool RouteSnapshot(ReadOnlySpan<byte> body)
        {
            switch (Decoder.Read(body))
            {
                case SnapshotReadResult.Applied:
                    SnapshotsApplied++;

                    // Pushed AFTER the decoder has applied it, because DeltaDecoder.Current is
                    // the accumulated world, not this message's contents -- a delta carries only
                    // what changed. Pushing the message would buffer a world with a handful of
                    // actors in it and nothing else.
                    Interpolator.Push(Decoder.Current);
                    OnSnapshotApplied?.Invoke(Decoder.Current.ServerTick, Decoder.LastProcessedInputTick);
                    return true;

                case SnapshotReadResult.UnknownBaseline:
                    UnknownBaselines++;
                    return false;

                case SnapshotReadResult.Stale:
                    // Not malformed. A snapshot older than the one already applied is what
                    // UDP reordering looks like, and it is the reason the decoder checks at all.
                    return false;

                default:
                    MalformedMessages++;
                    return false;
            }
        }
    }
}
