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

        // Allocated once and reused, like every other buffer on this path: S_PLAYER_LIST is
        // rare, but a router that allocates per message is one that allocates per packet as
        // soon as somebody sends it per tick.
        private readonly byte[] _playerListBody = new byte[PlayerListMessage.MaxBodySize];

        /// <summary>Chat lines refused because nothing survived sanitizing. Phase P6.</summary>
        private long _chatLinesDropped;
        private readonly PlayerListEntry[] _playerListEntries =
            new PlayerListEntry[ProtocolConstants.MAX_ACTORS];

        /// <summary>Buffers snapshots so remote actors can be drawn between them.</summary>
        public SnapshotInterpolator Interpolator { get; } = new SnapshotInterpolator();

        /// <summary>Applies vehicle snapshot deltas. Its <c>Current</c> is the newest vehicle world.</summary>
        public VehicleDeltaDecoder VehicleDecoder { get; } = new VehicleDeltaDecoder();

        /// <summary>
        /// Buffers vehicle snapshots so replicated vehicles can be drawn between them.
        /// </summary>
        /// <remarks>
        /// A second interpolator rather than a second stream through the first: a vehicle's
        /// rotation is a full quaternion and an actor's is a yaw, so the sampled quantity
        /// differs even though the timing discipline is identical. See
        /// <see cref="VehicleSnapshotInterpolator"/> (V5-D1).
        /// </remarks>
        public VehicleSnapshotInterpolator VehicleInterpolator { get; } = new VehicleSnapshotInterpolator();

        /// <summary>Vehicle snapshots applied to the vehicle world state.</summary>
        public long VehicleSnapshotsApplied { get; private set; }

        /// <summary>
        /// Vehicle snapshots dropped because their baseline is a tick this client no longer
        /// holds. See <see cref="UnknownBaselines"/>; the same recovery applies.
        /// </summary>
        public long UnknownVehicleBaselines { get; private set; }

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

        /// <summary>
        /// A vehicle entered this client's interest set. Carries the kind and the network type
        /// id the client needs to bind the id to a scene vehicle.
        /// </summary>
        public event Action<VehicleSpawnMessage>? OnVehicleSpawn;

        /// <summary>
        /// A vehicle left it, or was destroyed.
        /// </summary>
        /// <remarks>
        /// The handler must stop applying snapshots for that id on the frame this arrives
        /// (V4-D12): the server has already stopped sending them, so anything still sampling
        /// the interpolator for it holds a stale pose forever.
        /// </remarks>
        public event Action<VehicleDespawnMessage>? OnVehicleDespawn;

        /// <summary>
        /// Someone entered or left a seat, or was refused.
        /// </summary>
        /// <remarks>
        /// <b>This, and the actor entry's <c>SnapshotField.SeatInfo</c>, are the only things
        /// that decide which vehicle is "mine".</b> A client that concludes locally that it is
        /// driving — because it pressed Use next to a car — keeps predicting a vehicle the
        /// server refused it, and nothing ever tells it otherwise.
        /// </remarks>
        public event Action<SeatChangeMessage>? OnSeatChange;

        /// <summary>
        /// A shot this client fired connected. Drives the hitmarker and its audio.
        /// </summary>
        /// <remarks>
        /// Only ever sent to the shooter — see <c>ServerEventWriter.WriteHitConfirm</c>, which
        /// explains why broadcasting it would be a wallhack served by the server. The damage it
        /// carries is for feedback only; the authoritative health is in the next snapshot.
        /// </remarks>
        public event Action<HitConfirmMessage>? OnHitConfirm;

        /// <summary>
        /// Someone died. Drives the local ragdoll, the death audio and the killfeed.
        /// </summary>
        /// <remarks>
        /// Broadcast, because the killfeed is global. Corpses are never synchronized (AD-4) —
        /// the force vector is here so each client's ragdoll flies roughly the right way
        /// without a byte of ongoing replication.
        /// </remarks>
        public event Action<DeathMessage>? OnDeath;

        /// <summary>
        /// Another actor fired. Drives muzzle flashes, tracers and 3D audio.
        /// </summary>
        /// <remarks>
        /// Unreliable-sequenced on channel 1 and lossy on purpose, so a handler must be
        /// tolerant of gaps: it is a cue to play an effect, never a fact to accumulate.
        /// </remarks>
        public event Action<WeaponFireMessage>? OnWeaponFire;

        /// <summary>Match phase, timer or score changed.</summary>
        public event Action<MatchStateMessage>? OnMatchState;

        /// <summary>A capture point's owner or progress changed.</summary>
        public event Action<CapturePointMessage>? OnCapturePoint;

        /// <summary>An explosion to render. Cosmetic — damage is the server's.</summary>
        public event Action<ExplosionMessage>? OnExplosion;

        /// <summary>
        /// A projectile was launched, or an already-live one was re-parameterized.
        /// </summary>
        /// <remarks>
        /// <b>A repeat of a live id is a correction, not a second projectile</b> (V7-D6, V7-D8).
        /// The handler is expected to route through
        /// <see cref="Projectiles.ClientProjectileTracker"/>, which answers "spawn or re-seat"
        /// once rather than at every subscriber.
        /// </remarks>
        public event Action<ProjectileSpawnMessage>? OnProjectileSpawn;

        /// <summary>
        /// The actor-id-to-name table changed. Raised with the parsed rows and their count.
        /// </summary>
        /// <remarks>
        /// <b>The names point into the receive buffer this call was made from</b>, so a handler
        /// that keeps one past the callback copies it — <see cref="PlayerListMessage.NameOf"/>
        /// is the allocating decode for exactly that moment. Handing out the slices rather than
        /// strings keeps a broadcast of 64 names from allocating 64 strings on every join.
        /// </remarks>
        public event Action<PlayerListEntry[], int>? OnPlayerList;

        /// <summary>
        /// Somebody said something. Carries the speaker's actor id and the decoded line.
        /// Phase P6 task 3.3, ledger X-8.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A string, unlike <see cref="OnPlayerList"/>, and the asymmetry is deliberate.</b>
        /// A player list is up to 64 names on every join and hands out slices so that a broadcast
        /// does not allocate 64 strings; a chat line is one short line a few times a minute whose
        /// only possible consumer is a label. Handing out a slice there would buy nothing and
        /// would hand every subscriber a buffer that is about to become somebody else's packet.
        /// </para>
        /// <para>
        /// <b>Already sanitized</b> — <c>PlayerNameSanitizer</c> ran on it before this was
        /// raised. The client sanitizes what it receives even though the server sanitized what
        /// it forwarded, for the reason that class's own remark gives: the client cannot verify
        /// the game server, so each end cleans at its own ingress.
        /// </para>
        /// <para>
        /// <b>Empty is never raised.</b> A line that sanitizes to nothing is dropped rather than
        /// delivered as a blank row, which would read as a rendering fault.
        /// </para>
        /// </remarks>
        public event Action<byte, string>? OnChat;

        /// <summary>
        /// A snapshot was applied. Carries the server tick and the newest input tick the server
        /// had processed, which is exactly what <see cref="PredictionReconciler.Reconcile"/>
        /// needs.
        /// </summary>
        public event Action<uint, uint>? OnSnapshotApplied;

        /// <summary>
        /// A vehicle snapshot was applied. Carries the server tick it was built at.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="OnSnapshotApplied"/> and carrying no
        /// <c>lastProcessedInputTick</c>, because vehicle prediction has nothing to reconcile
        /// against: it is error-corrected simulation, never input replay, so there is no
        /// acknowledged input tick to replay from (<c>VehicleSnapshotHeader</c> records the same
        /// decision on the wire).
        /// </remarks>
        public event Action<uint>? OnVehicleSnapshotApplied;

        /// <summary>Drops all decoded state. Call on disconnect or when rejoining.</summary>
        public void Reset()
        {
            Decoder.Reset();
            Interpolator.Reset();
            VehicleDecoder.Reset();
            VehicleInterpolator.Reset();
            SnapshotsApplied = 0;
            UnknownBaselines = 0;
            VehicleSnapshotsApplied = 0;
            UnknownVehicleBaselines = 0;
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

                    case ServerMessageType.VehicleSnapshot:
                        if (RouteVehicleSnapshot(body)) handled++;
                        break;

                    case ServerMessageType.VehicleSpawn:
                        if (VehicleSpawnMessage.TryParse(body, out VehicleSpawnMessage vehicleSpawn))
                        {
                            OnVehicleSpawn?.Invoke(vehicleSpawn);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.VehicleDespawn:
                        if (VehicleDespawnMessage.TryParse(body, out VehicleDespawnMessage vehicleDespawn))
                        {
                            OnVehicleDespawn?.Invoke(vehicleDespawn);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.SeatChange:
                        if (SeatChangeMessage.TryParse(body, out SeatChangeMessage seatChange))
                        {
                            OnSeatChange?.Invoke(seatChange);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.HitConfirm:
                        if (HitConfirmMessage.TryParse(body, out HitConfirmMessage hit))
                        {
                            OnHitConfirm?.Invoke(hit);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.Death:
                        if (DeathMessage.TryParse(body, out DeathMessage death))
                        {
                            OnDeath?.Invoke(death);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.WeaponFire:
                        if (WeaponFireMessage.TryParse(body, out WeaponFireMessage weaponFire))
                        {
                            OnWeaponFire?.Invoke(weaponFire);
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

                    case ServerMessageType.ProjectileSpawn:
                        if (ProjectileSpawnMessage.TryParse(
                                body, out ProjectileSpawnMessage projectile))
                        {
                            OnProjectileSpawn?.Invoke(projectile);
                            handled++;
                        }
                        else MalformedMessages++;
                        break;

                    case ServerMessageType.PlayerList:
                        if (RoutePlayerList(body)) handled++;
                        break;

                    case ServerMessageType.Chat:
                        if (RouteChat(body)) handled++;
                        break;

                    default:
                        UnknownMessages++;
                        break;
                }
            }

            return handled;
        }

        /// <summary>Chat lines that parsed but had nothing left after sanitizing.</summary>
        /// <remarks>
        /// Counted rather than logged, because the cause is a hostile or broken sender and a log
        /// line per message is what turns that into a way to fill somebody's console.
        /// </remarks>
        public long ChatLinesDropped => _chatLinesDropped;

        /// <summary>
        /// Parses an <c>S_CHAT</c> body and raises <see cref="OnChat"/> with a decoded line.
        /// </summary>
        /// <remarks>
        /// The decode allocates and that is the right trade here — see <see cref="OnChat"/>. The
        /// sanitize runs at this ingress, on the client's own side of a game server it cannot
        /// verify, and a line with nothing left is dropped rather than raised blank.
        /// </remarks>
        private bool RouteChat(ReadOnlySpan<byte> body)
        {
            if (!ChatTextMessage.TryParseServer(
                    body, out byte actorId, out ReadOnlySpan<byte> textUtf8))
            {
                MalformedMessages++;
                return false;
            }

            string text = PlayerNameSanitizer.Sanitize(
                ChatTextMessage.TextOf(textUtf8), ChatTextMessage.MaxTextCharacters);

            if (text.Length == 0)
            {
                _chatLinesDropped++;

                // Parsed, understood and deliberately not delivered -- so it is handled, not
                // malformed. Counting it as malformed would put a hostile name-shaped line in
                // the same counter as a truncated packet.
                return true;
            }

            OnChat?.Invoke(actorId, text);
            return true;
        }

        /// <summary>
        /// Parses a player list into the reusable row buffer and raises
        /// <see cref="OnPlayerList"/>.
        /// </summary>
        /// <remarks>
        /// The body is copied into <see cref="_playerListBody"/> first. The parsed rows are
        /// slices of the buffer they were read from, and the one this method is handed is a
        /// slice of a transport frame that will be recycled the moment this call returns — so
        /// pointing the rows at it would hand every subscriber a name that is about to become
        /// somebody else's packet.
        /// </remarks>
        private bool RoutePlayerList(ReadOnlySpan<byte> body)
        {
            if (body.Length > _playerListBody.Length)
            {
                MalformedMessages++;
                return false;
            }

            body.CopyTo(_playerListBody);

            if (!PlayerListMessage.TryParse(
                    _playerListBody, 0, body.Length, _playerListEntries, out int count))
            {
                MalformedMessages++;
                return false;
            }

            OnPlayerList?.Invoke(_playerListEntries, count);
            return true;
        }

        /// <summary>
        /// Decodes an <c>S_VEHICLE_SNAPSHOT</c> and buffers the resulting vehicle world.
        /// </summary>
        /// <remarks>
        /// Pushed AFTER the decoder applied it, for the same reason the actor path is: a delta
        /// carries only what changed, so pushing the message would buffer a world containing two
        /// vehicles and nothing else.
        /// </remarks>
        private bool RouteVehicleSnapshot(ReadOnlySpan<byte> body)
        {
            switch (VehicleDecoder.Read(body))
            {
                case SnapshotReadResult.Applied:
                    VehicleSnapshotsApplied++;
                    VehicleInterpolator.Push(VehicleDecoder.Current);
                    OnVehicleSnapshotApplied?.Invoke(VehicleDecoder.Current.ServerTick);
                    return true;

                case SnapshotReadResult.UnknownBaseline:
                    UnknownVehicleBaselines++;
                    return false;

                case SnapshotReadResult.Stale:
                    // Not malformed. An older snapshot than the one already applied is what UDP
                    // reordering looks like, and it is why the decoder checks at all.
                    return false;

                default:
                    MalformedMessages++;
                    return false;
            }
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
