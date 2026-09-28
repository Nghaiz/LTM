using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-59 / X-60 — the two defects behind the exception storm a lane-A log carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything under test lives in <c>Assembly-CSharp</c>, which no asmdef may reference and
    /// which <c>dotnet build</c> never compiles, so these read the shipped sources off disk —
    /// the same instrument as <c>NullReferenceCascadeTests</c>, with the same limit: they pin
    /// the SHAPE of the fix, and the lane-A run in the report is what pins the behaviour.
    /// </para>
    /// <para>
    /// <b>Both were observed RED against the pre-fix tree, and the counts are recorded</b> in
    /// <c>plans/reports/2026-08-29-p1-exception-storm.md</c> beside the lane-A run that produced
    /// them. A detector that has never failed is decoration (plan § 5 rule 4).
    /// </para>
    /// </remarks>
    public sealed class ExceptionStormTests
    {
        // ------------------------------------------------ X-59: the double-add

        /// <summary>
        /// A death written through the net seam leaves the alive register, exactly as the
        /// gameplay death does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The enumeration, which is the deliverable and not the guard.</b>
        /// <c>ActorManager.aliveActors</c> is added to by <c>SetAlive</c> (from
        /// <c>Actor.SpawnAt</c> and <c>ForcedAiTarget.Start</c>) and removed from by
        /// <c>SetDead</c> (from <c>Actor.Die</c> and <c>Actor.OnDestroy</c>) — and the flag those
        /// two registers mirror, <c>Actor.dead</c>, is written in four places: <c>Actor.Awake</c>,
        /// <c>Actor.SpawnAt</c>, <c>Actor.Die</c>, and <c>ActorGameplaySource.IsDead</c>. The
        /// first three maintain the register. The fourth did not.
        /// </para>
        /// <para>
        /// <b>So the second registration was never legitimate.</b>
        /// <c>ServerActorDamageSink</c> kills by writing <c>NetServerActor.IsAlive = false</c>,
        /// which writes through to <c>Actor.dead</c> and deliberately does NOT call
        /// <c>Actor.Die()</c> — its own remark says so, and the reasons are good ones. But
        /// <c>Actor.Die()</c> is the only caller of <c>SetDead</c> on that path, so the corpse
        /// stayed in <c>aliveActors[team]</c>; <c>ActorManager.SpawnWave</c> then selected the
        /// body on <c>dead</c> and <c>Actor.SpawnAt</c> registered it a SECOND time. Every
        /// <c>FindPotentialTargets</c> on the opposing team then threw
        /// <c>ArgumentException: An item with the same key has already been added</c> out of
        /// <c>distanceTo.Add</c> for the rest of the run.
        /// </para>
        /// <para>
        /// <b>Which is why the fix closes the window rather than guarding the add.</b> A
        /// membership test at <c>SetAlive</c> makes the symptom unproducible without saying why
        /// a body was registered twice; the pair below says why and stops it happening.
        /// </para>
        /// </remarks>
        [Fact]
        public void ADeathWrittenThroughTheNetSeamLeavesTheAliveRegisterToo()
        {
            string bindings = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/NetBindings/IronfrontNetBindings.cs");

            // Scoped to the class first: VehicleGameplaySource declares an IsDead of its own and
            // is the first match in the file, so an unscoped search reads the wrong entity.
            string source = MethodBody(bindings, "internal sealed class ActorGameplaySource");
            string body = MethodBody(source, "public bool IsDead");

            Assert.Contains("ActorManager.SetDead", body, StringComparison.Ordinal);
            Assert.Contains("ActorManager.SetAlive", body, StringComparison.Ordinal);

            // The flag and the register are one fact in two places, so the write is idempotent:
            // without the early-out, a second `IsAlive = true` on a body already alive would add
            // a second entry and reopen X-59 through the very setter that closes it.
            Assert.Matches(new Regex(@"_actor\.dead\s*==\s*value"), body);
        }

        /// <summary>
        /// Nothing outside those four files writes the alive register, so the enumeration above
        /// is the whole enumeration.
        /// </summary>
        /// <remarks>
        /// Asserted by FILE IDENTITY rather than by a count: a count is satisfied by any set of
        /// the right size, and the property that matters is WHICH code owns the register. A new
        /// caller fails here, and the failure means the enumeration in
        /// <see cref="ADeathWrittenThroughTheNetSeamLeavesTheAliveRegisterToo"/> has to be
        /// redone rather than the list here extended.
        /// </remarks>
        [Fact]
        public void TheAliveRegisterIsWrittenFromTheseFilesAndNoOthers()
        {
            // The CALLERS. ActorManager.cs is absent deliberately -- it declares SetAlive and
            // SetDead rather than calling them, so it never matches the qualified form below.
            var expected = new[]
            {
                "Actor.cs",                // SpawnAt adds; Die and OnDestroy remove
                "ForcedAiTarget.cs",       // a static target dummy: adds once, never dies
                "IronfrontNetBindings.cs", // the net seam's write-through, paired above
            };

            var found = new SortedSet<string>(StringComparer.Ordinal);

            foreach (string file in Directory.EnumerateFiles(
                         Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts"),
                         "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (Regex.IsMatch(text, @"ActorManager\.Set(Alive|Dead)\s*\("))
                {
                    found.Add(Path.GetFileName(file));
                }
            }

            Assert.Equal(expected.OrderBy(f => f, StringComparer.Ordinal), found);
        }

        /// <summary>
        /// The pair is on the path the defect actually used: the server's kill still funnels
        /// through <c>IsAlive</c>, so pairing that setter covers it.
        /// </summary>
        /// <remarks>
        /// A fix can be present and never run. This is the wired half — if the damage sink is
        /// ever changed to write the flag another way, the pair above stops covering the kill
        /// path and this goes red rather than the next lane-A log going 60 exceptions deep.
        /// </remarks>
        [Fact]
        public void TheServerKillStillWritesTheFlagThroughTheSeamThatMaintainsTheRegister()
        {
            string sink = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Net/Server/ServerActorDamageSink.cs");

            Assert.Matches(new Regex(@"victim\.IsAlive\s*=\s*false"), sink);

            // Over CODE, not prose. Two remarks in this file discuss Actor.Die() at length --
            // including the one explaining why it is deliberately not called -- so a matcher
            // that reads comments reports the opposite of the truth. That is the trap
            // check-net-layering documents, and it caught this assertion on its first run.
            Assert.DoesNotMatch(new Regex(@"\bDie\s*\("), CodeOnly(sink));
        }

        // -------------------------------------- X-60: the anti-stuck null reference

        /// <summary>
        /// The anti-stuck event marks the vehicle this body is DRIVING, not the squad's own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The filed cause was wrong, and the code says so.</b> X-60 was filed as "a squadless
        /// body with an enabled controller reaches <c>PushAntiStuckEvent</c> and dereferences a
        /// null squad", with the candidate fix of requiring a squad in <c>AiWorkAllowed()</c>.
        /// But <c>PushAntiStuckEvent</c> is reachable from exactly one place — the Car/Tank
        /// branch of <c>AiVehicle</c> — and that branch is entered only after
        /// <c>IsSquadLeader()</c>, which is <c>squad.Leader() == this</c> with no null guard. A
        /// null <c>squad</c> throws there, at the branch head, and never reaches the anti-stuck
        /// event at all. <c>AiOrders</c> would have thrown on <c>squad.Update()</c> every half
        /// second besides. Neither is in any artifact; only <c>PushAntiStuckEvent</c> is.
        /// </para>
        /// <para>
        /// <b>What is actually null is <c>squad.squadVehicle</c>.</b> It is written only by
        /// <c>Squad.EnterVehicle</c> and <c>Squad.SetAlreadyInVehicle</c>, so a squad whose
        /// member boarded on its own — <c>AiVehicle</c>'s own tail does exactly that,
        /// <c>actor.EnterSeat(targetVehicle.GetEmptySeat())</c> — has none, while that member is
        /// nonetheless driving. Getting stuck three times then dereferences it. That is the
        /// intermittency the counts show (5 / 3 / 2 / 0 / 0 / 0 / 0 / 0 across eight runs): it
        /// needs a lone boarder AND a stuck vehicle, not a fixed state.
        /// </para>
        /// <para>
        /// <b>So this is a wrong reference corrected, not a null check added.</b> The Boat branch
        /// two lines above already marks <c>actor.seat.vehicle</c>, and that is the vehicle that
        /// is stuck. Gating <c>AiWorkAllowed()</c> on a squad — the filed candidate — would have
        /// changed the one gate all eight coroutines park on and closed nothing.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheAntiStuckEventMarksTheVehicleTheBodyIsDrivingAndNotTheSquadsOwn()
        {
            string controller = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs");

            string body = MethodBody(controller, "private void PushAntiStuckEvent()");

            Assert.DoesNotContain("squad.squadVehicle", body, StringComparison.Ordinal);
            Assert.Contains("actor.seat.vehicle.stuck", body, StringComparison.Ordinal);

            // The squad itself is NOT null here and is still ordered out of the vehicle. A fix
            // that dropped these would be muting the method rather than correcting a reference.
            Assert.Contains("squad.ExitVehicle()", body, StringComparison.Ordinal);
            Assert.Contains("squad.MoveTo(", body, StringComparison.Ordinal);

            // An unseated body reaching here is a state nothing has explained, so it is reported
            // rather than skipped -- the same treatment EjectOccupants gives a one-sided seat.
            Assert.Contains("Debug.LogError", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The evidence for the paragraph above: the driving branch proves the squad is
        /// non-null before an anti-stuck event can be pushed.
        /// </summary>
        /// <remarks>
        /// Not a pinned baseline — it asserts a healthy invariant and would go red only if
        /// someone made <c>IsSquadLeader()</c> null-tolerant, which would silently re-admit a
        /// squadless body to that branch and make the enumeration above false. At that point the
        /// enumeration is what needs redoing, not this assertion.
        /// </remarks>
        [Fact]
        public void TheDrivingBranchDereferencesTheSquadBeforeItCanPushAnAntiStuckEvent()
        {
            string controller = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs");

            string leader = MethodBody(controller, "public bool IsSquadLeader()");
            Assert.Contains("squad.Leader() == this", leader, StringComparison.Ordinal);
            Assert.DoesNotContain("InSquad()", leader, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"squad\s*(==|!=)\s*null"), leader);

            string vehicle = MethodBody(controller, "private IEnumerator AiVehicle()");
            int gate = vehicle.IndexOf("IsSquadLeader()", StringComparison.Ordinal);
            int push = vehicle.IndexOf("PushAntiStuckEvent()", StringComparison.Ordinal);

            Assert.True(gate >= 0, "AiVehicle no longer asks whether this body leads its squad");
            Assert.True(push >= 0, "AiVehicle no longer pushes anti-stuck events");
            Assert.True(
                gate < push,
                "PushAntiStuckEvent is now reachable without the squad having been dereferenced "
                + "first, so a null squad can reach it and X-60's filed cause becomes possible "
                + "after all. Re-do the enumeration before touching the throw site.");
        }

        // ------------------------------------------------------------ instrument

        /// <summary>
        /// The braced body of the first method whose signature line matches
        /// <paramref name="signature"/>, brace-counted rather than regex-matched.
        /// </summary>
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

        /// <summary>
        /// <paramref name="source"/> with line and block comments removed, for the assertions
        /// that must read code rather than the prose describing it.
        /// </summary>
        private static string CodeOnly(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", " ");
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

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
