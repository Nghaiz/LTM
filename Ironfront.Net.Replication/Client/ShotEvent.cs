using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// One shot somebody else fired, as the cosmetic layer needs it: who, with what, which way.
    /// phase-V10 task 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No state and nothing accumulated.</b> Weapon fire is the one event on the cosmetic
    /// channel — unreliable-sequenced, documented safe to drop. Any counter driven from it
    /// (a muzzle index, an ammo tally, a shot number) desynchronises permanently on the first
    /// dropped packet and does not reproduce on a clean network, which is the worst pair of
    /// properties a bug can have. So this is a value, the presenter plays it, and nothing
    /// remembers it (V10 D9).
    /// </para>
    /// <para>
    /// <b>Direction unpacks through <see cref="Quantize.UnpackVel16"/></b> — same wide form as
    /// <see cref="DeathImpulse"/>, and for the same reason. <c>PackVel16</c>'s doc names
    /// <c>S_WEAPON_FIRE</c> explicitly.
    /// </para>
    /// </remarks>
    public readonly struct ShotEvent
    {
        public readonly ushort ShooterActorId;
        public readonly byte WeaponId;

        /// <summary>Fire direction, already unpacked. Not normalised — the sender's vector.</summary>
        public readonly Vec3 Direction;

        public ShotEvent(ushort shooterActorId, byte weaponId, Vec3 direction)
        {
            ShooterActorId = shooterActorId;
            WeaponId       = weaponId;
            Direction      = direction;
        }

        /// <summary>Builds one from the wire message.</summary>
        public static ShotEvent From(in WeaponFireMessage message)
            => new ShotEvent(
                message.ShooterActorId,
                message.WeaponId,
                new Vec3(
                    Quantize.UnpackVel16(message.DirX),
                    Quantize.UnpackVel16(message.DirY),
                    Quantize.UnpackVel16(message.DirZ)));
    }
}
