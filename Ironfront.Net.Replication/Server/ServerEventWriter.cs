using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Frames gameplay events into payloads. phase-02 task 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The companion to <see cref="ServerPayloadWriter"/>, and engine-free for the same
    /// reason: the Unity tick loop cannot be reached from CI, so the framing it performs is
    /// written here where it can be. Which clients receive an event is the caller's decision —
    /// this class only turns an event into bytes on the right channel.
    /// </para>
    /// <para>
    /// <b>The channel per event type is not a free choice.</b> phase-02 task 6 and
    /// architecture.md AD-7 assign each one, and the assignment follows from what the event
    /// is. Spawn, despawn, death, hit confirmation and explosions are facts — missing one
    /// leaves the client permanently wrong, so they go reliable-ordered on channel 2. A
    /// gunshot is a cue for a muzzle flash and a sound; retransmitting it would put the effect
    /// on screen after the moment had passed, so it goes unreliable-sequenced on channel 1.
    /// </para>
    /// </remarks>
    public static class ServerEventWriter
    {
        /// <summary>Channel for events that must not be lost.</summary>
        public const ChannelId ReliableChannel = ChannelId.ReliableOrdered;

        /// <summary>Channel for cosmetic, time-sensitive cues.</summary>
        public const ChannelId CosmeticChannel = ChannelId.SnapshotSequenced;

        /// <summary>Metres beyond which a gunshot is not worth sending. phase-02 task 6.</summary>
        public const float WeaponFireAudibleRadius = 100f;

        /// <summary>Metres beyond which an explosion is not worth sending.</summary>
        public const float ExplosionAudibleRadius = 200f;

        /// <summary>Writes S_SPAWN_ACTOR as a channel-2 payload.</summary>
        /// <returns>Bytes written, or -1 when the destination was too small.</returns>
        public static int WriteSpawn(Span<byte> destination, in SpawnActorMessage message)
        {
            Span<byte> body = stackalloc byte[SpawnActorMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.SpawnActor, body);
        }

        /// <summary>Writes S_DESPAWN_ACTOR as a channel-2 payload.</summary>
        public static int WriteDespawn(Span<byte> destination, in DespawnActorMessage message)
        {
            Span<byte> body = stackalloc byte[DespawnActorMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.DespawnActor, body);
        }

        /// <summary>Writes S_DEATH as a channel-2 payload. Broadcast — the killfeed is global.</summary>
        public static int WriteDeath(Span<byte> destination, in DeathMessage message)
        {
            Span<byte> body = stackalloc byte[DeathMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.Death, body);
        }

        /// <summary>
        /// Writes S_HIT_CONFIRM as a channel-2 payload.
        /// </summary>
        /// <remarks>
        /// Sent to the shooter and nobody else. Broadcasting it would tell every client
        /// exactly when and how hard everyone else was being hit, which is a wallhack served
        /// by the server.
        /// </remarks>
        public static int WriteHitConfirm(Span<byte> destination, in HitConfirmMessage message)
        {
            Span<byte> body = stackalloc byte[HitConfirmMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.HitConfirm, body);
        }

        /// <summary>Writes S_WEAPON_FIRE as a channel-1 payload. Lossy on purpose.</summary>
        public static int WriteWeaponFire(Span<byte> destination, in WeaponFireMessage message)
        {
            Span<byte> body = stackalloc byte[WeaponFireMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, CosmeticChannel, ServerMessageType.WeaponFire, body);
        }

        /// <summary>Writes S_EXPLOSION as a channel-2 payload.</summary>
        /// <remarks>
        /// Reliable while a gunshot is not, because an explosion also carries damage the client
        /// has to account for and a blast the camera has to react to. A missed muzzle flash is
        /// invisible; a missed explosion is a player dying to nothing.
        /// </remarks>
        public static int WriteExplosion(Span<byte> destination, in ExplosionMessage message)
        {
            Span<byte> body = stackalloc byte[ExplosionMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.Explosion, body);
        }

        /// <summary>
        /// Writes S_MATCH_STATE as a channel-2 payload. Broadcast — every client draws the
        /// same scoreboard. Phase-03 task 1.
        /// </summary>
        /// <remarks>
        /// Reliable, unlike a gunshot, because it is the only thing that tells a client the
        /// round has ended. A dropped one leaves that client playing a match everyone else has
        /// finished, and the next thing it sees is the world resetting under it.
        /// </remarks>
        public static int WriteMatchState(Span<byte> destination, in MatchStateMessage message)
        {
            Span<byte> body = stackalloc byte[MatchStateMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.MatchState, body);
        }

        /// <summary>
        /// Writes S_CAPTURE_POINT as a channel-2 payload. Phase-03 task 2.
        /// </summary>
        /// <remarks>
        /// <b>Trap 3 is not solved here.</b> This class turns one capture point into bytes; how
        /// often it is asked to is <c>CapturePointState.Tick</c>'s answer, and that is where the
        /// send threshold lives. Sending every tick would be 5 points x 30 Hz x 16 clients =
        /// 2400 messages a second, and no amount of efficient framing would make that acceptable.
        /// </remarks>
        public static int WriteCapturePoint(Span<byte> destination, in CapturePointMessage message)
        {
            Span<byte> body = stackalloc byte[CapturePointMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.CapturePoint, body);
        }

        /// <summary>
        /// Writes S_PLAYER_LIST as a channel-2 payload. Broadcast, on join and on change.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The opcode was declared at the freeze and had no writer for four phases, which is why
        /// the killfeed rendered an actor id and no name. Reliable, because a client that misses
        /// it has no second chance to learn who anybody is — nothing re-sends names on a timer.
        /// </para>
        /// <para>
        /// <paramref name="bodyScratch"/> is the caller's, not a <c>stackalloc</c>: the body is
        /// variable-length and its worst case is
        /// <see cref="PlayerListMessage.MaxBodySize"/> (1153 B), which is not a size to put on
        /// the stack of a 30 Hz tick loop. Size it once and reuse it.
        /// </para>
        /// </remarks>
        public static int WritePlayerList(
            Span<byte> destination,
            Span<byte> bodyScratch,
            ReadOnlySpan<PlayerListEntry> entries)
        {
            int bodyLength = PlayerListMessage.Write(bodyScratch, entries);
            return bodyLength < 0
                ? -1
                : Frame(
                    destination, ReliableChannel, ServerMessageType.PlayerList,
                    bodyScratch.Slice(0, bodyLength));
        }

        /// <summary>
        /// Writes S_CHAT as a channel-2 payload. Broadcast. Phase P6 task 3.3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Reliable, and broadcast to everyone.</b> Chat is a fact, not a cue: a line
        /// delivered to eleven of twelve players is a conversation one person is silently
        /// excluded from, with nothing to re-send it and no way for them to know they missed
        /// anything. There is no earshot filter either — lobby chat is global by definition, and
        /// the proximity question is a different feature with a different opcode.
        /// </para>
        /// <para>
        /// <paramref name="bodyScratch"/> is the caller's rather than a <c>stackalloc</c>, for
        /// <see cref="WritePlayerList"/>'s reason: the body is variable-length. It is far
        /// smaller here (<see cref="ChatTextMessage.MaxServerBodySize"/>, 122 B), so the stack would
        /// survive it — the caller supplies it anyway so that both variable-length writers on
        /// this class read the same way, and so the buffer is sized once at construction rather
        /// than per message.
        /// </para>
        /// <para>
        /// <b>The text is not sanitized here, and it must already have been.</b> This class
        /// turns an event into bytes; deciding what a label may render is
        /// <c>PlayerNameSanitizer</c>'s job and the server does it at ingress, where the bytes
        /// first cross the socket. Sanitizing again here would leave two places to keep one
        /// security rule in step.
        /// </para>
        /// </remarks>
        public static int WriteChat(
            Span<byte> destination, Span<byte> bodyScratch, byte actorId,
            ReadOnlySpan<byte> textUtf8)
        {
            int bodyLength = ChatTextMessage.WriteServer(bodyScratch, actorId, textUtf8);
            return bodyLength < 0
                ? -1
                : Frame(
                    destination, ReliableChannel, ServerMessageType.Chat,
                    bodyScratch.Slice(0, bodyLength));
        }

        /// <summary>
        /// Writes S_VEHICLE_SPAWN as a channel-2 payload. Broadcast. Phase-V8 task 6.
        /// </summary>
        /// <remarks>
        /// Reliable and unfiltered, unlike an explosion. A vehicle spawn is not a cue — it is
        /// what creates the object every later <c>S_VEHICLE_SNAPSHOT</c> entry addresses, so a
        /// client that misses it has nothing to apply those entries to and no second chance to
        /// learn the vehicle exists. Earshot filtering would make that permanent for anyone who
        /// happened to be across the map when a jeep respawned.
        /// </remarks>
        public static int WriteVehicleSpawn(Span<byte> destination, in VehicleSpawnMessage message)
        {
            Span<byte> body = stackalloc byte[VehicleSpawnMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.VehicleSpawn, body);
        }

        /// <summary>Writes S_VEHICLE_DESPAWN as a channel-2 payload. Phase-V8 task 6.</summary>
        /// <remarks>
        /// Reliable for the mirror of the reason above: a missed despawn leaves a wreck standing
        /// on a client forever, and no snapshot removes it — the vehicle simply stops being
        /// mentioned, which is indistinguishable from one that has not moved.
        /// </remarks>
        public static int WriteVehicleDespawn(
            Span<byte> destination, in VehicleDespawnMessage message)
        {
            Span<byte> body = stackalloc byte[VehicleDespawnMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.VehicleDespawn, body);
        }

        /// <summary>Writes S_SEAT_CHANGE as a channel-2 payload. V4 task 4.</summary>
        /// <remarks>
        /// <para>
        /// <b>Reliable, and it has to be.</b> Leaving a seat is the one edge-triggered vehicle
        /// action (protocol-spec.md § 4.10) — a dropped answer strands the player welded into a
        /// vehicle with no second chance to ask, because the request that would have asked again
        /// was already consumed.
        /// </para>
        /// <para>
        /// <b>Who receives it is the decision's, not this method's.</b> An accept is broadcast —
        /// everyone must see who is driving — and a refusal is addressed to the requester alone
        /// (V4-D7). <see cref="Vehicles.SeatDecision.Broadcast"/> carries that so it is decided
        /// once rather than re-derived at every send site.
        /// </para>
        /// <para>
        /// <b>The transition, not the state.</b> Occupancy that clients render comes from
        /// <c>SnapshotField.SeatInfo</c> on the <b>actor</b> entry, which V3 finished. This
        /// message is the change and the refusal; there is deliberately one source of truth for
        /// "who is in what seat", and it is the actor entry.
        /// </para>
        /// </remarks>
        public static int WriteSeatChange(Span<byte> destination, in SeatChangeMessage message)
        {
            Span<byte> body = stackalloc byte[SeatChangeMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.SeatChange, body);
        }

        /// <summary>Writes S_PROJECTILE_SPAWN as a channel-2 payload. Phase-V7 task 3.</summary>
        /// <remarks>
        /// <para>
        /// <b>Reliable, and unusually load-bearing for a per-shot event.</b> A dropped launch is
        /// not a missing tracer — it is a projectile that exists on the server, damages someone,
        /// and was never visible to the person it killed. It also carries the re-announces of
        /// V7-D6 and V7-D8, where reliable ORDERING is what makes id reuse safe:
        /// <see cref="Projectiles.ProjectileIdPool"/> runs without a quarantine precisely because
        /// nothing on this channel can arrive out of order.
        /// </para>
        /// <para>
        /// <b>Not earshot-filtered here, and the fallback ladder knows it.</b> V7 section 5 lists
        /// a visible/audible-radius filter as the third bandwidth fallback, after halving the
        /// guided and deployable re-announce rates. It is not applied by default because a
        /// projectile is a thing you can watch cross the whole map, unlike the fire report
        /// <see cref="WeaponFireAudibleRadius"/> governs, and cutting it at a radius makes long
        /// shots arrive from nowhere.
        /// </para>
        /// </remarks>
        public static int WriteProjectileSpawn(
            Span<byte> destination, in ProjectileSpawnMessage message)
        {
            Span<byte> body = stackalloc byte[ProjectileSpawnMessage.Size];
            return message.Write(body) < 0
                ? -1
                : Frame(destination, ReliableChannel, ServerMessageType.ProjectileSpawn, body);
        }

        /// <summary>
        /// Whether a listener at <paramref name="listenerDistanceSquared"/> should receive an
        /// event audible within <paramref name="radius"/> metres.
        /// </summary>
        /// <remarks>
        /// Takes the SQUARED distance, because that is what the caller has: the broadcast loop
        /// runs per (event, client) and computing a square root per pair to compare against a
        /// constant is work with no answer attached. Passing a linear distance here reports
        /// almost everything as in earshot — 150 m against a 200 m radius compares 150 to
        /// 40,000 — so the parameter is named for its units rather than left to a comment.
        /// </remarks>
        public static bool IsWithinEarshotSquared(float listenerDistanceSquared, float radius)
            => listenerDistanceSquared <= radius * radius;

        private static int Frame(
            Span<byte> destination, ChannelId channel, ServerMessageType type,
            ReadOnlySpan<byte> body)
        {
            var writer = new PayloadFrameWriter(destination, channel);
            if (!writer.WriteMessage(type, body)) return -1;
            return writer.TryFinish(out int total) ? total : -1;
        }
    }
}
