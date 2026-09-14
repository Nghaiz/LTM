using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Vehicle health survives the trip to the wire and back. X-85.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this exists to stop.</b> The encode side
    /// (<see cref="VehicleState.NormalizedHealth"/>) scales against
    /// <see cref="Quantize.HEALTH_MAX"/>; the decode side (<see cref="VehiclePose.FromEntry"/>)
    /// wrote the literal <c>255f</c> instead. A full-health vehicle therefore arrived on every
    /// client as <c>100/255 = 0.392</c>, <c>NetClientVehicle</c> fed that into
    /// <c>Vehicle.SetHealthAuthoritative</c>, and <c>ApplyHealth</c>'s
    /// <c>health &lt; 0.5 * maxHealth</c> rung played the damage smoke on frame one for every
    /// vehicle in the match. It never cleared, because each later snapshot carried the same
    /// wrong fraction.
    /// </para>
    /// <para>
    /// <b>Both halves are asserted, not just the round trip.</b> A round-trip assertion alone
    /// passes if someone "fixes" a future mismatch by changing the ENCODE side to 255 as well:
    /// the pair would agree and every other reader of the wire byte —
    /// <see cref="VehicleSpawnStateLog"/>, which reports <c>n/100</c> — would silently start
    /// lying. So the wire byte's own value is pinned against the constant too.
    /// </para>
    /// <para>
    /// <b>It is written to be capable of going red.</b> Restore <c>255f</c> in
    /// <c>VehiclePose.FromEntry</c> and <see cref="FullHealth_SurvivesTheRoundTrip"/> fails at
    /// 0.392 — that mutation was run before this file was committed.
    /// </para>
    /// </remarks>
    public sealed class VehicleHealthRoundTripTests
    {
        private const float MaxHealth = 400f;

        private static VehiclePose Decode(float health)
        {
            VehicleState state = VehicleState.Spawned(
                vehicleId: 1, spawnerId: 1, kind: VehicleKind.Tank,
                seatCount: 2, maxHealth: MaxHealth, ownerTeam: 0);
            state.Health = health;

            return VehiclePose.FromEntry(new VehicleSnapshotEntry
            {
                VehicleId  = 1,
                ChangeMask = VehicleField.Full,
                Health     = state.NormalizedHealth,
            });
        }

        [Fact]
        public void FullHealth_SurvivesTheRoundTrip()
        {
            Assert.Equal(1f, Decode(MaxHealth).Health, 3);
        }

        /// <summary>
        /// The rung that actually produced the smoke: a healthy vehicle must decode well clear
        /// of the half-health threshold <c>Vehicle.ApplyHealth</c> starts the particles at.
        /// </summary>
        [Fact]
        public void FullHealth_DecodesAboveTheDamageParticleThreshold()
        {
            Assert.True(
                Decode(MaxHealth).Health > 0.5f,
                "A full-health vehicle decoded at or under half health, so every client will "
                + "play its damage smoke from the first snapshot. Check the divisor in "
                + "VehiclePose.FromEntry against Quantize.HEALTH_MAX -- and do NOT lower "
                + "maxHealth to make the particles stop (protocol 10 section 16).");
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.25f, 0.25f)]
        [InlineData(0.5f, 0.5f)]
        [InlineData(1f, 1f)]
        public void EveryFraction_SurvivesTheRoundTrip(float fraction, float expected)
        {
            Assert.Equal(expected, Decode(MaxHealth * fraction).Health, 2);
        }

        /// <summary>
        /// The wire byte itself, so a future "fix" that moves BOTH sides onto 255 cannot pass.
        /// </summary>
        [Fact]
        public void FullHealth_OccupiesTheProtocolsOwnCeiling()
        {
            VehicleState state = VehicleState.Spawned(
                vehicleId: 1, spawnerId: 1, kind: VehicleKind.Tank,
                seatCount: 2, maxHealth: MaxHealth, ownerTeam: 0);

            Assert.Equal(Quantize.HEALTH_MAX, state.NormalizedHealth);
        }
    }
}
