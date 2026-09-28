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
    /// the client track checklist.
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
    /// C_SPAWN_REQUEST (0x23). protocol-spec.md § 4.1 lists the message; its body was empty
    /// from the freeze until V8, which gave it this layout. See <see cref="SpawnActorMessage"/>
    /// for why an out-of-band layout does not itself bump <see cref="ProtocolConstants.PROTOCOL_VERSION"/>
    /// — this one does, because the bytes on the wire changed: an empty body decoded by a V8
    /// parser expecting six is <c>MalformedMessages</c>, not a compatible no-op.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One message drives both a first deploy and every later respawn.</b> ledger X-11. A
    /// join no longer places the body (<c>ServerTickLoop.OnClientConnected</c>); the client's
    /// own loadout screen sends this the moment the player deploys, whether that is the first
    /// life of the match or the twentieth. <c>ServerPlayer.AwaitingFirstDeploy</c> is what tells
    /// the two apart server-side — this message's shape does not need to.
    /// </para>
    /// <para>
    /// <b>Weapon slots carry network ids, never <c>WeaponEntry</c> references or names.</b> Same
    /// boundary <c>ILoadoutDirectory</c> and <c>ISpawnPointDirectory</c> are built
    /// around in <c>Ironfront.Net.Unity.Server</c>: <c>WeaponManager.LoadoutSet</c> and
    /// <c>WeaponEntry</c> compile into <c>Assembly-CSharp</c>, unreachable from this project and
    /// from the wire alike. 0 in a slot means "this slot was left empty," not "arm slot 0" —
    /// <c>WeaponManager</c> reserves 0 for exactly that (protocol-spec.md § 4.8).
    /// </para>
    /// <para>
    /// <b><see cref="SpawnPointIndex"/> is the index <c>ISpawnPointDirectory</c> already
    /// exposes</b> — the same one <c>ServerCombatBridge.ChooseSpawnIndex</c> samples from — so
    /// honouring a client's choice costs the server one bounds-and-eligibility check against a
    /// seam that already existed, never a new one. <see cref="NoSpawnPointPreference"/> is the
    /// sentinel a client sends to mean "let the server choose," which is what every sender does
    /// today: the minimap-driven spawn choice offline (<c>MinimapUi.SelectedSpawnPoint</c>) is
    /// not yet wired to populate this field over the network. The field is real and consumed on
    /// the server the moment a sender starts populating it with something other than the
    /// sentinel; until then the server keeps its own random-among-eligible draw exactly as
    /// before.
    /// </para>
    /// </remarks>
    public readonly struct SpawnRequestMessage
    {
        /// <summary>u8 x 5 (loadout slots) + u8 (spawn point index) = 6 bytes.</summary>
        public const int Size = 6;

        /// <summary>Sent in <see cref="SpawnPointIndex"/> for "no preference, let the server choose."</summary>
        public const byte NoSpawnPointPreference = 0xFF;

        /// <summary>Weapon network id for the primary slot. 0 = left empty.</summary>
        public readonly byte Primary;
        /// <summary>Weapon network id for the secondary slot. 0 = left empty.</summary>
        public readonly byte Secondary;
        /// <summary>Weapon network id for the first gear slot. 0 = left empty.</summary>
        public readonly byte Gear1;
        /// <summary>Weapon network id for the second gear slot. 0 = left empty.</summary>
        public readonly byte Gear2;
        /// <summary>Weapon network id for the third gear slot. 0 = left empty.</summary>
        public readonly byte Gear3;
        /// <summary>
        /// The chosen spawn point's index into the server's <c>ISpawnPointDirectory</c>, or
        /// <see cref="NoSpawnPointPreference"/>.
        /// </summary>
        public readonly byte SpawnPointIndex;

        public SpawnRequestMessage(
            byte primary, byte secondary, byte gear1, byte gear2, byte gear3,
            byte spawnPointIndex = NoSpawnPointPreference)
        {
            Primary         = primary;
            Secondary       = secondary;
            Gear1           = gear1;
            Gear2           = gear2;
            Gear3           = gear3;
            SpawnPointIndex = spawnPointIndex;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU8(Primary);
            w.WriteU8(Secondary);
            w.WriteU8(Gear1);
            w.WriteU8(Gear2);
            w.WriteU8(Gear3);
            w.WriteU8(SpawnPointIndex);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out SpawnRequestMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            byte primary   = r.ReadU8();
            byte secondary = r.ReadU8();
            byte gear1     = r.ReadU8();
            byte gear2     = r.ReadU8();
            byte gear3     = r.ReadU8();
            byte spawnIdx  = r.ReadU8();
            if (!r.Ok) return false;

            message = new SpawnRequestMessage(primary, secondary, gear1, gear2, gear3, spawnIdx);
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
