using System;
using System.IO;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Whose head gets a name and health bar, when, and how large. Playtest 2026-09-28,
    /// feature 1; people only since 2026-09-29.
    /// </summary>
    public sealed class NameplateTests
    {
        private static PlayerListEntry Row(byte actorId, string name)
            => new PlayerListEntry
            {
                ActorId = actorId,
                Name = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(name)),
            };

        /// <summary>
        /// Owner report 2026-09-29: a plate over every bot was clutter. A plate is for a person
        /// the player list names, on either side, and for nothing else.
        /// </summary>
        [Fact]
        public void APlate_IsForAPersonOnEitherSide_NeverABot()
        {
            var bots = new BotRoster();
            bots.Apply(new SpawnActorMessage(3, TeamId.Team0, SpawnFlags.IsBot, 0, 0, 0, 0, 100, 0));
            bots.Apply(new SpawnActorMessage(4, TeamId.Team1, SpawnFlags.IsBot, 0, 0, 0, 0, 100, 0));
            bots.Apply(new SpawnActorMessage(5, TeamId.Team0, SpawnFlags.None, 0, 0, 0, 0, 100, 0));
            bots.Apply(new SpawnActorMessage(6, TeamId.Team1, SpawnFlags.None, 0, 0, 0, 0, 100, 0));

            var names = new PlayerNameTable();
            var rows = new PlayerListEntry[ProtocolConstants.MAX_ACTORS];
            rows[0] = Row(5, "Minh");
            rows[1] = Row(6, "Hoang");
            names.Apply(rows, 2);

            Assert.Equal("Minh", NameplateRules.PlateNameOf(5, names));
            Assert.Equal("Hoang", NameplateRules.PlateNameOf(6, names));

            // The bots have names everywhere else (a callsign) and no plate here.
            Assert.Equal(BotCallsigns.For(3), ActorNames.Display(3, names, bots));
            Assert.Null(NameplateRules.PlateNameOf(3, names));
            Assert.Null(NameplateRules.PlateNameOf(4, names));
        }

        /// <summary>
        /// A player who leaves drops out of the next list, and their plate with them: a released
        /// slot's body must not keep floating a name.
        /// </summary>
        [Fact]
        public void APlayerWhoLeaves_LosesTheirPlate()
        {
            var names = new PlayerNameTable();
            var rows = new PlayerListEntry[ProtocolConstants.MAX_ACTORS];
            rows[0] = Row(5, "Minh");
            names.Apply(rows, 1);
            Assert.Equal("Minh", NameplateRules.PlateNameOf(5, names));

            names.Apply(new PlayerListEntry[ProtocolConstants.MAX_ACTORS], 0);

            Assert.Null(NameplateRules.PlateNameOf(5, names));
        }

        /// <summary>
        /// The presenter asks the rule above for every name it draws. Read off the source because
        /// the presenter compiles into a Unity assembly this project cannot reference; the name
        /// the killfeed uses (<c>DisplayNameOf</c>) names bots too, and reaching for it put a plate
        /// over every bot.
        /// </summary>
        [Fact]
        public void ThePresenter_NamesPlatesOnlyThroughTheRule()
        {
            string source = File.ReadAllText(ClientScript("NameplatePresenter.cs"));

            Assert.Contains("NameplateRules.PlateNameOf(actorId, _combat.Names)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DisplayNameOf", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ActorNames.", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// The star over a head is the star on the Tab board: the plate asks the score table for
        /// each side's leader, and the board sorts by that table's one order, so the two can
        /// never crown different players.
        /// </summary>
        [Fact]
        public void TheStarOnAPlate_IsTheBoardsStar()
        {
            string plates = File.ReadAllText(ClientScript("NameplatePresenter.cs"));
            Assert.Contains("scores.LeaderOf(TeamId.Team0)", plates, StringComparison.Ordinal);
            Assert.Contains("scores.LeaderOf(TeamId.Team1)", plates, StringComparison.Ordinal);

            string board = File.ReadAllText(ClientScript("NetClientCombatPresenter.cs"));
            Assert.Contains("=> _scores.CompareRank(left, right);", board, StringComparison.Ordinal);
        }

        /// <remarks>A missing file fails rather than scanning nothing and passing.</remarks>
        private static string ClientScript(string fileName)
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                if (!File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) continue;

                string path = Path.Combine(
                    d.FullName, "Ironfront_Reborn", "Assets", "Scripts", "Net", "Client", fileName);
                Assert.True(File.Exists(path), $"missing Unity source: {path}");
                return path;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }

        [Fact]
        public void ATeammate_IsShownThroughCover_ButDimmed()
        {
            Assert.Equal(1f, NameplateRules.Opacity(teammate: true, distance: 40f, covered: false));
            Assert.Equal(
                NameplateRules.CoveredTeammateOpacity,
                NameplateRules.Opacity(teammate: true, distance: 40f, covered: true));
        }

        /// <summary>An enemy plate through a wall would be a wallhack the game handed out.</summary>
        [Fact]
        public void AnEnemy_IsShownOnlyInSight()
        {
            Assert.Equal(1f, NameplateRules.Opacity(teammate: false, distance: 40f, covered: false));
            Assert.Equal(0f, NameplateRules.Opacity(teammate: false, distance: 40f, covered: true));
            Assert.Equal(0f, NameplateRules.Opacity(teammate: false, distance: 5f, covered: true));
        }

        [Fact]
        public void EachSide_HasItsOwnRange()
        {
            Assert.Equal(0f, NameplateRules.Opacity(false, NameplateRules.EnemyRange + 1f, false));
            Assert.True(NameplateRules.Opacity(true, NameplateRules.EnemyRange + 1f, false) > 0f);
            Assert.Equal(0f, NameplateRules.Opacity(true, NameplateRules.TeammateRange + 1f, false));
        }

        [Fact]
        public void APlate_FadesOverTheLastStretchOfItsRange()
        {
            float range = NameplateRules.EnemyRange;
            float fadeFrom = range * (1f - NameplateRules.FadeShare);

            Assert.Equal(1f, NameplateRules.Opacity(false, fadeFrom, false));
            Assert.InRange(NameplateRules.Opacity(false, (fadeFrom + range) * 0.5f, false), 0.45f, 0.55f);
            Assert.Equal(0f, NameplateRules.Opacity(false, range, false), 3);
        }

        [Fact]
        public void APlate_ShrinksWithDistance_ButStaysReadable()
        {
            Assert.Equal(1f, NameplateRules.Scale(3f));
            Assert.Equal(1f, NameplateRules.Scale(NameplateRules.FullSizeDistance));
            Assert.True(NameplateRules.Scale(50f) < 1f);
            Assert.True(NameplateRules.Scale(50f) > NameplateRules.Scale(80f));
            Assert.Equal(NameplateRules.MinScale, NameplateRules.Scale(500f));
        }

        [Fact]
        public void APlate_SitsAboveTheHeadForEveryPose()
        {
            float clearance = NameplateRules.HeadClearance;

            Assert.Equal(MovementCore.StandHeight + clearance, NameplateRules.AnchorHeight(false, false, false), 3);
            Assert.Equal(NameplateRules.CrouchedHeadHeight + clearance, NameplateRules.AnchorHeight(true, false, false), 3);
            Assert.Equal(NameplateRules.ProneHeadHeight + clearance, NameplateRules.AnchorHeight(false, true, false), 3);
            Assert.Equal(NameplateRules.SeatedHeadHeight + clearance, NameplateRules.AnchorHeight(true, false, true), 3);

            // The crouched collider is far below a kneeling head; a plate placed on it would sit
            // in the player's face.
            Assert.True(NameplateRules.CrouchedHeadHeight > MovementCore.CrouchHeight);
        }

        [Theory]
        [InlineData(100, 1f)]
        [InlineData(50, 0.5f)]
        [InlineData(0, 0f)]
        [InlineData(180, 1f)]
        public void Health_IsAShareOfOneHundred(byte health, float share)
            => Assert.Equal(share, NameplateRules.Health01(health), 3);
    }
}
