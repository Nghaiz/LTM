using System;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// <see cref="MasterLinkKeepAlive"/>: when a game server dials the master again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect these grade cost a whole afternoon of invisible servers.</b> On 2026-09-14 a
    /// VMware host holding two registered game servers was suspended. The master saw the
    /// heartbeats stop, marked both unhealthy and dropped them — <c>gsRegistered</c> 2 to 0 in
    /// its own durability CSV. On resume both processes went on playing matches with dead
    /// sockets and never dialled again, because registration happened once in <c>Start</c> and
    /// nothing watched it afterwards. Nothing in either log said so: the reporter's
    /// <c>if (!IsConnected) return;</c> guards turn a dead link into silence rather than an
    /// error.
    /// </para>
    /// <para>
    /// <b>Both directions are graded.</b> Retrying too eagerly is its own fault — a server that
    /// dials every frame through an hour of downtime is a denial of service against a master
    /// that is already unwell — so the backoff ceiling and the one-attempt-per-arming rule are
    /// asserted beside the recovery itself.
    /// </para>
    /// </remarks>
    public sealed class MasterLinkKeepAliveTests
    {
        /// <summary>Ages the policy in small steps and reports when it asked to dial.</summary>
        /// <remarks>
        /// Stepped rather than handed one large delta, because a frame is small and the countdown
        /// has to survive being crossed in pieces — the bug this would catch is a comparison that
        /// only fires when a single delta lands past the deadline.
        /// </remarks>
        private static float SecondsUntilAttempt(MasterLinkKeepAlive keepAlive, float budget)
        {
            const float Step = 0.25f;

            for (float elapsed = 0f; elapsed <= budget; elapsed += Step)
            {
                if (keepAlive.ShouldAttempt(Step)) return elapsed;
            }

            return -1f;
        }

        /// <summary>A server that never wanted a link never dials, however long it runs.</summary>
        /// <remarks>
        /// The standalone contingency. A declared client, an unconfigured master and a missing
        /// shared secret all land here, and each is a deliberate decision rather than a fault to
        /// recover from — a retry loop that ignored this would put every rendered client in the
        /// server registry, which is a defect this codebase has already had once.
        /// </remarks>
        [Test]
        public void AStandaloneServerNeverDials()
        {
            var keepAlive = new MasterLinkKeepAlive();

            keepAlive.OnLinkDown();

            Assert.AreEqual(-1f, SecondsUntilAttempt(keepAlive, 120f),
                "a server that never wanted a link asked to dial the master");
        }

        /// <summary>A lost link arms a prompt retry.</summary>
        /// <remarks>
        /// Five seconds rather than a minute because the common case is a master that bounced and
        /// is already back. This is the assertion that would have failed before the fix: nothing
        /// armed anything, and the answer was "never".
        /// </remarks>
        [Test]
        public void ALostLinkIsRetriedWithinTheFloor()
        {
            var keepAlive = new MasterLinkKeepAlive { WantsLink = true };

            keepAlive.OnLinkDown();

            float due = SecondsUntilAttempt(keepAlive, 30f);

            Assert.GreaterOrEqual(due, 4.5f, "the first retry was sooner than the floor");
            Assert.LessOrEqual(due, 5.5f, "the first retry was later than the floor");
        }

        /// <summary>Repeated failures back off, and stop at the ceiling.</summary>
        /// <remarks>
        /// <b>The ceiling is the half that stops this being a hot loop.</b> Doubling without a cap
        /// reaches hours, which is indistinguishable from the bug being fixed; doubling without a
        /// SHIFT cap eventually overflows to a negative multiplier, and a negative delay reads as
        /// "due immediately" — the exact hot loop the backoff exists to prevent, arriving only
        /// after a server has been retrying long enough that nobody is watching.
        /// </remarks>
        [Test]
        public void TheBackoffDoublesAndThenHoldsAtTheCeiling()
        {
            var keepAlive = new MasterLinkKeepAlive { WantsLink = true };
            var seen = new float[6];

            for (int i = 0; i < seen.Length; i++)
            {
                keepAlive.OnLinkDown();
                seen[i] = keepAlive.RetryInSeconds;
            }

            Assert.AreEqual(new[] { 5f, 10f, 20f, 40f, 60f, 60f }, seen);

            for (int i = 0; i < 40; i++) keepAlive.OnLinkDown();

            Assert.AreEqual(60f, keepAlive.RetryInSeconds,
                "the backoff left the ceiling after many failures");
        }

        /// <summary>A registration that lands clears the backoff.</summary>
        /// <remarks>
        /// So the delay measures CONSECUTIVE failures. A link that flaps once an hour would
        /// otherwise creep to the ceiling over a long-lived process and take a full minute to
        /// recover from a blip it used to shrug off in five seconds.
        /// </remarks>
        [Test]
        public void ASuccessfulRegistrationForgetsTheBackoff()
        {
            var keepAlive = new MasterLinkKeepAlive { WantsLink = true };

            for (int i = 0; i < 5; i++) keepAlive.OnLinkDown();
            Assert.AreEqual(60f, keepAlive.RetryInSeconds, "precondition: at the ceiling");

            keepAlive.OnRegistered();

            Assert.AreEqual(0, keepAlive.Attempt);
            Assert.AreEqual(-1f, SecondsUntilAttempt(keepAlive, 120f),
                "a registered server kept asking to dial");

            keepAlive.OnLinkDown();
            Assert.AreEqual(5f, keepAlive.RetryInSeconds, "the next loss did not start from the floor");
        }

        /// <summary>One arming produces one attempt, however long the caller keeps ticking.</summary>
        /// <remarks>
        /// The component starts an async connect on a true here and cannot start a second while
        /// the first is in flight. A countdown that stayed past zero would ask on every frame,
        /// which is the same hot loop the ceiling defends against and is reached far sooner.
        /// </remarks>
        [Test]
        public void AnArmedRetryFiresExactlyOnce()
        {
            var keepAlive = new MasterLinkKeepAlive { WantsLink = true };
            keepAlive.OnLinkDown();

            int fired = 0;
            for (int i = 0; i < 400; i++)
            {
                if (keepAlive.ShouldAttempt(0.25f)) fired++;
            }

            Assert.AreEqual(1, fired, "the armed retry fired more than once");
        }

        /// <summary>The constructor refuses a range that cannot produce a sane delay.</summary>
        /// <remarks>
        /// A zero floor is an infinite retry loop and a ceiling under the floor silently inverts
        /// the backoff, so both are refused where they are written rather than diagnosed later
        /// from a log full of connection attempts.
        /// </remarks>
        [Test]
        public void AnImpossibleRetryRangeIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MasterLinkKeepAlive(0f, 60f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MasterLinkKeepAlive(30f, 10f));
        }
    }
}
