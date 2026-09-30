using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-69 and X-71, which are one defect. <c>AiActorController.Velocity</c> is an override that
    /// another component CALLS, so <c>enabled = false</c> does not stop it — and it was the one
    /// movement override with no <c>base.enabled</c> guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why one missing guard produced two ledger rows.</b> <c>IAiDriver.Suspend</c> disables
    /// the bot brain when a connection claims a body, because <c>ServerPlayer</c> driving through
    /// <c>NetMovementAgent</c> must be the single writer to that position. Unity's
    /// <c>enabled</c> flag gates the engine's own callbacks and nothing else, which is exactly
    /// why <c>BoatInput</c>, <c>CarInput</c>, <c>HelicopterInput</c>, <c>StartSeated</c>,
    /// <c>EndSeated</c> and <c>AiWorkAllowed</c> each open with an explicit check. <c>Velocity</c>
    /// did not, so on a claimed body it kept returning a real walk vector:
    /// </para>
    /// <list type="bullet">
    /// <item><b>X-71</b> — a second writer moved the body. 518 m across the map, monotonic,
    /// beginning while its owner was still alive and sending no movement input.</item>
    /// <item><b>X-69</b> — that same call reaches <c>LocalAvoidanceVelocity</c>, which enumerates
    /// <c>squad.members</c>. Every networked player slot is squadless by design (see
    /// <c>AiActorController.OnDestroy</c>'s own remark), so the enumeration dereferences null:
    /// 534 in one P5 run, 10,126 in one 600 s P7 soak. Late-onset because it needs a claimed,
    /// pathing, squadless body — which is what accumulates as players join and leave.</item>
    /// </list>
    /// <para>
    /// <b>Not a null-guard, and <c>TheRosterIsMendedAtTheRegisterRatherThanAtItsLoudestReader</c>
    /// is why.</b> That test forbids <c>member == null</c> / <c>actor == null</c> inside
    /// <c>LocalAvoidanceVelocity</c>, because silencing the loudest reader leaves a corpse in the
    /// roster for four quieter ones to average in. This fix is the opposite shape: there is no
    /// corpse and no register to mend — a suspended brain simply must not be steering, and the
    /// guard goes where the other six already are.
    /// </para>
    /// <para>
    /// Both assertions were observed RED against the pre-fix tree.
    /// </para>
    /// </remarks>
    public sealed class SuspendedControllerMovementTests
    {
        private const string Controller =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs";

        [Theory]
        [InlineData("public override Vector3 Velocity()")]
        [InlineData("public override Vector3 SwimInput()")]
        public void ASuspendedBrainSteersNothing(string signature)
        {
            string body = MethodBody(ReadUnitySource(Controller), signature);

            Assert.Matches(new Regex(@"!\s*base\.enabled"), body);

            // Above the hasPath branch, not inside it. A guard below it still runs the whole
            // steering path on a claimed body that happens to be pathing -- which is the only
            // state either defect was ever observed in.
            int guard = body.IndexOf("base.enabled", StringComparison.Ordinal);
            int hasPath = body.IndexOf("hasPath", StringComparison.Ordinal);
            Assert.True(
                hasPath < 0 || guard < hasPath,
                $"'{signature}' checks hasPath before base.enabled, so a suspended brain still steers");
        }

        [Fact]
        public void EverySteeringOverrideCarriesTheGuardRatherThanAListOfSix()
        {
            // The companion. Velocity was missed for as long as it was because the guard was
            // applied one method at a time, by hand, six times -- and the seventh looked like
            // the other six from a distance. This fails on the eighth override added without it.
            string source = ReadUnitySource(Controller);

            var missing = new System.Collections.Generic.List<string>();

            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(
                         source, @"public override (?:Vector[234]|void) (\w+)\([^)]*\)"))
            {
                string signature = m.Value;
                string name = m.Groups[1].Value;

                if (MustRunSuspended.ContainsKey(name)) continue;

                string body;
                try { body = MethodBody(source, signature); }
                catch (InvalidOperationException) { continue; }

                bool steers = body.Contains("hasPath", StringComparison.Ordinal)
                              || body.Contains("squad.", StringComparison.Ordinal);

                if (steers && !Regex.IsMatch(body, @"!\s*base\.enabled")) missing.Add(name);
            }

            Assert.True(
                missing.Count == 0,
                "steering override(s) with no base.enabled guard, so a suspended brain still "
                + "drives a claimed body: " + string.Join(", ", missing));

            // The companion direction. An exemption that has quietly acquired the guard is a
            // record of the past, and this is where the next reader would trust it.
            foreach (var exempt in MustRunSuspended)
            {
                string body;
                try { body = MethodBody(source, "public override void " + exempt.Key + "("); }
                catch (Xunit.Sdk.XunitException) { continue; }

                Assert.False(
                    Regex.IsMatch(body, @"!\s*base\.enabled"),
                    $"{exempt.Key} IS guarded but is still listed in MustRunSuspended. Delete "
                    + $"that entry -- the reason it carries is no longer true. Reason on record: "
                    + exempt.Value);
            }
        }

        [Fact]
        public void AnEnabledBrainWithNoSquadNeverReachesTheSquadAvoidance()
        {
            // The enabled half. The P29 capacity bench (two maps at 100 bots, Island) had one
            // body walking a path with no squad: LocalAvoidanceVelocity enumerates squad.members
            // and threw every frame, 4,966 times in 43 s. The avoidance is only asked for inside
            // a squad check; a bot with no squad walks on without it. Comments out first: the
            // remark there names the method in prose.
            string body = Regex.Replace(
                MethodBody(ReadUnitySource(Controller), "public override Vector3 Velocity()"),
                @"//[^\n]*", "");

            Assert.True(
                body.Contains("LocalAvoidanceVelocity()", StringComparison.Ordinal),
                "Velocity no longer calls LocalAvoidanceVelocity; this test pins nothing");
            Assert.Matches(
                new Regex(@"if\s*\(\s*InSquad\(\)\s*\)\s*\{[^{}]*LocalAvoidanceVelocity\(\)"), body);
            Assert.Single(Regex.Matches(body, @"LocalAvoidanceVelocity\(\)"));
        }

        [Fact]
        public void ParkingABodyHandsItOverAndAReleasedBodyLeavesTheMatch()
        {
            // Disabling the controller parks it and stops nothing else: Unity stops coroutines
            // when a GameObject is deactivated, not when a component is disabled. So a claimed
            // body stayed on its squad's roster, and the commander counted a player as its bot (P29).
            string driver = MethodBody(
                ReadUnitySource("Ironfront_Reborn/Assets/Scripts/NetBindings/IronfrontNetBindings.cs"),
                "internal sealed class AiActorControllerDriver");

            string suspend = MethodBody(driver, "public void Suspend()");
            Assert.Matches(new Regex(@"HandOverToPlayer\(\)\s*;\s*_ai\.enabled\s*=\s*false"), suspend);

            string controller = ReadUnitySource(Controller);
            string handOver = MethodBody(controller, "public void HandOverToPlayer()");
            Assert.Contains("squad.DropMember(this)", handOver, StringComparison.Ordinal);
            Assert.Contains("StopAllCoroutines()", handOver, StringComparison.Ordinal);

            // And nothing hands it back. A resume on release made a leaver's body play on as a
            // bot, and an unclaimed slot is announced to no client (X-18): on Island on
            // 2026-09-30 it played on unseen, and asked for a path twice a second for four hours
            // from off the navgraph. The leave takes the body out of the match instead.
            Assert.DoesNotContain("Resume()", driver, StringComparison.Ordinal);
            Assert.DoesNotContain("TakeOverAsBot", controller, StringComparison.Ordinal);

            string registry = ReadUnitySource("Ironfront_Reborn/Assets/Scripts/Net/Server/ServerActorRegistry.cs");
            string releaseSlot = MethodBody(registry, "public void ReleaseSlot(NetServerActor actor)");
            Assert.Matches(new Regex(@"actor\.Release\(\)\s*;\s*actor\.ReturnToPool\(\)\s*;"), releaseSlot);

            string body = ReadUnitySource("Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerActor.cs");
            string returnToPool = MethodBody(body, "internal void ReturnToPool()");
            Assert.Matches(new Regex(@"Health\s*=\s*0f\s*;\s*IsAlive\s*=\s*false\s*;"), returnToPool);
            Assert.Contains("Teleport(_poolPosition)", returnToPool, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryWayOntoASquadRosterIsAMove()
        {
            // A bot is on exactly one roster, the one its squad field names. A roster that kept a
            // bot it had lost counted it twice (56 bots a side against 50 in the P29 bench), and
            // Join returned early for a bot already listed -- leaving it walking the squad's
            // orders with no squad of its own. Both ways onto a roster now take the bot off any
            // other first, and Join always hands over the squad.
            string squad = ReadUnitySource("Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Squad.cs");

            string constructor = MethodBody(squad, "public Squad(List<AiActorController> members, float timeUntilReady)");
            Assert.Matches(new Regex(@"TakeOffOtherRoster\(member\)\s*;\s*member\.AssignedToSquad\(this\)"), constructor);

            string join = MethodBody(squad, "public void Join(AiActorController member)");
            Assert.Contains("TakeOffOtherRoster(member)", join, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"members\.Contains\(member\)\s*\)\s*\{\s*return"), join);
            Assert.DoesNotMatch(new Regex(@"\|\|\s*members\.Contains\(member\)"), join);
            Assert.Contains("member.AssignedToSquad(this)", join, StringComparison.Ordinal);
        }

        /// <summary>
        /// Overrides that MUST still run on a suspended controller, each with the reason.
        /// </summary>
        /// <remarks>
        /// Not everything that mentions <c>squad</c> is steering. <c>Die</c> unwinds a body that
        /// is dying whether or not a brain is driving it, and its own remark records what
        /// happened when it did not run: the first death threw, which aborted the rest of
        /// <c>Actor.Die</c>, so the body never finished dying and died again the next frame. A
        /// guard here would reproduce that from the other direction.
        /// </remarks>
        private static readonly System.Collections.Generic.Dictionary<string, string>
            MustRunSuspended = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Die"] =
                    "cancels rather than starts movement, and Actor.Die depends on it completing "
                    + "on every body including a claimed one. Its squad reference is guarded by "
                    + "InSquad(), not by enabled.",
            };

        // ------------------------------------------------------------------ helpers

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
