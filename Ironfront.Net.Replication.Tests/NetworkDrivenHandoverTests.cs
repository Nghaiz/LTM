using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-64. The handover to the replication layer could be RECORDED without ever happening, and
    /// then never retried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mechanism was documented before it was observed.</b> <c>SetNetworkDriven</c>'s own
    /// remark says a replicated vehicle whose <c>Rigidbody</c> is still dynamic "runs local PhysX
    /// against the incoming snapshots… jitter that looks exactly like a network problem and is
    /// not. Nothing above this layer can diagnose that, because every number on the wire is
    /// correct." What was missing is that the handover could silently fail to occur.
    /// </para>
    /// <para>
    /// <c>Vehicle.rigidbody</c> is cached in <c>Awake</c>, and the replication layer binds its
    /// proxy — <c>NetClientVehicle</c>'s constructor calls <c>SetMode(Remote)</c> — at spawn. If
    /// the field is not populated at that moment, the old code set <c>NetworkDriven = true</c>,
    /// skipped the <c>isKinematic</c> write, and its <c>NetworkDriven == value</c> early-return
    /// then made every later call a no-op. The handover was recorded as done and had never
    /// happened.
    /// </para>
    /// <para>
    /// <b>What P4 measured matches that and not the row's own lead.</b> On
    /// <c>p4-vehicle-02</c>, OBS-B read vehicle 15 as 303 m behind at <c>driven</c> with
    /// <c>vehicleInterpStalled 0</c>, <c>vehicleBaselineMiss 0</c> and 74 snapshots applied over
    /// the interval. The row proposed a duplicate or mis-keyed proxy, pointing at an id 4 that
    /// only OBS-B listed — but id 4 appears on <b>all three clients</b> (driver and OBS-A one
    /// checkpoint later), at the pad the driven vehicle left, so it is the spawner's replacement
    /// and not a duplicate. The real anomaly is earlier: OBS-B's copy of 15 read
    /// <c>(2099.69, 13.53, 1159.40)</c> at <c>at-vehicle</c> against
    /// <c>(2097.36, 12.33, 1150.91)</c> on both others — <b>9 m apart before anyone drove</b> —
    /// then jittered by centimetres and stopped. That is a dynamic body settling and resting,
    /// not an interpolator stalling.
    /// </para>
    /// <para>
    /// These are source-invariant assertions, stated as the limitation they are: <c>Vehicle</c>
    /// is Assembly-CSharp and a behavioural test of it needs a live Unity domain. Both were
    /// observed RED against the pre-fix tree and mutation-tested afterwards.
    /// </para>
    /// </remarks>
    public sealed class NetworkDrivenHandoverTests
    {
        private const string Vehicle =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs";

        [Fact]
        public void TheHandoverIsNotRecordedUntilTheBodyAgrees()
        {
            // The flag alone is not the question. "Did I already say true?" answers yes for a
            // vehicle that never went kinematic; "is the body kinematic?" answers no.
            string body = StripComments(MethodBody(ReadUnitySource(Vehicle),
                "public void SetNetworkDriven(bool value)"));

            Assert.Matches(new Regex(@"rigidbody\.isKinematic\s*==\s*value"), body);

            // And the early-return must require BOTH, not the flag on its own.
            Assert.DoesNotMatch(
                new Regex(@"if\s*\(\s*NetworkDriven\s*==\s*value\s*\)"),
                body);
        }

        [Fact]
        public void AMissingBodyIsResolvedAndThenReported()
        {
            // Awake caches the field, and Awake has not run when a prefab is instantiated
            // inactive -- which is precisely when the replication layer binds.
            string body = StripComments(MethodBody(ReadUnitySource(Vehicle),
                "public void SetNetworkDriven(bool value)"));

            Assert.Matches(new Regex(@"rigidbody\s*==\s*null.*GetComponent<Rigidbody>\(\)"), body);

            // Errors over silent fallbacks: a vehicle that still has no body cannot be handed
            // over at all, and nothing on the wire will ever say so.
            Assert.Contains("Debug.LogError", body, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ helpers

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
