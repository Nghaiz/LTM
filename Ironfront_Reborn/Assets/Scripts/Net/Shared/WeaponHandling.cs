using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>How one carried gun handles in the player's hands: its recoil and its spread.</summary>
    public readonly struct WeaponHandlingProfile
    {
        public WeaponHandlingProfile(
            float kickUpDegrees, float kickSideDegrees, float firstShotScale,
            float recoverySeconds, float recoveryShare, float hipSpreadScale, float movingSpreadScale)
        {
            KickUpDegrees = kickUpDegrees;
            KickSideDegrees = kickSideDegrees;
            FirstShotScale = firstShotScale;
            RecoverySeconds = recoverySeconds;
            RecoveryShare = recoveryShare;
            HipSpreadScale = hipSpreadScale;
            MovingSpreadScale = movingSpreadScale;
        }

        /// <summary>How far one shot climbs the aim, degrees.</summary>
        public float KickUpDegrees { get; }

        /// <summary>The most one shot pushes the aim sideways, degrees, either way at random.</summary>
        public float KickSideDegrees { get; }

        /// <summary>The first shot after a rest kicks this share of the rest: the shooter is braced.</summary>
        public float FirstShotScale { get; }

        /// <summary>How long the hands take to bring the aim back once the trigger rests, seconds.</summary>
        public float RecoverySeconds { get; }

        /// <summary>How much of the climb the hands take back (the rest is the shooter's to pull down).</summary>
        public float RecoveryShare { get; }

        /// <summary>The weapon's spread multiplied this much when fired from the hip.</summary>
        public float HipSpreadScale { get; }

        /// <summary>And this much more while the shooter moves.</summary>
        public float MovingSpreadScale { get; }
    }

    /// <summary>
    /// How the carried guns handle (owner's run of 2026-10-10, phase P38: "the guns are unbalanced;
    /// they should handle like the real guns they are").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Recoil moves the aim, and the round follows the aim.</b> The original game threw the gun
    /// model about and sent the round where the model pointed, so the crosshair and the round
    /// parted (the owner's other complaint, closed in 14.0.6). Since then the round flies where
    /// the player aims, which left a sustained burst no recoil at all. Here the recoil is put
    /// back where it belongs: each shot climbs the aim itself -- the crosshair -- and the hands
    /// bring part of it back once the trigger rests, so a burst walks up the target the way a
    /// real one does and the player pulls it down, and every round still lands under the
    /// crosshair. The aim is the one the server is sent, so it sees the same climb.
    /// </para>
    /// <para>
    /// <b>The numbers follow the real guns.</b> RK-44, an AK on 7.62x39 at about 630 rounds a
    /// minute: a steady climb. SIGNAL DMR, a select-fire 7.62x51 marksman rifle: twice the climb,
    /// so it is tapped rather than sprayed. RECON LRR, a .308 bullpup semi-automatic: a hard
    /// kick per shot. SL-DEFENDER, a .338 bolt action: the heaviest kick, recovered while the bolt
    /// cycles. S-IND7, a 9 mm service pistol: a snappy flip. 76 EAGLE, a 12-gauge pump: a big
    /// shove. Aimed fire is the weapon's own spread; from the hip and on the move it opens up,
    /// never past <see cref="MaxSpreadScale"/>, the share the server's hit judge allows.
    /// </para>
    /// </remarks>
    public static class WeaponHandling
    {
        /// <summary>
        /// The most the spread may be multiplied: <c>ReportedHitJudge.SpreadAllowanceFactor</c>, the
        /// spread the server allows a reported hit (a test pins the two together).
        /// </summary>
        public const float MaxSpreadScale = 3f;

        /// <summary>A shot this long after the last one is the first of a new burst, seconds.</summary>
        public const float BurstGapSeconds = 0.35f;

        /// <summary>The trigger must rest this long before the hands start to recover, seconds.</summary>
        public const float RecoveryDelaySeconds = 0.12f;

        /// <summary>Faster than this, metres a second, the shooter counts as moving.</summary>
        public const float MovingSpeed = 1.5f;

        public static bool TryGet(byte weaponId, out WeaponHandlingProfile profile)
        {
            switch (weaponId)
            {
                case WeaponIds.RK44:
                    profile = new WeaponHandlingProfile(0.45f, 0.2f, 0.7f, 0.35f, 0.6f, 2.5f, 1.2f);
                    return true;
                case WeaponIds.SIGNAL_DMR:
                    profile = new WeaponHandlingProfile(0.85f, 0.28f, 0.8f, 0.4f, 0.6f, 3f, 1.2f);
                    return true;
                case WeaponIds.RECON_LRR:
                    profile = new WeaponHandlingProfile(1.4f, 0.25f, 1f, 0.3f, 0.7f, 3f, 1.2f);
                    return true;
                case WeaponIds.SL_DEFENDER:
                    profile = new WeaponHandlingProfile(3f, 0.4f, 1f, 0.5f, 0.85f, 3f, 1.2f);
                    return true;
                case WeaponIds.SIND7:
                    profile = new WeaponHandlingProfile(1.1f, 0.35f, 1f, 0.25f, 0.7f, 1.6f, 1.2f);
                    return true;
                case WeaponIds.SIND7_SUPPRESSED:
                    profile = new WeaponHandlingProfile(0.9f, 0.3f, 1f, 0.25f, 0.7f, 1.6f, 1.2f);
                    return true;
                case WeaponIds.EAGLE_76:
                    profile = new WeaponHandlingProfile(4f, 0.8f, 1f, 0.45f, 0.8f, 1.25f, 1.1f);
                    return true;
                default:
                    profile = default;
                    return false;
            }
        }

        /// <summary>The spread multiplier for a shot, capped at <see cref="MaxSpreadScale"/>.</summary>
        public static float SpreadScale(in WeaponHandlingProfile profile, bool aiming, bool moving)
        {
            float scale = (aiming ? 1f : profile.HipSpreadScale) * (moving ? profile.MovingSpreadScale : 1f);
            return Mathf.Min(MaxSpreadScale, scale);
        }
    }

    /// <summary>
    /// The aim's recoil over a burst: what each shot adds and what the hands take back. Plain
    /// state, no engine calls, so its arithmetic is tested; the controller applies the deltas.
    /// </summary>
    public sealed class AimRecoilState
    {
        private float _lastShotAt = -10f;
        private float _climbPitch;
        private float _climbYaw;
        private float _recoveredPitch;
        private float _recoveredYaw;

        /// <summary>The climb still on the aim from shots since the last full rest, degrees (pitch up, yaw).</summary>
        public Vector2 Climb => new Vector2(_climbPitch - _recoveredPitch, _climbYaw - _recoveredYaw);

        /// <summary>
        /// One shot: the kick, in degrees, to add to the aim now (x pitch up, y yaw right).
        /// <paramref name="side"/> is a random draw in -1..1 for the sideways part.
        /// </summary>
        public Vector2 Kick(in WeaponHandlingProfile profile, float now, float side)
        {
            bool first = now - _lastShotAt > WeaponHandling.BurstGapSeconds;
            if (first)
            {
                // A new burst: whatever was not recovered is the shooter's now.
                _climbPitch = 0f;
                _climbYaw = 0f;
                _recoveredPitch = 0f;
                _recoveredYaw = 0f;
            }
            _lastShotAt = now;
            float scale = first ? profile.FirstShotScale : 1f;
            var kick = new Vector2(profile.KickUpDegrees * scale, profile.KickSideDegrees * Mathf.Clamp(side, -1f, 1f) * scale);
            _climbPitch += kick.x;
            _climbYaw += kick.y;
            return kick;
        }

        /// <summary>
        /// This frame's recovery, in degrees to take off the aim (x pitch down, y yaw back): once the
        /// trigger has rested, the hands return <see cref="WeaponHandlingProfile.RecoveryShare"/> of
        /// the climb over <see cref="WeaponHandlingProfile.RecoverySeconds"/>.
        /// </summary>
        public Vector2 Recover(in WeaponHandlingProfile profile, float now, float dt)
        {
            if (now - _lastShotAt < WeaponHandling.RecoveryDelaySeconds || profile.RecoverySeconds <= 0f)
            {
                return Vector2.zero;
            }
            float targetPitch = _climbPitch * profile.RecoveryShare;
            float targetYaw = _climbYaw * profile.RecoveryShare;
            float step = dt / profile.RecoverySeconds;
            float pitch = Mathf.MoveTowards(_recoveredPitch, targetPitch, Mathf.Abs(targetPitch) * step) - _recoveredPitch;
            float yaw = Mathf.MoveTowards(_recoveredYaw, targetYaw, Mathf.Abs(targetYaw) * step) - _recoveredYaw;
            _recoveredPitch += pitch;
            _recoveredYaw += yaw;
            return new Vector2(pitch, yaw);
        }

        /// <summary>Forgets the burst: a new life, a new weapon.</summary>
        public void Reset()
        {
            _lastShotAt = -10f;
            _climbPitch = _climbYaw = _recoveredPitch = _recoveredYaw = 0f;
        }
    }
}
