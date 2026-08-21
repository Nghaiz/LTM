using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins which actors <c>ServerTickLoop.AnnounceNewActors</c> will tell a client about. X-18.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule exists because an unclaimed slot body has a position that is a lie.</b>
    /// <c>ServerPlayerSlotPool</c> fills sixteen bodies at startup and
    /// <c>IronfrontNetBindings.CreatePlayerBody</c> Instantiates them with no position, so they
    /// sit on the prefab's authored spot near (0, 1000, 0) until a join runs
    /// <c>MoveToSpawnPoint</c>. <c>SpawnAckTracker.MarkSpawnSent</c> fires once per pair, so
    /// announcing one before it is claimed sends a wrong position that is never corrected.
    /// </para>
    /// <para>
    /// <b>Both directions, because either half alone is a different bug.</b> Announcing too
    /// early is X-18. Announcing too LATE — a bot, or a slot that has been claimed — makes an
    /// actor that is really there invisible to everyone, which is strictly worse and would not
    /// be caught by a test that only checked the skip.
    /// </para>
    /// </remarks>
    public sealed class AnnounceableActorTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);

            _spawned.Clear();
        }

        /// <summary>
        /// A bare replicated body, deactivated before the component is added.
        /// </summary>
        /// <remarks>
        /// Same ordering as <c>ServerPlayerSlotPoolTests.CreateBody</c> and for the same reason:
        /// <c>OnEnable</c> registers into the process-wide singleton registry even outside play
        /// mode, and bodies leaking into it push an unrelated suite past MAX_ACTORS.
        /// </remarks>
        private NetServerActor CreateBody(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            go.SetActive(false);
            return go.AddComponent<NetServerActor>();
        }

        [Test]
        public void AnUnclaimedPlayerSlotIsNotAnnounced()
        {
            NetServerActor slot = CreateBody("unclaimed slot");
            slot.MarkAvailableForPlayers();

            Assert.IsFalse(
                ServerTickLoop.IsAnnounceable(slot),
                "an unclaimed slot body is parked on the prefab's spot, so announcing it sends a "
                + "position that is wrong on arrival and is never re-sent (X-18).");
        }

        [Test]
        public void AClaimedPlayerSlotIsAnnounced()
        {
            NetServerActor slot = CreateBody("claimed slot");
            slot.MarkAvailableForPlayers();
            slot.Claim();

            Assert.IsTrue(
                ServerTickLoop.IsAnnounceable(slot),
                "a claimed slot has been through MoveToSpawnPoint, so its position is true and "
                + "the client needs it. Skipping this one would make every player invisible.");
        }

        /// <summary>
        /// A bot never goes through the slot pool, so the claim state must not gate it.
        /// </summary>
        /// <remarks>
        /// <c>AvailableForPlayers</c> is set only by <c>ServerPlayerSlotPool</c>. A rule written
        /// as "skip unclaimed" rather than "skip unclaimed PLAYER SLOTS" would have hidden all
        /// fifty-five bots on Dustbowl — the failure this case exists to catch.
        /// </remarks>
        [Test]
        public void ABotIsAnnouncedThoughItIsNeverClaimed()
        {
            NetServerActor bot = CreateBody("bot");

            Assert.IsFalse(bot.AvailableForPlayers, "guard: a bot never enters the slot pool.");
            Assert.IsFalse(bot.IsClaimed, "guard: a bot is never claimed.");
            Assert.IsTrue(ServerTickLoop.IsAnnounceable(bot));
        }

        [Test]
        public void ADestroyedActorIsNotAnnounced()
        {
            Assert.IsFalse(ServerTickLoop.IsAnnounceable(null));
        }
    }
}
