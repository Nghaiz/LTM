using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.World;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// <see cref="PlayVolume.IsBelowFloor"/> — the fall-through-collision case, distinct from
    /// crossing a wall. Ledger <b>X-75</b>.
    /// </summary>
    public sealed class PlayVolumeFloorTests
    {
        /// <summary>The wire's own cube: the range every replicated position must fit inside.</summary>
        private static PlayVolume Wire()
        {
            float centre = (Quantize.POS_MIN + Quantize.POS_MAX) / 2f;
            return new PlayVolume(
                new Vec3(centre, centre, centre),
                new Vec3(Quantize.POS_RANGE, Quantize.POS_RANGE, Quantize.POS_RANGE));
        }

        [Fact]
        public void APointTwoHundredMetresBelowMinYIsBelowTheFloor()
        {
            PlayVolume volume = Wire();
            var point = new Vec3(0f, volume.Min.Y - 200f, 0f);

            Assert.True(volume.IsBelowFloor(in point, slackMetres: 0f));
        }

        [Fact]
        public void APointFiveMetresAboveMinYIsNotBelowTheFloor()
        {
            PlayVolume volume = Wire();
            var point = new Vec3(0f, volume.Min.Y + 5f, 0f);

            Assert.False(volume.IsBelowFloor(in point, slackMetres: 0f));
        }

        /// <summary>
        /// Outside the box on X, but above the floor — a wall crossing, not a fall-through. The
        /// method is Y-only and must not treat any other axis as a floor.
        /// </summary>
        [Fact]
        public void APointOutsideAWallButAboveTheFloorIsNotBelowTheFloor()
        {
            PlayVolume volume = Wire();
            var point = new Vec3(volume.Max.X + 500f, volume.Min.Y + 5f, 0f);

            Assert.False(volume.IsBelowFloor(in point, slackMetres: 0f));
        }

        /// <summary>Exactly on the floor, with zero slack, is not below it — the check is strict.</summary>
        [Fact]
        public void APointExactlyOnMinYIsNotBelowTheFloorAtZeroSlack()
        {
            PlayVolume volume = Wire();
            Vec3 onTheFloor = volume.Min;

            Assert.False(volume.IsBelowFloor(in onTheFloor, slackMetres: 0f));
        }

        /// <summary>Slack pushes the trigger point further down, exactly as documented.</summary>
        [Fact]
        public void SlackMovesTheTriggerPointDownByExactlyItself()
        {
            PlayVolume volume = Wire();
            var justBelowFloor = new Vec3(0f, volume.Min.Y - 1f, 0f);

            // One metre below the floor is "below" with no slack...
            Assert.True(volume.IsBelowFloor(in justBelowFloor, slackMetres: 0f));

            // ...but not once five metres of slack is given.
            Assert.False(volume.IsBelowFloor(in justBelowFloor, slackMetres: 5f));
        }

        /// <summary>
        /// The caller must use zero slack, and this says why in a place that fails.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>ServerPlayer.EnforceWireVolume</c> tests this BEFORE it clamps, so a body below
        /// the floor but inside the slack is pushed back up to the floor instead of dying.
        /// Gravity then moves it down by one tick's fall and the cycle repeats: alive, pinned to
        /// the boundary, forever — which is the X-75 symptom this whole path exists to end.
        /// </para>
        /// <para>
        /// The recorded descent is ~0.517 m per tick, so ANY slack at or above that swallows the
        /// fall whole. All four recorded occurrences landed between -1024.03 and -1025.07.
        /// </para>
        /// <para>
        /// If this fails because the constant moved, do not adjust the number here — the
        /// oscillation is not a tuning question.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheServersSlackIsZeroSoASingleTicksFallIsNotSwallowed()
        {
            const float recordedFallPerTickMetres = 0.517f;

            string source = ReadServerPlayerSource();
            System.Text.RegularExpressions.Match m = Regex.Match(
                source, @"FloorDeathSlackMetres\s*=\s*(-?\d+(?:\.\d+)?)f?\s*;");

            Assert.True(m.Success, "no FloorDeathSlackMetres constant in ServerPlayer.cs");

            float slack = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            Assert.True(
                slack < recordedFallPerTickMetres,
                $"ServerPlayer uses {slack} m of floor slack, but a falling body descends about "
                + $"{recordedFallPerTickMetres} m per tick and is clamped back to the floor every "
                + "tick it is not judged fallen. At this slack it can never get far enough below "
                + "the floor in one tick to die, so it oscillates at the boundary alive — X-75 "
                + "again. Zero is the intended value.");

            Assert.Equal(0f, slack);
        }

        private static string ReadServerPlayerSource()
        {
            string path = Path.Combine(
                RepoRoot(),
                "Ironfront_Reborn", "Assets", "Scripts", "Net", "Server", "ServerPlayer.cs");

            Assert.True(File.Exists(path), $"missing ServerPlayer.cs at {path}");
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
