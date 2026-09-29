using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins which actors get a row in S_PLAYER_SCORES: <c>ServerTickLoop.FillScoreRows</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found live on 2026-09-29.</b> The rebuilt board listed the sixteen parked player slots
    /// as players named "actor 3" to "actor 16", so a match of two humans and twelve bots read
    /// "28 PLAYERS". Every slot body is registered from startup; only a claimed one is a player.
    /// </para>
    /// <para>
    /// <b>Both directions</b>, for <see cref="AnnounceableActorTests"/>' reason: a parked slot
    /// left in is that bug, and a claimed slot or a bot left out makes a real player vanish from
    /// the board, which is worse.
    /// </para>
    /// </remarks>
    public sealed class ScoreRowsTests
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
        /// A bare replicated body, deactivated before the component is added so it stays out of
        /// the process-wide registry. See <c>AnnounceableActorTests.CreateBody</c>.
        /// </summary>
        private NetServerActor CreateBody(string name, ushort actorId, byte team)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            go.SetActive(false);

            NetServerActor actor = go.AddComponent<NetServerActor>();
            actor.ActorId = actorId;
            actor.Team = team;
            return actor;
        }

        private NetServerActor CreateSlot(ushort actorId, byte team, bool claimed)
        {
            NetServerActor slot = CreateBody(claimed ? "claimed slot" : "parked slot", actorId, team);
            slot.MarkAvailableForPlayers();
            if (claimed) slot.Claim();
            return slot;
        }

        private static int Fill(IReadOnlyList<NetServerActor> actors, MatchScoreTally tally, out PlayerScoreEntry[] rows)
        {
            rows = new PlayerScoreEntry[ProtocolConstants.MAX_ACTORS];
            return ServerTickLoop.FillScoreRows(actors, tally, rows);
        }

        [Test]
        public void AParkedPlayerSlot_HasNoRow()
        {
            var actors = new[] { CreateSlot(3, 0, claimed: false), CreateSlot(4, 1, claimed: false) };

            int count = Fill(actors, new MatchScoreTally(), out _);

            Assert.AreEqual(
                0, count,
                "a parked slot is a body nobody holds; listing it put 'actor 3' on the board as a "
                + "0/0 player for the whole match.");
        }

        [Test]
        public void AClaimedSlotAndABot_EachHaveARow_WithTheirOwnNumbers()
        {
            NetServerActor player = CreateSlot(1, 0, claimed: true);
            NetServerActor parked = CreateSlot(2, 1, claimed: false);
            NetServerActor bot = CreateBody("bot", 17, 1);

            var tally = new MatchScoreTally();
            tally.RecordDeath(victimActorId: 17, killerActorId: 1);
            tally.RecordDeath(victimActorId: 17, killerActorId: 1);
            tally.RecordDeath(victimActorId: 1, killerActorId: 17);

            int count = Fill(new[] { player, parked, bot }, tally, out PlayerScoreEntry[] rows);

            Assert.AreEqual(2, count, "the player and the bot, and not the parked slot between them.");

            Assert.AreEqual(1, rows[0].ActorId);
            Assert.AreEqual(2, rows[0].Kills);
            Assert.AreEqual(1, rows[0].Deaths);
            Assert.AreEqual(0, rows[0].Team);

            Assert.AreEqual(17, rows[1].ActorId, "a bot is never claimed, and still has a row.");
            Assert.AreEqual(1, rows[1].Kills);
            Assert.AreEqual(2, rows[1].Deaths);
            Assert.AreEqual(1, rows[1].Team);
        }

        [Test]
        public void AReleasedSlot_LeavesTheTable()
        {
            NetServerActor slot = CreateSlot(5, 0, claimed: true);
            var actors = new[] { slot };

            Assert.AreEqual(1, Fill(actors, new MatchScoreTally(), out _), "guard: a held slot has a row.");

            slot.Release();

            Assert.AreEqual(
                0, Fill(actors, new MatchScoreTally(), out _),
                "a leaver's body goes back to the pool; its row going with it is what takes the "
                + "leaver off every other player's board.");
        }

        [Test]
        public void ADestroyedActor_HasNoRow()
        {
            NetServerActor bot = CreateBody("bot", 20, 0);

            int count = Fill(new NetServerActor[] { null, bot }, new MatchScoreTally(), out PlayerScoreEntry[] rows);

            Assert.AreEqual(1, count);
            Assert.AreEqual(20, rows[0].ActorId);
        }
    }
}
