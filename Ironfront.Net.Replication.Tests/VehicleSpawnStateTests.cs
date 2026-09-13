using System;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Protocol 10 § 8.3 — a vehicle is never born burning, a driverless one takes no crash
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
        /// A driverless vehicle takes no crash damage from settling or spawn overlap, for at
        /// least the five seconds § 8.3 requires.
        /// </summary>
        /// <remarks>
        /// The parameters are the four real inputs, so this is the guard executing rather than a
        /// re-statement of it. <c>now</c> at exactly the deadline is included because an
        /// inclusive comparison there is the difference between five seconds and four.
        /// </remarks>
        [Theory]
        [InlineData(0f)]
        [InlineData(2.5f)]
        [InlineData(VehicleSpawnSettle.SettleSeconds - 0.001f)]
        [InlineData(VehicleSpawnSettle.SettleSeconds)]
        [InlineData(VehicleSpawnSettle.SettleSeconds + 60f)]
        public void ADriverlessVehicleTakesNoCrashDamage(float elapsed)
        {
            float spawnedAt = 100f;
            float deadline  = VehicleSpawnSettle.DeadlineFrom(spawnedAt);

            Assert.True(VehicleSpawnSettle.CrashDamageIsSuppressed(
                isServer: true, hasDriver: false, now: spawnedAt + elapsed, notBefore: deadline));
        }

        /// <summary>
        /// Once somebody is driving, the grace is the settle window and no longer.
        /// </summary>
        /// <remarks>
        /// The deadline is re-armed every frame while the seat is empty (<c>Vehicle.FixedUpdate</c>),
        /// so "the first driver enters" and "the deadline was written" are the same moment. That is
        /// what makes this window an exit-from-pad grace rather than one that expired long before
        /// anybody arrived — scene vehicles Awake minutes before the first spawn wave.
        /// </remarks>
        [Fact]
        public void ADrivenVehicleIsProtectedForTheSettleWindowAndThenNoLonger()
        {
            float entered  = 500f;
            float deadline = VehicleSpawnSettle.DeadlineFrom(entered);

            Assert.True(VehicleSpawnSettle.CrashDamageIsSuppressed(
                true, hasDriver: true, now: entered, notBefore: deadline));

            Assert.True(VehicleSpawnSettle.CrashDamageIsSuppressed(
                true, hasDriver: true,
                now: entered + VehicleSpawnSettle.SettleSeconds - 0.001f, notBefore: deadline));

            Assert.False(VehicleSpawnSettle.CrashDamageIsSuppressed(
                true, hasDriver: true,
                now: entered + VehicleSpawnSettle.SettleSeconds, notBefore: deadline));
        }

        /// <summary>
        /// Offline and on a client the guard is absent entirely, which is what keeps single
        /// player byte-for-byte unchanged.
        /// </summary>
        [Fact]
        public void OffTheServerCrashDamageIsNeverSuppressed()
        {
            Assert.False(VehicleSpawnSettle.CrashDamageIsSuppressed(
                isServer: false, hasDriver: false, now: 0f, notBefore: float.MaxValue));
        }

        /// <summary>
        /// <c>Vehicle</c> reads the shared guard rather than carrying its own copy of it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Source-invariant, and the companion to the five tests above: they prove the rule is
        /// right, and this proves the shipped path is the one they proved. Without it
        /// <see cref="VehicleSpawnSettle"/> could be correct, tested, and called by nothing —
        /// which is the state the guard was already in when the handoff asked for it to be
        /// tested.
        /// </para>
        /// <para>
        /// The literal is asserted ABSENT as well as the call present. Three copies of
        /// <c>Time.time + 5f</c> are how the window drifts to four seconds in one of the three
        /// places and nobody notices.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheShippedVehicleUsesTheSharedSettleGuard()
        {
            string source = ReadUnitySource(VehicleSource);

            Assert.Contains(
                "VehicleSpawnSettle.CrashDamageIsSuppressed", source, StringComparison.Ordinal);
            Assert.Contains(
                "VehicleSpawnSettle.DeadlineFrom(Time.time)", source, StringComparison.Ordinal);

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
