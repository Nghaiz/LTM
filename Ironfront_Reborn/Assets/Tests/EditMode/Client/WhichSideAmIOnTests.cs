using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// P12's two client-side rules: the local body takes the team the server put it on, and the
    /// minimap draws friendlies rather than everybody.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both defects were green in CI and invisible to every existing gate.</b>
    /// <c>ClientWiringGate</c> retires an event exemption on SUBSCRIPTION, and both of these had
    /// a subscriber — the snapshot was decoded, the team was read, the markers were drawn. What
    /// was wrong was the VALUE, and no rule about wiring can see a value.
    /// </para>
    /// <para>
    /// <b>Every assertion here was observed RED against the un-fixed code</b>, and the mutations
    /// are recorded in the phase report. An assertion nobody has watched fail is a claim about
    /// intent, not about behaviour.
    /// </para>
    /// <para>
    /// <b>Globals are captured and restored per test.</b> <c>NetContext.Role</c>,
    /// <c>NetClientBindings.LocalPlayer</c> and <c>NetClientBindings.LocalTeam</c> are statics
    /// that survive between tests in one domain, so a test that left any of them set would change
    /// the answer of every test after it — silently, and differently depending on run order.
    /// </para>
    /// </remarks>
    public sealed class WhichSideAmIOnTests
    {
        private NetRole _role;
        private ILocalPlayerRig _rig;
        private NetClientBindings.LocalTeamResolver _localTeam;

        [SetUp]
        public void CaptureGlobals()
        {
            _role = NetContext.Role;
            _rig = NetClientBindings.LocalPlayer;
            _localTeam = NetClientBindings.LocalTeam;
        }

        [TearDown]
        public void RestoreGlobals()
        {
            NetContext.SetRole(_role);
            NetClientBindings.LocalPlayer = _rig;
            NetClientBindings.LocalTeam = _localTeam;
        }

        // ------------------------------------------------------------------ D-1: the local team

        /// <summary>
        /// A client the server put on team 1 puts its own body on team 1.
        /// </summary>
        /// <remarks>
        /// The whole of D-1. <c>Player Fps Actor.prefab</c> authored <c>team: 0</c> and the only
        /// <c>Actor.SetTeam</c> callers were the offline bot factory and the SERVER's body
        /// factory — so on every client, for every player, the local body believed it was team 0.
        /// A team-1 player saw their own body in blue and every <c>actor.team == playerTeam</c>
        /// test in the game answered for the wrong side.
        /// </remarks>
        [Test]
        public void LocalBody_TakesTheTeamTheSnapshotReports()
        {
            var rig = new RecordingRig { Exists = true, Team = -1 };
            Arrange(NetRole.Client, rig, TeamId.Team1);

            NetClientLocalCombatDriver.ApplyLocalTeam();

            Assert.AreEqual(TeamId.Team1, rig.Team,
                "the local body kept its authored team while the server said team 1 — D-1.");
            Assert.AreEqual(1, rig.SetTeamCalls);
        }

        /// <summary>The apply is idempotent, so a per-frame poll costs one comparison.</summary>
        /// <remarks>
        /// <c>Actor.SetTeam</c> writes <c>material.color</c> on two skinned renderers, which
        /// instantiates a material the first time. Re-writing it every frame for a value that
        /// changes at most once a life is what the equality test in <c>ApplyLocalTeam</c> exists
        /// to avoid, and a poll with no such test is how a fix becomes a frame-rate problem.
        /// </remarks>
        [Test]
        public void LocalTeamApply_WritesOnceAndThenStops()
        {
            var rig = new RecordingRig { Exists = true, Team = -1 };
            Arrange(NetRole.Client, rig, TeamId.Team1);

            NetClientLocalCombatDriver.ApplyLocalTeam();
            NetClientLocalCombatDriver.ApplyLocalTeam();
            NetClientLocalCombatDriver.ApplyLocalTeam();

            Assert.AreEqual(1, rig.SetTeamCalls, "a settled team was re-written on a later poll.");
        }

        /// <summary>
        /// Team 0 is a real answer and is applied like any other.
        /// </summary>
        /// <remarks>
        /// The trap the phase named explicitly: a sentinel of <c>0</c> re-creates D-1 in a new
        /// place. If "not known yet" and "team 0" share a value, a genuine team-0 player is
        /// indistinguishable from an unresolved one and the apply either never runs or runs
        /// forever. <c>-1</c> is the engine's unknown and <see cref="TeamId.None"/> the wire's;
        /// neither is 0.
        /// </remarks>
        [Test]
        public void LocalTeamApply_TreatsTeamZeroAsAnAnswerNotAsAbsence()
        {
            var rig = new RecordingRig { Exists = true, Team = -1 };
            Arrange(NetRole.Client, rig, TeamId.Team0);

            NetClientLocalCombatDriver.ApplyLocalTeam();

            Assert.AreEqual(TeamId.Team0, rig.Team);
            Assert.AreEqual(1, rig.SetTeamCalls,
                "team 0 was read as 'not known yet' — the sentinel collision D-1 warns about.");
        }

        /// <summary>Nothing is written until the snapshot actually names a team.</summary>
        [Test]
        public void LocalTeamApply_WritesNothingBeforeTheTeamArrives()
        {
            var rig = new RecordingRig { Exists = true, Team = -1 };

            NetContext.SetRole(NetRole.Client);
            NetClientBindings.LocalPlayer = rig;
            NetClientBindings.LocalTeam = Unresolved;

            NetClientLocalCombatDriver.ApplyLocalTeam();

            Assert.AreEqual(0, rig.SetTeamCalls);
            Assert.AreEqual(-1, rig.Team);
        }

        /// <summary>
        /// A rig that does not exist yet is not written to, in either arrival order.
        /// </summary>
        /// <remarks>
        /// Hazard 2 of the phase: the body and the first snapshot arrive in either order, so the
        /// apply must land on whichever comes SECOND. A poll gets that for free — this pins that
        /// the absent-rig branch does not throw and does not consume the team, so the next poll
        /// still applies it once the body exists.
        /// </remarks>
        [Test]
        public void LocalTeamApply_SurvivesTheTeamArrivingBeforeTheBody()
        {
            var rig = new RecordingRig { Exists = false, Team = -1 };
            Arrange(NetRole.Client, rig, TeamId.Team1);

            NetClientLocalCombatDriver.ApplyLocalTeam();
            Assert.AreEqual(0, rig.SetTeamCalls, "wrote a team to a body that does not exist.");

            rig.Exists = true;
            NetClientLocalCombatDriver.ApplyLocalTeam();

            Assert.AreEqual(TeamId.Team1, rig.Team,
                "the team was dropped rather than applied when the body finally arrived.");
        }

        /// <summary>Offline keeps setting its own team, and this never touches it.</summary>
        /// <remarks>
        /// Acceptance criterion 2's unit half. Offline the answer is set in
        /// <c>FpsActorController.Awake</c> and there is no snapshot to override it; a resolver
        /// that answered anyway must not be allowed to write, or single-player inherits a
        /// networked decision.
        /// </remarks>
        [Test]
        public void LocalTeamApply_NeverRunsOffline()
        {
            var rig = new RecordingRig { Exists = true, Team = 0 };
            Arrange(NetRole.Offline, rig, TeamId.Team1);

            NetClientLocalCombatDriver.ApplyLocalTeam();

            Assert.AreEqual(0, rig.SetTeamCalls, "the offline body was re-teamed from a snapshot.");
            Assert.AreEqual(0, rig.Team);
        }

        // ------------------------------------------------------------------ D-3: the minimap

        /// <summary>
        /// The minimap draws friendlies and not enemies — the offline game's own rule.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>RemoteActorRegistry</c> marked every live remote actor and
        /// <c>MinimapUi.SetMarker</c> has no team test, so a networked client saw the position of
        /// every hostile inside <c>InterestManager.CullRadius</c>. That is a regression against
        /// <c>ActorBlip.LateUpdate</c>, which has filtered to friendlies since the original game.
        /// </para>
        /// <para>
        /// <b>The <c>IsHighlighted()</c> half of that rule is deliberately absent.</b> Nothing
        /// carries a highlight bit across the wire — <c>ActorSnapshotEntry</c> has no such field
        /// and neither does <c>SpawnActorMessage</c> — so spotting an enemy is already impossible
        /// over the network, for a reason older than this filter. Asserting a disjunct over a
        /// value that can only be false would be a green that proves nothing.
        /// </para>
        /// </remarks>
        [Test]
        public void Minimap_MarksFriendliesAndNotEnemies()
        {
            Assert.IsTrue(RemoteActorRegistry.ShouldMarkOnMinimap(TeamId.Team1, TeamId.Team1));
            Assert.IsTrue(RemoteActorRegistry.ShouldMarkOnMinimap(TeamId.Team0, TeamId.Team0));

            Assert.IsFalse(RemoteActorRegistry.ShouldMarkOnMinimap(TeamId.Team0, TeamId.Team1),
                "an enemy was drawn on the minimap — D-3.");
            Assert.IsFalse(RemoteActorRegistry.ShouldMarkOnMinimap(TeamId.Team1, TeamId.Team0),
                "an enemy was drawn on the minimap — D-3.");
        }

        /// <summary>
        /// An unresolved team on either side marks nothing.
        /// </summary>
        /// <remarks>
        /// The failure directions are not symmetric. Marking everything until the local team
        /// arrives shows exactly the enemy positions the filter exists to hide; marking nothing
        /// costs a blank minimap for the fraction of a second before it does.
        /// <c>MinimapUi.UpdateSpawnPointButtons</c> takes the same branch for the same reason.
        /// </remarks>
        [Test]
        public void Minimap_MarksNothingWhileEitherTeamIsUnknown()
        {
            Assert.IsFalse(RemoteActorRegistry.ShouldMarkOnMinimap(TeamId.Team0, TeamId.None),
                "everything was drawn while this client's own team was still unknown.");
            Assert.IsFalse(RemoteActorRegistry.ShouldMarkOnMinimap(TeamId.None, TeamId.Team0),
                "a body of unknown team was drawn as a friendly.");

            // Two unknowns are EQUAL, which a bare `team == localTeam` would read as friendly.
            // That is the D-1 sentinel trap wearing a different hat, one rule over.
            Assert.IsFalse(RemoteActorRegistry.ShouldMarkOnMinimap(TeamId.None, TeamId.None),
                "two unknown teams compared equal and were drawn as friendlies.");
        }

        // ------------------------------------------------------------------ helpers

        private static void Arrange(NetRole role, ILocalPlayerRig rig, byte team)
        {
            NetContext.SetRole(role);
            NetClientBindings.LocalPlayer = rig;
            NetClientBindings.LocalTeam = (out byte t) => { t = team; return true; };
        }

        private static bool Unresolved(out byte team)
        {
            team = TeamId.None;
            return false;
        }

        /// <summary>
        /// A rig that records what was written to it. <see cref="Exists"/> is settable so one
        /// test can drive both halves of the body-then-team arrival race.
        /// </summary>
        private sealed class RecordingRig : ILocalPlayerRig
        {
            public bool Exists { get; set; } = true;
            public int Team { get; set; } = -1;
            public int SetTeamCalls { get; private set; }

            public void SetTeam(int team)
            {
                Team = team;
                SetTeamCalls++;
            }

            public IInputSource InputSource => null;
            public GameObject GameObject => null;
            public bool IsInputEnabled => false;
            public void SetInputSource(IInputSource source) { }
            public void EnableInput() { }
            public void DisableInput() { }
            public void EnterDeployedView() { }
            public bool ConsumeDeployIntent() => false;
            public bool IsDriving(IGameplayActorPresence actor) => false;
            public Vector3 Position => Vector3.zero;
            public float YawDegrees => 0f;
            public bool CanApplyScreenshake => false;
            public void ApplyScreenshake(float magnitude, int iterations) { }
            public bool HasFellableBody => false;
            public void FellBody(Vector3 force, HumanBodyBones bone) { }
        }
    }
}
