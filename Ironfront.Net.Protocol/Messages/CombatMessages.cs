using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// S_HIT_CONFIRM (0x43). protocol-spec.md section 4.5.
    /// Drives the client's hitmarker; the authoritative damage is already reflected in
    /// the next snapshot's health field.
    /// </summary>
    public readonly struct HitConfirmMessage
    {
        /// <summary>u16 + u16 + u8 + u8 = 6 bytes.</summary>
        public const int Size = 6;

        public readonly ushort TargetActorId;
        /// <summary>Damage x 10 — fixed point with one decimal place.</summary>
        public readonly ushort DamageFixed;
        public readonly HitboxType HitboxType;
        public readonly HitFlags Flags;

        public HitConfirmMessage(
            ushort targetActorId, ushort damageFixed, HitboxType hitboxType, HitFlags flags)
        {
            TargetActorId = targetActorId;
            DamageFixed   = damageFixed;
            HitboxType    = hitboxType;
            Flags         = flags;
        }

        public float Damage => DamageFixed / 10f;
        public bool Killed => (Flags & HitFlags.Killed) != 0;
        public bool Headshot => (Flags & HitFlags.Headshot) != 0;

        /// <summary>Converts damage to the x10 fixed-point form used on the wire.</summary>
        public static ushort PackDamage(float damage)
        {
            if (damage <= 0f) return 0;
            float scaled = damage * 10f;
            return scaled >= ushort.MaxValue ? ushort.MaxValue : (ushort)(scaled + 0.5f);
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(TargetActorId);
            w.WriteU16(DamageFixed);
            w.WriteU8((byte)HitboxType);
            w.WriteU8((byte)Flags);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out HitConfirmMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort target = r.ReadU16();
            ushort damage = r.ReadU16();
            byte hitbox   = r.ReadU8();
            byte flags    = r.ReadU8();
            if (!r.Ok) return false;

            message = new HitConfirmMessage(target, damage, (HitboxType)hitbox, (HitFlags)flags);
            return true;
        }
    }

    /// <summary>
    /// S_DEATH (0x44). protocol-spec.md section 4.6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On receipt the client enables the ragdoll LOCALLY, plays audio and updates the
    /// killfeed. Corpses are never synchronized between clients (architecture.md AD-4) —
    /// the force vector exists so each client's ragdoll flies in roughly the right
    /// direction without a byte of ongoing replication.
    /// </para>
    /// <para>
    /// <b>The detail tail (1.1, 2026-09-29).</b> Three optional bytes after the twelve: the
    /// weapon, the vehicle and <see cref="DeathDetail"/>, so the killfeed can say HOW (playtest
    /// 2026-09-28, feature 2). Every message is length-framed, and a 1.0 client reads its twelve
    /// bytes and never looks further, so the tail needs no version change;
    /// <see cref="TryParse"/> takes a message with or without it.
    /// </para>
    /// </remarks>
    public readonly struct DeathMessage
    {
        /// <summary>u16 + u16 + u8 + i16 x 3 + u8 = 12 bytes, without the detail tail.</summary>
        public const int Size = 12;

        /// <summary>With the detail tail: u8 weapon + u8 vehicle + u8 detail.</summary>
        public const int SizeWithDetail = Size + 3;

        /// <summary>With the range tail too: u16 metres from the killer to the victim.</summary>
        public const int SizeWithRange = SizeWithDetail + 2;

        /// <summary>Sentinel killer id meaning "killed by the environment".</summary>
        public const ushort EnvironmentKiller = 0xFFFF;

        public readonly ushort VictimActorId;
        public readonly ushort KillerActorId;
        public readonly CauseOfDeath Cause;
        /// <summary>Quantized velocity (see <see cref="Quantize.PackVel"/> scale, as i16).</summary>
        public readonly short ForceX, ForceY, ForceZ;
        public readonly byte HitboxHit;

        /// <summary>The weapon that killed, as a <see cref="WeaponIds"/> id; <c>NONE</c> when none did.</summary>
        public readonly byte WeaponId;

        /// <summary>The vehicle the death involved, as a <see cref="VehicleIds"/> id; <c>NONE</c> when none.</summary>
        public readonly byte VehicleType;

        /// <summary>How the vehicle was involved, and whether it was a melee kill.</summary>
        public readonly DeathDetail Detail;

        /// <summary>Whether the tail was sent. A 1.0 server never sends it.</summary>
        public readonly bool HasDetail;

        /// <summary>
        /// Whole metres from the killer to the victim when the killing blow landed; 0 when the
        /// server did not say (a 1.x server, the world, a suicide) or they stood in one metre.
        /// </summary>
        public readonly ushort DistanceMetres;

        /// <summary>Whether the range tail was sent. It follows the detail tail and needs it.</summary>
        public readonly bool HasRange;

        public DeathMessage(
            ushort victimActorId, ushort killerActorId, CauseOfDeath cause,
            short forceX, short forceY, short forceZ, byte hitboxHit)
            : this(victimActorId, killerActorId, cause, forceX, forceY, forceZ, hitboxHit,
                   WeaponIds.NONE, VehicleIds.NONE, DeathDetail.None, hasDetail: false)
        {
        }

        public DeathMessage(
            ushort victimActorId, ushort killerActorId, CauseOfDeath cause,
            short forceX, short forceY, short forceZ, byte hitboxHit,
            byte weaponId, byte vehicleType, DeathDetail detail)
            : this(victimActorId, killerActorId, cause, forceX, forceY, forceZ, hitboxHit,
                   weaponId, vehicleType, detail, hasDetail: true, distanceMetres: 0, hasRange: false)
        {
        }

        /// <summary>A death with both tails: the detail, then the range.</summary>
        public DeathMessage(
            ushort victimActorId, ushort killerActorId, CauseOfDeath cause,
            short forceX, short forceY, short forceZ, byte hitboxHit,
            byte weaponId, byte vehicleType, DeathDetail detail, ushort distanceMetres)
            : this(victimActorId, killerActorId, cause, forceX, forceY, forceZ, hitboxHit,
                   weaponId, vehicleType, detail, hasDetail: true, distanceMetres, hasRange: true)
        {
        }

        private DeathMessage(
            ushort victimActorId, ushort killerActorId, CauseOfDeath cause,
            short forceX, short forceY, short forceZ, byte hitboxHit,
            byte weaponId, byte vehicleType, DeathDetail detail, bool hasDetail,
            ushort distanceMetres = 0, bool hasRange = false)
        {
            VictimActorId = victimActorId;
            KillerActorId = killerActorId;
            Cause         = cause;
            ForceX        = forceX;
            ForceY        = forceY;
            ForceZ        = forceZ;
            HitboxHit     = hitboxHit;
            WeaponId      = weaponId;
            VehicleType   = vehicleType;
            Detail        = detail;
            HasDetail     = hasDetail;
            DistanceMetres = distanceMetres;
            HasRange      = hasDetail && hasRange;
        }

        public bool KilledByEnvironment => KillerActorId == EnvironmentKiller;

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(VictimActorId);
            w.WriteU16(KillerActorId);
            w.WriteU8((byte)Cause);
            w.WriteI16(ForceX);
            w.WriteI16(ForceY);
            w.WriteI16(ForceZ);
            w.WriteU8(HitboxHit);

            if (HasDetail)
            {
                w.WriteU8(WeaponId);
                w.WriteU8(VehicleType);
                w.WriteU8((byte)Detail);

                if (HasRange) w.WriteU16(DistanceMetres);
            }

            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out DeathMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort victim = r.ReadU16();
            ushort killer = r.ReadU16();
            byte cause    = r.ReadU8();
            short fx = r.ReadI16(), fy = r.ReadI16(), fz = r.ReadI16();
            byte hitbox   = r.ReadU8();
            if (!r.Ok) return false;

            // The tail is optional: a 1.0 server sends twelve bytes. A partial tail is a
            // malformed message, not a shorter one -- reading half of it would name the wrong gun.
            if (r.Remaining == 0)
            {
                message = new DeathMessage(victim, killer, (CauseOfDeath)cause, fx, fy, fz, hitbox);
                return true;
            }

            byte weapon  = r.ReadU8();
            byte vehicle = r.ReadU8();
            byte detail  = r.ReadU8();
            if (!r.Ok) return false;

            if (r.Remaining == 0)
            {
                message = new DeathMessage(
                    victim, killer, (CauseOfDeath)cause, fx, fy, fz, hitbox,
                    weapon, vehicle, (DeathDetail)detail);
                return true;
            }

            // The range tail (2026-09-30). Half of it is malformed for the detail tail's reason;
            // anything after it is a later tail this build does not know, and is left unread the
            // way a 1.0 client leaves the detail tail.
            ushort metres = r.ReadU16();
            if (!r.Ok) return false;

            message = new DeathMessage(
                victim, killer, (CauseOfDeath)cause, fx, fy, fz, hitbox,
                weapon, vehicle, (DeathDetail)detail, metres);
            return true;
        }
    }

    /// <summary>
    /// S_WEAPON_FIRE (0x49). protocol-spec.md section 4.7.
    /// </summary>
    /// <remarks>
    /// Sent unreliable-sequenced on channel 1: losing one gunshot is harmless, since it
    /// only drives muzzle flashes, 3D audio and other players' tracers. Retransmitting it
    /// would put the effect on screen after the moment had passed.
    /// </remarks>
    public readonly struct WeaponFireMessage
    {
        /// <summary>u16 + u8 + i16 x 3 = 9 bytes.</summary>
        public const int Size = 9;

        public readonly ushort ShooterActorId;
        public readonly byte WeaponId;
        /// <summary>Quantized fire direction, for tracers.</summary>
        public readonly short DirX, DirY, DirZ;

        public WeaponFireMessage(
            ushort shooterActorId, byte weaponId, short dirX, short dirY, short dirZ)
        {
            ShooterActorId = shooterActorId;
            WeaponId       = weaponId;
            DirX           = dirX;
            DirY           = dirY;
            DirZ           = dirZ;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(ShooterActorId);
            w.WriteU8(WeaponId);
            w.WriteI16(DirX);
            w.WriteI16(DirY);
            w.WriteI16(DirZ);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out WeaponFireMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort shooter = r.ReadU16();
            byte weaponId  = r.ReadU8();
            short dx = r.ReadI16(), dy = r.ReadI16(), dz = r.ReadI16();
            if (!r.Ok) return false;

            message = new WeaponFireMessage(shooter, weaponId, dx, dy, dz);
            return true;
        }
    }
}
