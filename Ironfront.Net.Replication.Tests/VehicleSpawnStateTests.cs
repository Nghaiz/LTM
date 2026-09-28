using System;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Protocol 10 § 8.3 — a vehicle is never born burning, a vehicle takes no collision
    /// damage while it settles, and the line that says so is real.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because a comment claiming a guard is not a guard.</b> The settle window
    /// was three copies of <c>5f</c> and one boolean expression written twice inside
    /// <c>Vehicle.cs</c>, which compiles into <c>Assembly-CSharp</c> and so cannot be referenced
    /// by any test assembly (E-11b). The decision moved into
    /// <see cref="VehicleSpawnSettle"/> and these execute it; the source-invariant test at the
    /// bottom is what keeps <c>Vehicle</c> from quietly growing a second copy of the rule.
    /// </para>
    /// <para>
    /// <b>Full health is asserted as <see cref="Quantize.HEALTH_MAX"/>, not as 255.</b> The
    /// handoff document writes the invariant as <c>NormalizedHealth == 255</c>; in this repo the
    /// wire's health <c>u8</c> is a 0..100 ladder, so a full-health vehicle normalizes to 100
    /// and a test pinned to 255 would be red about a build that is correct. The constant is the
    /// thing that has to be right, which is the same argument as § 8.1's "do not scatter 24".
    /// </para>
    /// </remarks>
    public sealed class VehicleSpawnStateTests
    {
        private const string VehicleSource =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs";

        // ------------------------------------------------------- the spawn invariant

        /// <summary>
        /// A freshly spawned vehicle is at full health with no flags, in both the server's float
        /// and the byte the client will actually receive.
        /// </summary>
        [Fact]
        public void ASpawningVehicleIsFullHealthWithNoFlags()
        {
            VehicleState fresh = VehicleState.Spawned(
                vehicleId: 3, spawnerId: 7, VehicleKind.Tank, seatCount: 4,
                maxHealth: 1000f, ownerTeam: 1);

            Assert.Equal(fresh.MaxHealth, fresh.Health);
            Assert.Equal(Quantize.HEALTH_MAX, fresh.NormalizedHealth);
            Assert.False(fresh.Burning);
            Assert.False(fresh.Dead);

            Assert.True(VehicleSpawnStateLog.IsFreshlySpawned(
                fresh.Health, fresh.MaxHealth, VehicleStateFlags.None));
        }

        /// <summary>
        /// The capture that feeds the first full vehicle snapshot carries that invariant through
        /// to the wire, rather than the invariant being true only in the struct.
        /// </summary>
        /// <remarks>
        /// Asserted on the ENTRY rather than on the state, because the entry is what the client
        /// decodes. The two can disagree: normalization is where a scrape below one part in a
        /// hundred disappears, and it is also where a flags byte assembled from the wrong source
        /// would show up.
        /// </remarks>
        [Fact]
        public void TheFirstSnapshotOfAFreshVehicleIsFullHealthWithNoFlags()
        {
            var registry = new VehicleRegistry();
            var snapshot = new VehicleWorldSnapshot();

            Assert.True(registry.Add(
                VehicleState.Spawned(1, 1, VehicleKind.Car, 2, 100f, 0),
                new VehicleCaptureTests.FakePose()));

            registry.CaptureInto(snapshot, serverTick: 1);

            Assert.True(snapshot.TryFind(1, out VehicleSnapshotEntry entry));
            Assert.Equal(Quantize.HEALTH_MAX, entry.Health);
            Assert.Equal(VehicleStateFlags.None, entry.Flags);
        }

        /// <summary>
        /// The invariant check is not satisfied by a vehicle that has already taken damage, and
        /// in particular not by one whose damage rounds away on the wire.
        /// </summary>
        /// <remarks>
        /// The half-point case is the one that matters. A hull one part in a thousand below full
        /// still normalizes to <see cref="Quantize.HEALTH_MAX"/>, so a check written against the
        /// byte alone would call it fresh — and the argument this log exists to settle is
        /// precisely "did the server already take health off it before the client saw it?".
        /// </remarks>
        [Fact]
        public void AVehicleThatHasTakenDamageIsNotFreshEvenWhenTheByteRoundsToFull()
        {
            Assert.Equal(
                Quantize.HEALTH_MAX,
                VehicleSpawnStateLog.Normalize(999.9f, 1000f));

            Assert.False(VehicleSpawnStateLog.IsFreshlySpawned(999.9f, 1000f, VehicleStateFlags.None));
            Assert.False(VehicleSpawnStateLog.IsFreshlySpawned(1000f, 1000f, VehicleStateFlags.Burning));
            Assert.False(VehicleSpawnStateLog.IsFreshlySpawned(1000f, 1000f, VehicleStateFlags.Dead));
        }

        // ------------------------------------------------------------- the log line

        /// <summary>
        /// The line carries every field § 8.3 names, so the handover can be read without the
        /// reader having to go and look anything up.
        /// </summary>
        [Fact]
        public void TheSpawnStateLineCarriesEveryFieldTheHandoffAsksFor()
        {
            string line = VehicleSpawnStateLog.Format(
                vehicleId: 5, spawnerId: 9, VehicleKind.Helicopter,
                health: 1000f, maxHealth: 1000f, VehicleStateFlags.None,
                driverActorId: 0, new Vec3(12.5f, 3f, -40.25f));

            Assert.StartsWith("[vehicle-spawn-state]", line);
            foreach (string field in new[]
            {
                "id=5", "spawner=9", "kind=Helicopter", "health=1000/1000",
                "(100/100)", "flags=None", "driver=none", "position=(12.5, 3, -40.25)",
            })
            {
                Assert.Contains(field, line, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The budget bounds the log on a server that replaces a vehicle every few seconds for
        /// days, and it counts what it dropped rather than discarding it silently.
        /// </summary>
        /// <remarks>
        /// A rate limiter that discards without saying so turns a quiet log into evidence of a
        /// quiet server, which is the same instrument failure the line itself exists to avoid.
        /// </remarks>
        [Fact]
        public void TheSpawnStateLogIsRateLimitedAndReportsWhatItSuppressed()
        {
            var log = new VehicleSpawnStateLog();

            for (int i = 0; i < VehicleSpawnStateLog.MaxLinesPerWindow; i++)
                Assert.True(TryLog(log, now: 0f, out _));

            Assert.False(TryLog(log, now: 0f, out _));
            Assert.False(TryLog(log, now: 1f, out _));
            Assert.Equal(2, log.SuppressedSinceLastLine);

            Assert.True(TryLog(log, VehicleSpawnStateLog.WindowSeconds, out string after));
            Assert.Contains("suppressed=2", after, StringComparison.Ordinal);
            Assert.Equal(0, log.SuppressedSinceLastLine);
        }

        // --------------------------------------------------------- the settle window

        /// <summary>
        /// A vehicle takes no collision damage for the settle window after its deadline was
        /// armed, and full collision damage from the deadline on -- driver or not.
        /// </summary>
        /// <remarks>
        /// <c>now</c> at exactly the deadline is included because an inclusive comparison there is
        /// the difference between five seconds and four. The +60 s row is the one that used to be
        /// suppressed: an empty vehicle a minute after spawning was immune forever.
        /// </remarks>
        [Theory]
        [InlineData(0f, true)]
        [InlineData(2.5f, true)]
        [InlineData(VehicleSpawnSettle.SettleSeconds - 0.001f, true)]
        [InlineData(VehicleSpawnSettle.SettleSeconds, false)]
        [InlineData(VehicleSpawnSettle.SettleSeconds + 60f, false)]
        public void CollisionDamageIsSuppressedOnlyInsideTheSettleWindow(float elapsed, bool suppressed)
        {
            float armedAt  = 100f;
            float deadline = VehicleSpawnSettle.DeadlineFrom(armedAt);

            Assert.Equal(suppressed, VehicleSpawnSettle.CollisionDamageIsSuppressed(
                isServer: true, now: armedAt + elapsed, notBefore: deadline));
        }

        /// <summary>
        /// Offline and on a client the guard is absent entirely, which is what keeps single
        /// player byte-for-byte unchanged.
        /// </summary>
        [Fact]
        public void OffTheServerCollisionDamageIsNeverSuppressed()
        {
            Assert.False(VehicleSpawnSettle.CollisionDamageIsSuppressed(
                isServer: false, now: 0f, notBefore: float.MaxValue));
        }

        /// <summary>
        /// <c>Vehicle</c> applies the shared guard to COLLISIONS ONLY, arms it at spawn and at
        /// driver entry, and never keeps it open while the vehicle is empty.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Source-invariant, and the companion to the tests above: they prove the rule is right,
        /// and this proves the shipped path is the one they proved.
        /// </para>
        /// <para>
        /// <b>Exactly one call, inside <c>OnCollisionEnter</c>.</b> The 2026-09-23 defect was a
        /// second call in <c>Vehicle.Damage</c>, which is the weapon path: with it, every empty
        /// vehicle on a server ignored bullets, rockets and grenades for the whole match. The
        /// FixedUpdate re-arm (<c>!HasDriver()</c> keeping the deadline ahead) is asserted absent
        /// for the same reason: it is what made the window open-ended.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheShippedVehicleSuppressesCollisionsOnlyAndNeverWeaponDamage()
        {
            string source = ReadUnitySource(VehicleSource);
            const string guard = "VehicleSpawnSettle.CollisionDamageIsSuppressed(";

            int first = source.IndexOf(guard, StringComparison.Ordinal);
            Assert.True(first >= 0, "Vehicle.cs no longer applies the settle guard to collisions.");
            Assert.True(source.IndexOf(guard, first + 1, StringComparison.Ordinal) < 0,
                "Vehicle.cs applies the settle guard twice. It belongs in OnCollisionEnter only; "
                + "anywhere else (Damage) makes vehicles immune to weapons.");

            int collision = source.IndexOf("private void OnCollisionEnter(", StringComparison.Ordinal);
            Assert.True(collision >= 0 && first > collision,
                "The settle guard is not inside OnCollisionEnter.");

            Assert.DoesNotContain("CrashDamageIsSuppressed", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IsServer && !HasDriver()", source, StringComparison.Ordinal);

            // Armed at spawn (Awake) and at driver entry, and nowhere else.
            int arms = 0;
            for (int at = 0; (at = source.IndexOf(
                     "networkCrashDamageNotBefore = VehicleSpawnSettle.DeadlineFrom(Time.time)",
                     at, StringComparison.Ordinal)) >= 0; at++) arms++;
            Assert.Equal(2, arms);

            Assert.DoesNotContain(
                "networkCrashDamageNotBefore = Time.time + 5f", source, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Time.time < networkCrashDamageNotBefore", source, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ helpers

        private static bool TryLog(VehicleSpawnStateLog log, float now, out string line)
            => log.TryFormat(
                vehicleId: 1, spawnerId: 1, VehicleKind.Car,
                health: 100f, maxHealth: 100f, VehicleStateFlags.None,
                driverActorId: 0, new Vec3(0f, 0f, 0f), now, out line);

        private static string ReadUnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Ironfront.sln found walking up from {AppContext.BaseDirectory}.");
        }
    }
}
