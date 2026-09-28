using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V10 task 2 — the decode-to-intent half of the remote-actor representation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before V10 the client applied exactly two snapshot fields to a remote actor: position and
    /// yaw. Pitch, all eight state flags, health, weapon and team were decoded by
    /// <c>DeltaDecoder</c> and discarded, so remote players never crouched, never aimed, never
    /// ragdolled and always held the same weapon. These tests grade the mapping that ends that;
    /// whether the animator then draws it is client-track item E1.
    /// </para>
        /// <para>
        /// <b>BLIND TESTS: Lines 36, 58, 60 manually construct <c>ActorStateFlags.IsRagdoll</c>.</b>
        /// These prove the DECODER reads the bit correctly; they do NOT prove any producer sets it.
        /// </para>
    /// </remarks>
    public sealed class RemoteActorViewStateTests
    {
        [Fact]
        public void SnapshotFlagsMapToStanceAimAndRagdollIntent()
        {
            RemoteActorVisualState standing = Decode(
                ActorStateFlags.IsAlive | ActorStateFlags.IsAiming);

            Assert.Equal(RemoteActorStance.Standing, standing.Stance);
            Assert.True(standing.IsAiming);
            Assert.True(standing.IsAlive);
            Assert.False(standing.IsRagdoll);

            RemoteActorVisualState crouched = Decode(
                ActorStateFlags.IsAlive | ActorStateFlags.IsCrouching);
            Assert.Equal(RemoteActorStance.Crouching, crouched.Stance);

            RemoteActorVisualState dead = Decode(ActorStateFlags.IsRagdoll);
            Assert.True(dead.IsRagdoll);
            Assert.False(dead.IsAlive);
        }

        [Fact]
        public void ProneOutranksCrouchWhenAnActorClaimsBoth()
        {
            // An actor reporting both is lying; lying down is the more specific claim. Pinned so
            // the collapse is a decision rather than whichever branch happened to be written
            // first.
            RemoteActorVisualState state = Decode(
                ActorStateFlags.IsAlive | ActorStateFlags.IsCrouching | ActorStateFlags.IsProne);

            Assert.Equal(RemoteActorStance.Prone, state.Stance);
        }

        [Fact]
        public void ACorpsePlaysNoCosmetics()
        {
            // A fire event that crosses a death in flight would otherwise flash a muzzle on a
            // ragdoll.
            Assert.False(Decode(ActorStateFlags.IsRagdoll).CanPlayCosmetics);
            Assert.False(Decode(ActorStateFlags.None).CanPlayCosmetics);
            Assert.False(Decode(ActorStateFlags.IsAlive | ActorStateFlags.IsRagdoll).CanPlayCosmetics);
            Assert.True(Decode(ActorStateFlags.IsAlive).CanPlayCosmetics);
        }

        [Fact]
        public void SeatedAndInWaterAreDecodedAndDeliberatelyUnrendered()
        {
            // Pins the RECORDED non-consumption. IsSeated is V5's vehicle work and IsInWater has
            // no cosmetic in the original game — but both are decoded, so "the client discards
            // this field" stops being true of any snapshot field. An unconsumed capability
            // nobody writes down is exactly how six router events came to be dead.
            RemoteActorVisualState state = Decode(
                ActorStateFlags.IsAlive | ActorStateFlags.IsSeated | ActorStateFlags.IsInWater);

            Assert.True(state.IsSeated);
            Assert.True(state.IsInWater);
        }

        [Fact]
        public void HealthWeaponAndTeamSurviveTheDecode()
        {
            var entry = new ActorSnapshotEntry
            {
                ActorId = 21,
                StateFlags = ActorStateFlags.IsAlive,
                Health = 63,
                WeaponId = 4,
                AmmoInClip = 17,
                Team = TeamId.Team1,
            };

            RemoteActorVisualState state = RemoteActorVisualState.From(in entry);

            Assert.Equal(21, state.ActorId);
            Assert.Equal(63, state.Health);      // carried, deliberately not drawn on a remote
            Assert.Equal(4, state.WeaponId);     // selects whose PlayFireCosmetics a shot runs
            Assert.Equal(17, state.AmmoInClip);
            Assert.Equal(TeamId.Team1, state.Team);
        }

        [Fact]
        public void PitchDecodesThroughTheSnapshotsByteForm()
        {
            // The snapshot's pitch slot is an sbyte, not the i16 form. Decoding it through
            // UnpackPitch would put a remote player's aim off by a factor of 128.
            var entry = new ActorSnapshotEntry
            {
                ActorId = 1,
                StateFlags = ActorStateFlags.IsAlive,
                Pitch = Quantize.PackPitchByte(-35f),
            };

            RemoteActorVisualState state = RemoteActorVisualState.From(in entry);

            Assert.Equal(-35f, state.PitchDegrees, 0);
        }

        private static RemoteActorVisualState Decode(ActorStateFlags flags)
        {
            var entry = new ActorSnapshotEntry
            {
                ActorId = 9,
                StateFlags = flags,
            };

            return RemoteActorVisualState.From(in entry);
        }
    }
}
