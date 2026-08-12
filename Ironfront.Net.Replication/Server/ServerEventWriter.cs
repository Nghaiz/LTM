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
        /// Whether a client at <paramref name="listenerDistance"/> metres should receive an
        /// event audible within <paramref name="radius"/>.
        /// </summary>
        /// <remarks>
        /// Compared on squared distance by the caller where it matters; this overload exists so
        /// the radius policy has one definition rather than a literal at each call site.
        /// </remarks>
        public static bool IsWithinEarshot(float listenerDistance, float radius)
            => listenerDistance <= radius;

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
