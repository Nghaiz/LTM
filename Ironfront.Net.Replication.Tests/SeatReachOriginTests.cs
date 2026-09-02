using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-67. The client and the server measured a seat request from different origins against
    /// one 6 m constant, and three unmeasurable cases all came back to the player as
    /// "Too far from the seat."
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are SOURCE-INVARIANT assertions and that is a real limitation, stated rather than
    /// glossed: they read the shipped files off disk and check what those files say, not what a
    /// running server answers. <c>ServerSeatBridge</c> takes two concrete registry singletons, so
    /// a behavioural test of it needs a live scene; the arbiter's own half — the part that IS
    /// engine-free — is already covered by <c>SeatArbiter</c>'s tests, and what these guard is
    /// exactly the part that is not.
    /// </para>
    /// <para>
    /// <b>What this does NOT establish.</b> X-67 is filed as an origin mismatch, and that
    /// hypothesis is still unproven: P5's four <c>RejectedTooFar</c> refusals at 3.70–4.27 m
    /// from the hull are equally consistent with an unknown vehicle, an unknown actor, or a seat
    /// index with no <c>Seat</c> behind it, because every one of those rendered identically. The
    /// row stays open until a run distinguishes them. What changed is that a run now can.
    /// </para>
    /// <para>
    /// Both assertions were observed RED against the pre-fix tree.
    /// </para>
    /// </remarks>
    public sealed class SeatReachOriginTests
    {
        private const string Bridge =
            "Ironfront_Reborn/Assets/Scripts/Net/Server/ServerSeatBridge.cs";

        private const string Requester =
            "Ironfront_Reborn/Assets/Scripts/Net/Client/ClientSeatRequester.cs";

        [Fact]
        public void TheClientOffersTheSeatTheServerWillMeasure()
        {
            // The hull origin is what it used, and on a tank the driver's seat is metres from it.
            string source = ReadUnitySource(Requester);
            string body = MethodBody(
                source, "private bool TryFindNearestSeat(Vector3 from, out ushort vehicleId, out byte seatIndex)");

            // Comments stripped first. The remark inside this very method NAMES the old origin
            // in order to explain the fix, and a check that a comment can satisfy -- or defeat --
            // is not checking the code.
            string code = StripComments(body);

            Assert.Contains("GetSeatPosition", code, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"Body\.Transform\.position"), code);
        }

        [Fact]
        public void AnUnmeasurableRequestIsNotReportedAsDistance()
        {
            // An unknown actor and an unknown vehicle each returned float.MaxValue, and a missing
            // seat returned Vector3.positiveInfinity -- so all three reached the arbiter as an
            // enormous distance and came back as RejectedTooFar. RejectedNoSuchSeat already
            // existed, documented as "No such vehicle, or no such seat index on it", and was
            // never sent from here.
            string body = MethodBody(
                ReadUnitySource(Bridge),
                "private SeatChangeResult? TryMeasureSeatReach(");

            Assert.Contains("RejectedNoSuchSeat", body, StringComparison.Ordinal);

            // Three of them: unknown actor, unknown vehicle, unlocatable seat.
            //
            // If this reads 4, do NOT re-pin it. A fourth unmeasurable case gets its OWN code,
            // the way RejectedActorUnplaced did -- raising this number is how "no such seat"
            // becomes the second message that means four different things, which is the exact
            // defect X-67 was filed for.
            Assert.Equal(3, Regex.Matches(body, "RejectedNoSuchSeat").Count);

            // And the infinite coordinate is CHECKED rather than allowed to subtract into an
            // infinite distance, which is a comparison the arbiter answers "too far" without
            // anything being far away.
            Assert.Matches(new Regex(@"float\.IsInfinity"), body);
        }

        /// <summary>
        /// X-67, the fourth case -- and the one the row was actually filed against, having
        /// mistaken it for an origin mismatch.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In <c>p5-e11-03</c> a client stood 4.08 m from a hull and was refused four times with
        /// <c>RejectedTooFar</c>. The server's copy of that body was at y = -1024.67 -- on
        /// <c>Quantize.POS_MIN</c>, having fallen out of the world -- so it measured ~1037 m and
        /// refused correctly. The message was true and useless: the remedy it implies is "walk
        /// closer", and the position the player walks is not the one being measured.
        /// </para>
        /// <para>
        /// Observed RED against the pre-fix tree: <c>TryMeasureSeatReach</c> at
        /// <c>b940e9f</c> contains neither token.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnActorOutsideTheWirePositionRangeIsNotReportedAsDistance()
        {
            string body = StripComments(MethodBody(
                ReadUnitySource(Bridge),
                "private SeatChangeResult? TryMeasureSeatReach("));

            // The saturation is CHECKED, not allowed to subtract into a distance-to-the-clamp.
            Assert.Contains("PositionSaturates", body, StringComparison.Ordinal);

            // And it answers with its own code rather than borrowing one that would lie.
            Assert.Contains("RejectedActorUnplaced", body, StringComparison.Ordinal);

            // Never as "too far": that is the message this case was mis-rendered as for a day.
            Assert.DoesNotContain("RejectedTooFar", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The client can say what the new refusal means. A code the client renders as the
        /// default "Seat refused." is a code that has not arrived anywhere useful.
        /// </summary>
        [Fact]
        public void TheClientNamesTheUnplacedRefusalRatherThanFallingThrough()
        {
            string body = MethodBody(
                ReadUnitySource(Requester),
                "private static string RefusalText(");

            Assert.Contains("RejectedActorUnplaced", body, StringComparison.Ordinal);
        }

        [Fact]
        public void OnlyOneImplementationLocatesASeat()
        {
            // The fix is worth nothing if a second copy of the lookup survives: two copies of
            // one measurement is how the two ends came to disagree in the first place.
            string bindings = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/NetBindings/IronfrontNetBindings.cs");

            // Expression-bodied, so there is no brace pair to walk: take the declaration and
            // the line under it.
            int at = bindings.IndexOf(
                "public Vector3 GetSeatPosition(int seatIndex)", StringComparison.Ordinal);
            Assert.True(at >= 0, "VehicleGameplaySource no longer declares GetSeatPosition");

            string code = StripComments(bindings.Substring(at, Math.Min(400, bindings.Length - at)));

            Assert.Contains("_vehicle.GetSeatPosition", code, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"seat\.transform\.position"), code);
        }

        [Fact]
        public void LeavingASeatIsNotRefusedForAnUnmeasurableDistance()
        {
            // DecideLeave answers from where the actor actually is and never reads the distance,
            // so a leave request for a vehicle the registry has already dropped must still put
            // the player back on foot rather than strand them inside a despawned hull.
            string body = MethodBody(
                ReadUnitySource(Bridge),
                "public void OnSeatRequested(ClientSession session, in SeatRequestMessage message)");

            Assert.Matches(new Regex(@"unmeasurable\.HasValue[\s\S]{0,120}SeatAction\.Enter"), body);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Drops <c>//</c> comments, so an assertion about code cannot be satisfied — or broken —
        /// by prose that happens to quote the thing it forbids.
        /// </summary>
        private static string StripComments(string source)
            => Regex.Replace(source, @"//[^\r\n]*", string.Empty);

        private static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, $"no method '{signature}' in the source");

            int open = source.IndexOf('{', at);
            Assert.True(open >= 0, $"'{signature}' has no body");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            throw new InvalidOperationException($"unbalanced braces after '{signature}'");
        }

        private static string ReadUnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException("no Ironfront.sln above the working directory");
        }
    }
}
