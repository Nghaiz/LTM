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
                _registry.TryClaimPlayerSlot(out NetServerActor first),
                "connection 1 found no free player slot");

            Assert.IsTrue(
                _registry.TryClaimPlayerSlot(out NetServerActor second),
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

            for (int i = 0; i < MaxConnections; i++)
            {
                Assert.IsTrue(
                    _registry.TryClaimPlayerSlot(out NetServerActor actor),
                    $"connection {i + 1} of {MaxConnections} found no free player slot");

                Assert.IsTrue(claimed.Add(actor.ActorId), $"actor {actor.ActorId} handed out twice");
            }

            Assert.IsFalse(
                _registry.TryClaimPlayerSlot(out NetServerActor _),
                $"connection {MaxConnections + 1} was admitted; the pool exceeds transport capacity");
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

            Assert.IsTrue(_registry.TryClaimPlayerSlot(out NetServerActor claimed));
            Assert.AreSame(actor, claimed);
            Assert.AreEqual(1, driver.Suspends, "claiming a body left its AI driving");

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

            Assert.IsTrue(_registry.TryClaimPlayerSlot(out NetServerActor claimed));
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
    }
}
