using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// S_SPAWN_ACTOR (0x41). protocol-spec.md section 4.1 lists the message; section 4 never
    /// gives it a byte layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This layout is not in the frozen spec.</b> `S_SPAWN_ACTOR`, `S_DESPAWN_ACTOR` and
    /// `S_EXPLOSION` appear in the msgType table at section 4.1 with a channel and a
    /// one-line description, and then never again — exactly the gap `C_ACK_BASELINE` had in
    /// phase 01. It is handled the same way and for the same reason: defining a layout for a
    /// message the spec declares but does not describe *documents* an unspecified message
    /// rather than *changing* a specified one, so it does not bump
    /// <see cref="ProtocolConstants.PROTOCOL_VERSION"/>. It does still need the section 2
    /// review to become normative — it is flagged here, in the phase-02 report, and in the
    /// Dev A checklist.
    /// </para>
    /// <para>
    /// The fields are the minimum a client needs to instantiate an actor before any snapshot
    /// mentions it: who it is, which side it is on, whether it is a bot, and enough transform
    /// and state to draw the first frame without a pop. Everything else arrives in the next
    /// snapshot, so nothing here is duplicated state that could drift.
    /// </para>
    /// </remarks>
    public readonly struct SpawnActorMessage
    {
        /// <summary>u16 + u8 + u8 + i16 x 3 + u16 + u8 + u8 = 14 bytes.</summary>
        public const int Size = 14;

        public readonly ushort ActorId;
        public readonly byte Team;
        public readonly SpawnFlags Flags;
        /// <summary>Quantized spawn position (<see cref="Quantize.PackPos"/>).</summary>
        public readonly short PosX, PosY, PosZ;
        /// <summary>Quantized spawn yaw (<see cref="Quantize.PackYaw"/>).</summary>
        public readonly ushort Yaw;
        public readonly byte Health;
        public readonly byte WeaponId;

        public SpawnActorMessage(
            ushort actorId, byte team, SpawnFlags flags,
            short posX, short posY, short posZ, ushort yaw, byte health, byte weaponId)
        {
            ActorId  = actorId;
            Team     = team;
            Flags    = flags;
            PosX     = posX;
            PosY     = posY;
            PosZ     = posZ;
            Yaw      = yaw;
            Health   = health;
            WeaponId = weaponId;
        }

        public bool IsBot => (Flags & SpawnFlags.IsBot) != 0;

        /// <summary>True when this actor is the receiving client's own player.</summary>
        public bool IsLocalPlayer => (Flags & SpawnFlags.IsLocalPlayer) != 0;

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(ActorId);
            w.WriteU8(Team);
            w.WriteU8((byte)Flags);
            w.WriteI16(PosX);
            w.WriteI16(PosY);
            w.WriteI16(PosZ);
            w.WriteU16(Yaw);
            w.WriteU8(Health);
            w.WriteU8(WeaponId);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out SpawnActorMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort actorId = r.ReadU16();
            byte team      = r.ReadU8();
            byte flags     = r.ReadU8();
            short x = r.ReadI16(), y = r.ReadI16(), z = r.ReadI16();
            ushort yaw     = r.ReadU16();
            byte health    = r.ReadU8();
            byte weaponId  = r.ReadU8();
            if (!r.Ok) return false;

            message = new SpawnActorMessage(
                actorId, team, (SpawnFlags)flags, x, y, z, yaw, health, weaponId);
            return true;
        }
    }

    /// <summary>
    /// S_DESPAWN_ACTOR (0x42). Layout defined here, not in the spec — see
    /// <see cref="SpawnActorMessage"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="DespawnReason.Culled"/> exists but is never sent by the v1 server:
    /// phase-02 task 1 chose the "never fully cull inside 500 m" interest model precisely so
    /// that leaving a client's interest set is a change of *rate*, not a despawn. The value is
    /// reserved rather than removed because the distinction matters to the client — a culled
    /// actor keeps its id and may return, a destroyed one may not.
    /// </remarks>
    public readonly struct DespawnActorMessage
    {
        /// <summary>u16 + u8 = 3 bytes.</summary>
        public const int Size = 3;

        public readonly ushort ActorId;
        public readonly DespawnReason Reason;

        public DespawnActorMessage(ushort actorId, DespawnReason reason)
        {
            ActorId = actorId;
            Reason  = reason;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(ActorId);
            w.WriteU8((byte)Reason);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out DespawnActorMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort actorId = r.ReadU16();
            byte reason    = r.ReadU8();
            if (!r.Ok) return false;

            message = new DespawnActorMessage(actorId, (DespawnReason)reason);
            return true;
        }
    }

    /// <summary>
    /// S_EXPLOSION (0x4A). Layout defined here, not in the spec — see
    /// <see cref="SpawnActorMessage"/>.
    /// </summary>
    /// <remarks>
    /// Carries the source actor so the killfeed can attribute a grenade kill without a second
    /// message, and a radius so the client can scale the effect and the screen shake instead
    /// of assuming one blast size for every explosive in the game.
    /// </remarks>
    public readonly struct ExplosionMessage
    {
        /// <summary>u16 + i16 x 3 + u8 + u8 = 10 bytes.</summary>
        public const int Size = 10;

        /// <summary>Who caused it. <see cref="DeathMessage.EnvironmentKiller"/> for the world.</summary>
        public readonly ushort SourceActorId;
        /// <summary>Quantized blast centre (<see cref="Quantize.PackPos"/>).</summary>
        public readonly short PosX, PosY, PosZ;
        /// <summary>Blast radius in whole metres. 255 m is far past any weapon in scope.</summary>
        public readonly byte RadiusMetres;
        public readonly ExplosionKind Kind;

        public ExplosionMessage(
            ushort sourceActorId, short posX, short posY, short posZ,
            byte radiusMetres, ExplosionKind kind)
        {
            SourceActorId = sourceActorId;
            PosX          = posX;
            PosY          = posY;
            PosZ          = posZ;
            RadiusMetres  = radiusMetres;
            Kind          = kind;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(SourceActorId);
            w.WriteI16(PosX);
            w.WriteI16(PosY);
            w.WriteI16(PosZ);
            w.WriteU8(RadiusMetres);
            w.WriteU8((byte)Kind);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out ExplosionMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort source = r.ReadU16();
            short x = r.ReadI16(), y = r.ReadI16(), z = r.ReadI16();
            byte radius   = r.ReadU8();
            byte kind     = r.ReadU8();
            if (!r.Ok) return false;

            message = new ExplosionMessage(source, x, y, z, radius, (ExplosionKind)kind);
            return true;
        }
    }
}
