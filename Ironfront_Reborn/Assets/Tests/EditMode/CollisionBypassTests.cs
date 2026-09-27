using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ironfront.Net.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins that <c>NetMovementAgent.CharacterMove</c> reports a move it could not resolve
    /// through collision, instead of writing it onto the transform in silence. X-19.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The uncollided branch is legitimate and must stay.</b> A body whose controller is
    /// missing still has to go somewhere, and refusing to move it would strand a spawning actor
    /// rather than fix anything. What was wrong was that the branch was indistinguishable from a
    /// real move: it returned <c>transform.position</c> exactly as the collided path does, left
    /// <c>LastCollisionFlags</c> holding whatever the previous real move had put there, and said
    /// nothing. Eleven thousand of them in one lane-B run produced a client body a third of a
    /// metre below the server's and no artifact anywhere named the cause.
    /// </para>
    /// <para>
    /// <b>Three faults are claimed, so there are three tests.</b> The counter must not move on a
    /// healthy body, or it is noise nobody will read; it must move on a disabled controller,
    /// which is the state X-19 measured; and the flags must be cleared rather than left stale,
    /// because a caller reading <c>Below</c> off a move that never touched the collision system
    /// concludes the actor is standing on something.
    /// </para>
    /// <para>
    /// <b>Not a test of the fix, a test of the fault.</b> The fix for X-19 is in
    /// <c>ClientPredictionStage</c>, which compiles into <c>Assembly-CSharp</c> and is therefore
    /// unreachable from any test assembly (ledger <b>E-11b</b>). What IS reachable is the
    /// detector the fix is graded by: these tests pin the counter, and the lane-B run pins that
    /// it reads zero. A green here with a non-zero counter in the artifact would mean the fix
    /// regressed, which is the whole point of counting rather than logging.
    /// </para>
    /// </remarks>
    public sealed class CollisionBypassTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
            {
                if (go != null) Object.DestroyImmediate(go);
            }

            _spawned.Clear();
        }

        /// <summary>
        /// A body carrying the agent, with its controller in the requested state. <c>Awake</c>
        /// has already run: <c>AddComponent</c> runs it synchronously, and the agent caches
        /// <c>GetComponent&lt;CharacterController&gt;()</c> there.
        /// </summary>
        /// <remarks>
        /// The controller is added FIRST, before the agent: <c>NetMovementAgent.Awake</c> caches
        /// <c>GetComponent&lt;CharacterController&gt;()</c> and reads <c>isGrounded</c> off it
        /// unguarded, so attaching the agent to a bare GameObject would have Unity's
        /// <c>[RequireComponent]</c> supply one anyway and the ordering would be luck rather
        /// than intent.
        /// </remarks>
        private NetMovementAgent NewBody(bool controllerEnabled)
        {
            var go = new GameObject("body");
            _spawned.Add(go);

            CharacterController controller = go.AddComponent<CharacterController>();
            controller.enabled = controllerEnabled;

            return go.AddComponent<NetMovementAgent>();
        }

        /// <summary>
        /// A floor directly under <paramref name="body"/>, so a downward move has something to
        /// collide WITH.
        /// </summary>
        /// <remarks>
        /// Without it the stale-flags test is vacuous: a move into empty space returns
        /// <c>CollisionFlags.None</c>, so asserting None afterwards would pass whether or not
        /// the production code clears anything. The point of the test is that a REAL move left
        /// <c>Below</c> behind and the bypassed one wiped it.
        /// </remarks>
        private void GiveItAFloor(NetMovementAgent body)
        {
            var floor = new GameObject("floor");
            _spawned.Add(floor);

            floor.AddComponent<BoxCollider>().size = new Vector3(50f, 1f, 50f);
            floor.transform.position = body.transform.position + Vector3.down * 1.5f;
        }

        [Test]
        public void AMoveThroughALiveControllerIsNotCountedAsBypassed()
        {
            NetMovementAgent agent = NewBody(controllerEnabled: true);

            agent.CharacterMove(new Vector3(0f, -0.3322f, 0f));

            Assert.AreEqual(
                0L, agent.CollisionBypassedMoves,
                "A move that went through an enabled CharacterController was counted as "
                + "bypassed. The counter is the X-19 detector and a run is graded on it reading "
                + "zero; a false positive here makes that grade meaningless.");
        }

        [Test]
        public void AMoveThroughADisabledControllerIsCountedAndReportedOnce()
        {
            NetMovementAgent agent = NewBody(controllerEnabled: false);

            LogAssert.Expect(LogType.Error, new Regex("CharacterController is disabled"));

            agent.CharacterMove(new Vector3(0f, -0.3322f, 0f));
            agent.CharacterMove(new Vector3(0f, -0.3453f, 0f));

            Assert.AreEqual(
                2L, agent.CollisionBypassedMoves,
                "Two moves fell through the uncollided branch and the counter did not follow "
                + "them. This is the exact condition measured on all three lane-B clients for "
                + "11,785 consecutive ticks (X-19); if it does not raise the counter, nothing "
                + "reports it and the next occurrence is as invisible as the first.");

            // Exactly one error for two bypasses: at 30 Hz a per-tick error buries the log it is
            // trying to write, so LogAssert expecting a single entry is itself an assertion.
        }

        // The OTHER arm of the null-or-disabled predicate -- no CharacterController at all --
        // has no test, and deliberately not for lack of trying. NetMovementAgent declares
        // [RequireComponent(typeof(CharacterController))], so Unity adds one the instant the
        // agent is attached and REFUSES DestroyImmediate on it afterwards ("can't remove
        // because NetMovementAgent depends on it"). The state is therefore unreachable from a
        // test and very nearly unreachable at runtime: the null arm is a defensive guard, and
        // the disabled arm is the one X-19 actually walked 11,785 times.

        [Test]
        public void ABypassedMoveDoesNotLeaveStaleCollisionFlags()
        {
            NetMovementAgent agent = NewBody(controllerEnabled: true);
            GiveItAFloor(agent);

            // A real move onto the floor first, so LastCollisionFlags holds something to go
            // stale. Asserted, not assumed: if this landed in empty space the flags would
            // already be None and everything below would pass for the wrong reason.
            agent.CharacterMove(new Vector3(0f, -1f, 0f));
            Assert.AreNotEqual(
                CollisionFlags.None, agent.LastCollisionFlags,
                "Setup did not produce a collided move, so this test cannot tell a cleared flag "
                + "from a flag that was never set. Fix the floor, do not weaken the assert.");

            agent.GetComponent<CharacterController>().enabled = false;
            LogAssert.Expect(LogType.Error, new Regex("CharacterController is disabled"));

            agent.CharacterMove(new Vector3(0f, -0.3322f, 0f));

            Assert.AreEqual(
                CollisionFlags.None, agent.LastCollisionFlags,
                "A move that never reached the collision system left the previous move's flags "
                + "in place. A caller reading Below off those concludes the actor is standing "
                + "on something it has not touched since the controller went away.");
        }

        /// <summary>
        /// A switched-off controller keeps answering the <c>isGrounded</c> of its last move.
        /// </summary>
        /// <remarks>
        /// Measured 2026-09-27 on Island: a pooled server body with its capsule off sank at a
        /// constant 0.333 m per tick -- the grounded stick speed, not a fall -- because the
        /// simulation was told "grounded" every tick, and <c>FallDiagnostics</c>, which arms only
        /// on an ungrounded descent, printed nothing for four consecutive falls through the map.
        /// </remarks>
        [Test]
        public void ADisabledControllerIsNotGrounded()
        {
            NetMovementAgent agent = NewBody(controllerEnabled: true);
            GiveItAFloor(agent);

            agent.CharacterMove(new Vector3(0f, -1f, 0f));
            Assert.IsTrue(
                agent.IsGrounded,
                "Setup did not land the body on its floor, so this test cannot tell a stale "
                + "grounded flag from one that was never set. Fix the floor, do not weaken this.");

            agent.GetComponent<CharacterController>().enabled = false;

            Assert.IsFalse(
                agent.IsGrounded,
                "A body whose capsule is out of the physics world still reports standing on the "
                + "ground. The simulation then holds it at the grounded stick speed straight "
                + "through the map, and the fall diagnostic never arms.");
        }

        /// <summary>
        /// The 2026-09-27 Island fall, reduced to its two steps: a body left seated by one
        /// connection, and a fresh <see cref="ServerPlayer"/> that takes it over on foot.
        /// </summary>
        /// <remarks>
        /// Player 25 timed out while seated in room 22, so <c>SetSeated(true)</c> had run on
        /// actor 2's pooled body and the <see cref="ServerPlayer"/> that would have undone it was
        /// discarded with the connection. Room 23 gave the same body to a new
        /// <see cref="ServerPlayer"/>, whose own seated flag was false, so it never switched the
        /// capsule back on: every respawn sank through the map and drowned.
        /// </remarks>
        [Test]
        public void ACapsuleLeftOffByAPreviousOccupantIsSwitchedBackOn()
        {
            var go = new GameObject("pooled body");
            _spawned.Add(go);
            go.AddComponent<CharacterController>();
            var body = go.AddComponent<NetServerActor>();
            NetMovementAgent agent = body.AttachMovementAgent();

            agent.SetSeated(true);
            Assert.IsFalse(agent.CollisionEnabled, "Setup did not take the capsule out of the world.");

            var nextOccupant = new ServerPlayer(connectionId: 3, actorId: 2) { Actor = body };

            LogAssert.Expect(LogType.Warning, new Regex("capsule was switched off"));
            nextOccupant.Tick(1f / 30f);

            Assert.IsTrue(
                agent.CollisionEnabled,
                "A living, unseated body kept the capsule a previous occupant switched off. Its "
                + "moves bypass collision and it sinks through the map on every respawn.");
            Assert.AreEqual(
                0L, agent.CollisionBypassedMoves,
                "The first on-foot tick moved without collision: the capsule must be back BEFORE "
                + "the tick's move, not after it.");
        }
    }
}
