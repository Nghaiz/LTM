using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// The three phase-3A pins: a second connection gets a body, the pool never exceeds the
    /// transport's capacity, and the claimable count is the number the server advertises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These assert the healthy state, not a baseline.</b> Per
    /// <c>pinned-baseline-test-companion.md</c> nothing here pins a currently-broken number to
    /// keep the suite green; each one goes RED when the defect it names comes back, and the
    /// mutation that proved it does so is recorded in the phase report.
    /// </para>
    /// <para>
    /// <b>There was no coverage of <c>TryClaimPlayerSlot</c> at all before this file.</b>
    /// <c>grep -rln "TryClaimPlayerSlot"</c> returned its own definition,
    /// <c>ServerTickLoop</c> and the editor harness — so a server that admitted exactly one
    /// player, and said sixteen in its startup log, was a green suite for the whole of phases
    /// 1 and 2.
    /// </para>
    /// <para>
    /// <b>The factory builds bare rigs, not prefabs.</b> The production factory reaches
    /// <c>ActorManager.actorPrefab</c> through <c>NetServerBindings.PlayerBodyFactory</c>, which
    /// needs a scene, a game and an Editor in play mode. What the pool actually does — count,
    /// headroom, roll back, mark claimable — is independent of what a body is made of, which is
    /// exactly why <c>Fill</c> takes a delegate.
    /// </para>
    /// </remarks>
    public sealed class ServerPlayerSlotPoolTests
    {
        private const int MaxConnections = 16;

        private ServerPlayerSlotPool _pool;
        private ServerActorRegistry _registry;
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            // A private registry, not ServerActorRegistry.Instance: the singleton is process-wide
            // and any other EditMode test that registered an actor would land in this one's
            // headroom arithmetic. Fill() takes one for exactly this reason.
            _registry = new ServerActorRegistry();
            _pool = new ServerPlayerSlotPool();

        }

        [TearDown]
        public void TearDown()
        {
            _pool.Clear();

            // Anything the fake factory made that the pool never took ownership of. DestroyImmediate
            // because an EditMode test has no frame boundary for a deferred destroy to land on.
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();
            NetServerBindings.Clear();
        }

        /// <summary>
        /// A bare replicated body: a GameObject and a <see cref="NetServerActor"/>, registered
        /// the way <c>OnEnable</c> registers one.
        /// </summary>
        private NetServerActor CreateBody(byte team)
        {
            var go = new GameObject($"slot body t{team}");
            _spawned.Add(go);

            // Deactivated BEFORE the component is added, and that ordering is the point.
            // OnEnable fires on AddComponent even outside play mode, and it registers into the
            // process-wide SINGLETON -- not the registry under test. Sixty bodies leaking into
            // it would push the singleton past MAX_ACTORS and make an unrelated suite start
            // logging refusals. TryClaimPlayerSlot does not consult isActiveAndEnabled, so an
            // inactive rig is claimable exactly like a live one.
            go.SetActive(false);

            var actor = go.AddComponent<NetServerActor>();
            actor.Team = team;

            _registry.Register(actor);
            return actor;
        }

        // ------------------------------------------------------------------ pin 1

        /// <summary>
        /// Pin 1 — a second connection claims a slot.
        /// </summary>
        /// <remarks>
        /// Goes RED when the pool holds fewer than two bodies. Before phase-3A this was the
        /// shipped behaviour: one claimable body existed in the project, so
        /// <c>ServerTickLoop.OnClientConnected</c> answered connection two with
        /// <c>DisconnectReason.ServerFull</c> — a byte the client reads back verbatim.
        /// </remarks>
        [Test]
        public void SecondConnection_ClaimsASlot()
        {
            Assert.IsTrue(_pool.Fill(MaxConnections, CreateBody, _registry), "pool did not fill");

            Assert.IsTrue(
                _registry.TryClaimPlayerSlot(0, out NetServerActor first),
                "connection 1 found no free player slot");

            Assert.IsTrue(
                _registry.TryClaimPlayerSlot(1, out NetServerActor second),
                "connection 2 found no free player slot — this is the phase-3A defect, back");

            Assert.AreNotSame(first, second, "both connections were handed the same body");
            Assert.IsTrue(first.IsClaimed && second.IsClaimed);
        }

        /// <summary>
        /// Every admitted connection gets its own body, and the one after that does not.
        /// </summary>
        /// <remarks>
        /// The other half of pin 1. Two is the number that catches the shipped defect; N is the
        /// number that catches a pool sized off anything other than <c>MaxConnections</c>, and
        /// N+1 is what makes <c>ServerFull</c> mean what it says rather than mean "we only ever
        /// had one".
        /// </remarks>
        [Test]
        public void EveryAdmittedConnection_GetsABody_AndTheNextIsRefused()
        {
            Assert.IsTrue(_pool.Fill(MaxConnections, CreateBody, _registry), "pool did not fill");

            var claimed = new HashSet<ushort>();

            // Alternating sides, because that is what a balanced lobby sends and because a
            // team-keyed claim only reaches capacity when both sides are asked for. All-zero
            // would fill 8 and then refuse with 8 bodies standing idle -- correct behaviour,
            // but it would not exercise the capacity this pin is about.
            for (int i = 0; i < MaxConnections; i++)
            {
                Assert.IsTrue(
                    _registry.TryClaimPlayerSlot((byte)(i % 2), out NetServerActor actor),
                    $"connection {i + 1} of {MaxConnections} found no free player slot");

                Assert.IsTrue(claimed.Add(actor.ActorId), $"actor {actor.ActorId} handed out twice");
            }

            Assert.IsFalse(
                _registry.TryClaimPlayerSlot(0, out NetServerActor _),
                $"connection {MaxConnections + 1} was admitted; the pool exceeds transport capacity");
            Assert.IsFalse(
                _registry.TryClaimPlayerSlot(1, out NetServerActor _),
                "the other side was not full either; the pool exceeds transport capacity");
            Assert.IsFalse(
                _registry.HasFreePlayerSlotOnAnyTeam(),
                "a full pool still reported a free body somewhere");
        }

        // ------------------------------------------------------------------ pin 2

        /// <summary>
        /// Pin 2 — the pool never exceeds what the registry can hold, and never short-spawns.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Goes RED if the pool ever creates part of a request. <c>Register</c> refuses at
        /// <see cref="ProtocolConstants.MAX_ACTORS"/> and returns silently, so a pool that
        /// spawned first and counted afterwards would end up smaller than the number the
        /// startup log prints — the original 16-vs-1 defect, re-created one layer up.
        /// </para>
        /// <para>
        /// Asserted through <see cref="ServerPlayerSlotPool.SlotCount"/> being exactly zero,
        /// not merely "less than asked for": rolling back is the contract, not clamping.
        /// </para>
        /// </remarks>
        [Test]
        public void PoolLargerThanTheRegistryCanHold_CreatesNothing()
        {
            // Expect, not ignore: "fails loudly" is half the contract, and an EditMode test
            // fails on an unmatched error anyway -- so this asserts the refusal was REPORTED as
            // well as obeyed. A silent refusal would leave an operator with a server that admits
            // nobody and says nothing about why.
            LogAssert.Expect(LogType.Error, new Regex("player slots will not fit"));

            Assert.IsFalse(
                _pool.Fill(ProtocolConstants.MAX_ACTORS + 1, CreateBody, _registry),
                "an over-capacity request reported success");

            Assert.AreEqual(0, _pool.SlotCount, "an over-capacity request left bodies behind");
            Assert.AreEqual(0, _registry.ClaimableCount, "an over-capacity request registered bodies");
        }

        /// <summary>Existing actors count against the headroom, not just the request size.</summary>
        /// <remarks>
        /// The bots are already in the registry when the server starts. A check that only
        /// compared the request against <c>MAX_ACTORS</c> would pass a 16-slot pool onto a map
        /// holding 60 bots and then lose four of them inside <c>Register</c>, silently.
        /// </remarks>
        [Test]
        public void ExistingActors_CountAgainstTheHeadroom()
        {
            for (int i = 0; i < ProtocolConstants.MAX_ACTORS - 4; i++) CreateBody(0);

            LogAssert.Expect(LogType.Error, new Regex("player slots will not fit"));

            Assert.IsFalse(
                _pool.Fill(MaxConnections, CreateBody, _registry),
                "16 slots were accepted with only 4 registry slots left");

            Assert.AreEqual(0, _pool.SlotCount, "a refused fill left bodies behind");
        }

        /// <summary>A factory that fails part-way leaves nothing standing.</summary>
        [Test]
        public void FactoryFailingPartWay_RollsBack()
        {
            int built = 0;

            LogAssert.Expect(LogType.Error, new Regex("factory returned nothing for slot"));

            Assert.IsFalse(
                _pool.Fill(MaxConnections, team => ++built <= 3 ? CreateBody(team) : null, _registry),
                "a failed factory reported success");

            Assert.AreEqual(0, _pool.SlotCount, "the three bodies built before the failure survived");
            Assert.AreEqual(0, _registry.ClaimableCount, "a rolled-back fill left claimable bodies");
        }

        // ------------------------------------------------------------------ pin 3

        /// <summary>
        /// Pin 3 — the claimable-body count equals the admitted-connection count.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one that stops the startup log advertising capacity the world does not
        /// have. <c>NetServerBootstrap</c> printed <c>Config.MaxConnections</c> — sixteen —
        /// beside a registry holding one claimable body, and nothing anywhere compared them.
        /// The bootstrap now reads <c>ServerActorRegistry.ClaimableCount</c> back and errors on
        /// a mismatch; this pin is the same comparison, in the suite.
        /// </para>
        /// <para>
        /// Goes RED when the two numbers diverge in either direction — a pool sized off a
        /// literal, or <c>_maxConnections</c> changed without the pool following.
        /// </para>
        /// </remarks>
        [Test]
        public void ClaimableCount_EqualsMaxConnections()
        {
            Assert.IsTrue(_pool.Fill(MaxConnections, CreateBody, _registry), "pool did not fill");

            Assert.AreEqual(
                MaxConnections, _registry.ClaimableCount,
                "the claimable-body count and the admitted-connection count disagree — this is "
                + "the 16-vs-1 defect, and the startup log is about to print the wrong one");

            Assert.AreEqual(MaxConnections, _pool.SlotCount);
        }

        /// <summary>The count follows the configured number rather than a literal.</summary>
        /// <remarks>
        /// Pin 3's companion. The assertion above would still pass if the pool ignored its
        /// argument and always built 16 — which is precisely how the original defect was
        /// authored. Driving two different sizes through the same call is what makes the pin
        /// mean "follows MaxConnections" instead of "happens to be 16".
        /// </remarks>
        [Test]
        public void ClaimableCount_FollowsTheConfiguredNumber_NotALiteral()
        {
            const int Configured = 5;

            Assert.IsTrue(_pool.Fill(Configured, CreateBody, _registry), "pool did not fill");

            Assert.AreEqual(Configured, _registry.ClaimableCount, "the pool ignored its slot count");
            Assert.AreEqual(Configured, _pool.SlotCount);
        }

        // ------------------------------------------------------------------ claim lifecycle

        /// <summary>
        /// Claiming a body stops the bot brain steering it; releasing hands it back.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The mechanism <c>NetVerificationHarness</c> discovered by hand, now on the shipped
        /// claim path. Server movement for a claimed body runs through <c>ServerPlayer</c> and
        /// <c>NetMovementAgent</c>; an AI still driving the same <c>CharacterController</c> is a
        /// second writer to one position and the client is predicting against only one of them.
        /// </para>
        /// <para>
        /// The resume half is not symmetry for its own sake: a slot is reused across a match,
        /// and without it every disconnect would leave one more inert mannequin standing in the
        /// map for the rest of the round.
        /// </para>
        /// </remarks>
        [Test]
        public void ClaimSuspendsTheBotBrain_AndReleaseResumesIt()
        {
            NetServerActor actor = CreateBody(0);
            actor.MarkAvailableForPlayers();

            var driver = new FakeAiDriver();
            actor.BindAiDriver(driver);

            Assert.IsTrue(_registry.TryClaimPlayerSlot(0, out NetServerActor claimed));
            Assert.AreSame(actor, claimed);
            Assert.AreEqual(1, driver.Suspends, "claiming a body left its AI driving");

            _registry.ReleaseSlot(claimed);
            Assert.AreEqual(1, driver.Resumes, "releasing a body left it an inert mannequin");
        }

        /// <summary>
        /// A pool body's bot brain is parked from creation, not from the claim. P12 <b>D-4</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>ServerPlayerSlotPool.Fill</c> builds <c>Config.MaxConnections</c> bodies at Start
        /// and AI was suspended only on <c>Claim()</c>, so at 16 slots with two humans fourteen
        /// extra AI-driven, shootable, scoring bodies stood on top of the map's authored 20/20 —
        /// split 7/7 by the pool's own <c>i % 2</c>. A 1v1 was really a 21v21.
        /// </para>
        /// <para>
        /// <b>Worse than merely surplus, which is why the count is not the only assertion worth
        /// making.</b> X-18 holds an unclaimed slot out of both the announce and the snapshot, so
        /// those fourteen were invisible to every client while still shooting at it. No client
        /// could have reported them and no screenshot could have shown them.
        /// </para>
        /// </remarks>
        [Test]
        public void PoolBodies_AreParkedFromCreationRatherThanFromTheClaim()
        {
            var driver = new FakeAiDriver();
            NetServerActor actor = CreateBody(0);
            actor.BindAiDriver(driver);

            Assert.AreEqual(0, driver.Suspends, "nothing should park a body before it is a slot.");

            actor.MarkAvailableForPlayers();

            Assert.AreEqual(1, driver.Suspends,
                "an unclaimed player slot was left AI-driven — D-4.");
            Assert.AreEqual(0, driver.Resumes);
        }

        /// <summary>
        /// Claiming an already-parked slot does not re-park it, and releasing still resumes.
        /// </summary>
        /// <remarks>
        /// The lifecycle P12 changes is only the state a slot STARTS in. <c>Release</c> still
        /// hands the body back to the bots, for the reason <c>IAiDriver.Resume</c> gives: a slot
        /// is reused across a match, and without it every disconnect would leave one more inert
        /// mannequin standing in the map. Pinning the call COUNTS is what makes that a decision
        /// rather than an accident of ordering — a re-park on claim would be a redundant
        /// <c>enabled</c> write on every join.
        /// </remarks>
        [Test]
        public void ParkedSlot_IsNotReparkedOnClaimAndStillResumesOnRelease()
        {
            var driver = new FakeAiDriver();
            NetServerActor actor = CreateBody(0);
            actor.BindAiDriver(driver);
            actor.MarkAvailableForPlayers();

            Assert.IsTrue(_registry.TryClaimPlayerSlot(0, out NetServerActor claimed));
            Assert.AreSame(actor, claimed);
            Assert.AreEqual(1, driver.Suspends, "claiming re-parked an already-parked slot.");

            _registry.ReleaseSlot(claimed);
            Assert.AreEqual(1, driver.Resumes, "releasing a body left it an inert mannequin");
        }

        /// <summary>A body with no bot brain claims without incident.</summary>
        /// <remarks>
        /// Null is a real answer, not a failure: the local player's avatar and every bare test
        /// rig have no <c>AiActorController</c>. A claim path that assumed one would throw on
        /// the first connection to a listen server.
        /// </remarks>
        [Test]
        public void BodyWithNoBotBrain_ClaimsWithoutThrowing()
        {
            NetServerActor actor = CreateBody(0);
            actor.MarkAvailableForPlayers();

            Assert.IsTrue(_registry.TryClaimPlayerSlot(0, out NetServerActor claimed));
            Assert.AreSame(actor, claimed);
            Assert.IsTrue(claimed.IsClaimed);
        }

        private sealed class FakeAiDriver : IAiDriver
        {
            internal int Suspends;
            internal int Resumes;

            public bool Exists => true;

            public void Suspend() => Suspends++;

            public void Resume() => Resumes++;
        }
        // --------------------------------------------------------------- P13: claim by team

        /// <summary>
        /// The side the ticket names is the side the body is on. Criterion 4's unit half.
        /// </summary>
        /// <remarks>
        /// Goes RED the moment <c>TryClaimPlayerSlot</c> stops consulting the team — which is
        /// what it did for four phases: a first-fit walk took the lowest free index and the
        /// side was whatever parity that index happened to have, so the lobby balanced teams
        /// and the answer was thrown away at the door.
        /// </remarks>
        [Test]
        public void EveryClaim_LandsOnTheTeamItAskedFor()
        {
            Assert.IsTrue(_pool.Fill(MaxConnections, CreateBody, _registry), "pool did not fill");

            for (int i = 0; i < MaxConnections; i++)
            {
                byte want = (byte)(i % 2);
                Assert.IsTrue(
                    _registry.TryClaimPlayerSlot(want, out NetServerActor actor),
                    $"connection {i + 1} found no free body on team {want}");

                Assert.AreEqual(
                    want, actor.Team,
                    $"connection {i + 1} asked for team {want} and was given team {actor.Team}");
            }
        }

        /// <summary>
        /// Everyone asking for the same side fills that side and then stops — with the other
        /// side still empty. Criterion 6's unit half.
        /// </summary>
        /// <remarks>
        /// <b>This is the intended behaviour, not a defect</b>, and it is exactly why
        /// <c>ConnectDenyReason.TeamFull</c> and <c>DisconnectReason.TeamFull</c> exist. A
        /// server refusing a player with eight empty bodies standing on the other side must
        /// say which of the two facts it is, because only one of them has a remedy.
        /// </remarks>
        [Test]
        public void OneSideFillsAtHalfCapacity_AndTheServerIsNotFull()
        {
            Assert.IsTrue(_pool.Fill(MaxConnections, CreateBody, _registry), "pool did not fill");

            int perSide = MaxConnections / 2;

            for (int i = 0; i < perSide; i++)
            {
                Assert.IsTrue(
                    _registry.TryClaimPlayerSlot(0, out NetServerActor _),
                    $"team 0 joiner {i + 1} of {perSide} found no body");
            }

            Assert.IsFalse(
                _registry.TryClaimPlayerSlot(0, out NetServerActor _),
                $"team 0 admitted a {perSide + 1}th player; the pool holds {perSide} a side");

            // The distinction the player is owed: this refusal is NOT "the server is full".
            Assert.IsTrue(
                _registry.HasFreePlayerSlotOnAnyTeam(),
                "a full side was reported as a full server — TeamFull would render as "
                + "ServerFull and the player would be told a remediless lie");

            Assert.IsTrue(
                _registry.TryClaimPlayerSlot(1, out NetServerActor other),
                "the empty side refused a joiner");
            Assert.AreEqual(1, other.Team);
        }

        /// <summary>
        /// The strand: a departure in the middle of the live set, then a joiner the lobby put
        /// on the empty side. Criterion 5, and the server audit's ranked finding #2.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The mechanism.</b> <c>Release()</c> frees a body at its own index while a
        /// first-fit refill takes the LOWEST free one, so occupancy is a prefix only until
        /// somebody in the middle leaves. Once it is not a prefix, the parity of the lowest
        /// free index stops agreeing with the side the lobby chose — and a first-fit claim
        /// hands the joiner a body of the wrong team with nothing to say so.
        /// </para>
        /// <para>
        /// <b>This sequence needs TWO departures, and § 1.1's own one-departure example does
        /// not discriminate.</b> Exhaustively simulating both strategies (2–8 joins, every
        /// departure slot, 0–3 further joins) yields ZERO single-departure sequences where
        /// first-fit and a team-keyed claim end up with different live teams: for
        /// {0,1,2} then slot 1 leaving, BOTH leave two players on team 0 and BOTH give the
        /// fourth joiner a team-1 body. The 2v0 state § 1.1 names is reached under this fix
        /// too — correcting it would mean moving a player already in the match, which § 6 of
        /// the same phase puts out of scope. What the fix does close is the sequence below.
        /// </para>
        /// <para>
        /// <b>The mutation that proves it fails.</b> Drop the <c>candidate.Team != team</c>
        /// guard from <c>TryClaimPlayerSlot</c> and the final assertion goes RED with two
        /// players on team 0 and nobody opposing them.
        /// </para>
        /// </remarks>
        [Test]
        public void ADepartureInTheMiddle_DoesNotStrandTheNextJoinerOnTheWrongSide()
        {
            Assert.IsTrue(_pool.Fill(MaxConnections, CreateBody, _registry), "pool did not fill");

            // Three joiners, teams as the lobby's balancer hands them out: 0, then 1, then 0
            // (it breaks a 1-1 tie towards team 0).
            Assert.IsTrue(_registry.TryClaimPlayerSlot(0, out NetServerActor first));
            Assert.IsTrue(_registry.TryClaimPlayerSlot(1, out NetServerActor second));
            Assert.IsTrue(_registry.TryClaimPlayerSlot(0, out NetServerActor third));

            Assert.AreEqual(0, first.Team);
            Assert.AreEqual(1, second.Team, "the second joiner did not land on team 1");
            Assert.AreEqual(0, third.Team);

            // The first two leave. The live set is now a HOLE followed by one player, which is
            // the state a prefix-assuming refill gets wrong.
            _registry.ReleaseSlot(first);
            _registry.ReleaseSlot(second);
            Assert.IsFalse(first.IsClaimed, "Release left the body claimed");
            Assert.IsFalse(second.IsClaimed, "Release left the body claimed");

            // One player remains, on team 0. The lobby therefore puts the next joiner on
            // team 1 — the only assignment that gives them somebody to fight.
            Assert.IsTrue(third.IsClaimed);
            Assert.AreEqual(0, third.Team);

            Assert.IsTrue(
                _registry.TryClaimPlayerSlot(1, out NetServerActor fourth),
                "the fourth joiner found no body on team 1, though one was just released");

            Assert.AreEqual(
                1, fourth.Team,
                "the fourth joiner was handed a team 0 body: both humans are now on one side "
                + "with nobody opposing them. This is the strand, and it is what a first-fit "
                + "claim does here — it takes the lowest free index, which is team 0's");

            Assert.AreSame(
                second, fourth,
                "the released team-1 body was not the one reused; Release must free in place "
                + "so the body returning to the pool is still the right side's body");

            // One human per side, which is the whole point of carrying the lobby's answer.
            Assert.AreEqual(0, third.Team);
            Assert.AreEqual(1, fourth.Team);
        }
    }
}
