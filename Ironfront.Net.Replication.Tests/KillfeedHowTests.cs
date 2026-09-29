using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The killfeed says who, what and how: the server's attribution rules, the wording a client
    /// draws, and the numbering a HUD keys its rows on. Playtest 2026-09-28, feature 2.
    /// </summary>
    public sealed class KillfeedHowTests
    {
        private const ushort World = DeathMessage.EnvironmentKiller;
        private const ushort Minh = 7;
        private const ushort Victim = 12;

        // ------------------------------------------------------------------ attribution

        /// <summary>
        /// <c>Vehicle.Die</c> damages the crew naming nobody. Whoever emptied the vehicle killed
        /// them -- the "The world" deaths the playtest reported.
        /// </summary>
        [Fact]
        public void TheCrewOfADestroyedVehicle_IsCreditedToWhoeverDestroyedIt()
        {
            DeathAttribution result = DeathAttribution.Resolve(
                World, WeaponIds.NONE, DeathDetail.WentDownWithVehicle,
                vehicleType: VehicleIds.HELICOPTER, vehicleDestroyerActorId: Minh,
                killerSeatVehicleType: VehicleIds.NONE);

            Assert.Equal(Minh, result.KillerActorId);
            Assert.Equal(VehicleIds.HELICOPTER, result.VehicleType);
            Assert.Equal(DeathDetail.WentDownWithVehicle, result.Detail);
        }

        [Fact]
        public void ACrewWhoseVehicleNobodyDestroyed_StaysTheWorlds()
        {
            DeathAttribution result = DeathAttribution.Resolve(
                World, WeaponIds.NONE, DeathDetail.WentDownWithVehicle,
                vehicleType: VehicleIds.HELICOPTER, vehicleDestroyerActorId: World,
                killerSeatVehicleType: VehicleIds.NONE);

            Assert.Equal(World, result.KillerActorId);
            Assert.Equal(VehicleIds.HELICOPTER, result.VehicleType);
        }

        /// <summary>A tank's cannon carries no weapon id; the seat says whose it was.</summary>
        [Fact]
        public void AKillNoHandWeaponNames_ByASeatedKiller_IsTheVehicles()
        {
            DeathAttribution result = DeathAttribution.Resolve(
                Minh, WeaponIds.NONE, DeathDetail.None,
                vehicleType: VehicleIds.NONE, vehicleDestroyerActorId: World,
                killerSeatVehicleType: VehicleIds.TANK);

            Assert.Equal(Minh, result.KillerActorId);
            Assert.Equal(VehicleIds.TANK, result.VehicleType);
            Assert.Equal(DeathDetail.KillerInVehicle, result.Detail);
        }

        [Fact]
        public void APassengerFiringTheirRifle_KillsWithTheRifle_NotTheVehicle()
        {
            DeathAttribution result = DeathAttribution.Resolve(
                Minh, WeaponIds.RK44, DeathDetail.None,
                vehicleType: VehicleIds.NONE, vehicleDestroyerActorId: World,
                killerSeatVehicleType: VehicleIds.JEEP);

            Assert.Equal(WeaponIds.RK44, result.WeaponId);
            Assert.Equal(VehicleIds.NONE, result.VehicleType);
            Assert.Equal(DeathDetail.None, result.Detail);
        }

        /// <summary>The ram check names the vehicle; nobody drove it, so nobody scores.</summary>
        [Fact]
        public void ARunawayVehicle_WithNobodyAtTheWheel_StillNamesTheVehicle()
        {
            DeathAttribution result = DeathAttribution.Resolve(
                World, WeaponIds.NONE, DeathDetail.None,
                vehicleType: VehicleIds.JEEP, vehicleDestroyerActorId: World,
                killerSeatVehicleType: VehicleIds.NONE);

            Assert.Equal(World, result.KillerActorId);
            Assert.Equal(VehicleIds.JEEP, result.VehicleType);
            Assert.Equal(DeathDetail.None, result.Detail);
        }

        /// <summary>Whether the killer was in a vehicle is decided here, never claimed by a caller.</summary>
        [Fact]
        public void KillerInVehicle_ClaimedByTheCaller_IsIgnored()
        {
            DeathAttribution result = DeathAttribution.Resolve(
                Minh, WeaponIds.RK44, DeathDetail.KillerInVehicle | DeathDetail.Melee,
                vehicleType: VehicleIds.NONE, vehicleDestroyerActorId: World,
                killerSeatVehicleType: VehicleIds.NONE);

            Assert.Equal(DeathDetail.Melee, result.Detail);
        }

        [Fact]
        public void TheDestroyerCreditWindow_CoversTheFourSecondBurn()
        {
            Assert.True(DeathAttribution.DestroyerCreditSeconds >= 4f);
            Assert.Equal(
                (uint)(DeathAttribution.DestroyerCreditSeconds * ProtocolConstants.SIM_TICK_RATE),
                DeathAttribution.DestroyerCreditTicks);
        }

        // ------------------------------------------------------------------ killfeed wording

        private static KillfeedEntry Kill(
            ushort killer, CauseOfDeath cause = CauseOfDeath.Bullet, byte weapon = WeaponIds.NONE,
            byte vehicle = VehicleIds.NONE, DeathDetail detail = DeathDetail.None, bool headshot = false)
            => new KillfeedEntry(
                killer, Victim, cause, killer == World, headshot, 0f, weapon, vehicle, detail);

        [Theory]
        [InlineData(WeaponIds.RK44, "RK-44")]
        [InlineData(WeaponIds.SIGNAL_DMR, "SIGNAL DMR")]
        [InlineData(WeaponIds.FRAG, "FRAG")]
        public void AKill_IsLabelledWithTheWeapon(byte weapon, string label)
        {
            KillfeedWording wording = KillfeedWording.For(Kill(Minh, weapon: weapon));

            Assert.False(wording.IsSentence);
            Assert.Equal(label, wording.Label);
        }

        [Fact]
        public void AMeleeKill_SaysMelee()
            => Assert.Equal(
                "BIL SCALPEL  ·  MELEE",
                KillfeedWording.For(Kill(Minh, weapon: WeaponIds.BIL_SCALPEL, detail: DeathDetail.Melee)).Label);

        [Fact]
        public void AVehiclesGun_IsLabelledWithTheVehicle()
            => Assert.Equal(
                "TANK",
                KillfeedWording.For(Kill(
                    Minh, CauseOfDeath.Explosion, vehicle: VehicleIds.TANK,
                    detail: DeathDetail.KillerInVehicle)).Label);

        [Fact]
        public void ARoadkill_SaysSo()
            => Assert.Equal(
                "JEEP  ·  ROADKILL",
                KillfeedWording.For(Kill(
                    Minh, CauseOfDeath.Vehicle, vehicle: VehicleIds.JEEP,
                    detail: DeathDetail.KillerInVehicle)).Label);

        [Fact]
        public void ACrewKill_SaysTheVehicleWasDestroyed()
            => Assert.Equal(
                "DESTROYED HELICOPTER",
                KillfeedWording.For(Kill(
                    Minh, CauseOfDeath.Vehicle, vehicle: VehicleIds.HELICOPTER,
                    detail: DeathDetail.WentDownWithVehicle)).Label);

        [Fact]
        public void AnExplosionNoWeaponNames_SaysExplosion()
            => Assert.Equal("EXPLOSION", KillfeedWording.For(Kill(Minh, CauseOfDeath.Explosion)).Label);

        /// <summary>A 1.0 server sends no tail: the line has no label, and the HUD draws a chevron.</summary>
        [Fact]
        public void AKillFromA10Server_HasNoLabel()
            => Assert.Equal(string.Empty, KillfeedWording.For(Kill(Minh)).Label);

        [Theory]
        [InlineData(CauseOfDeath.Drown, "drowned")]
        [InlineData(CauseOfDeath.Fall, "fell to their death")]
        [InlineData(CauseOfDeath.Explosion, "was caught in an explosion")]
        [InlineData(CauseOfDeath.Bullet, "died")]
        public void AWorldDeath_IsASentence_NotTheWorldAsAKiller(CauseOfDeath cause, string sentence)
        {
            KillfeedWording wording = KillfeedWording.For(Kill(World, cause));

            Assert.True(wording.IsSentence);
            Assert.Equal(sentence, wording.Sentence);
            Assert.Equal(string.Empty, wording.Label);
        }

        [Fact]
        public void ACrewNobodyShotDown_WentDownWithTheVehicle()
        {
            Assert.Equal(
                "went down with the Helicopter",
                KillfeedWording.For(Kill(
                    World, CauseOfDeath.Vehicle, vehicle: VehicleIds.HELICOPTER,
                    detail: DeathDetail.WentDownWithVehicle)).Sentence);

            Assert.Equal(
                "went down with their vehicle",
                KillfeedWording.For(Kill(
                    World, CauseOfDeath.Vehicle, detail: DeathDetail.WentDownWithVehicle)).Sentence);
        }

        [Fact]
        public void ARunawayVehicle_HitTheVictim()
            => Assert.Equal(
                "was hit by a Quad bike",
                KillfeedWording.For(Kill(World, CauseOfDeath.Vehicle, vehicle: VehicleIds.QUADBIKE)).Sentence);

        [Fact]
        public void ADeathByYourOwnHand_IsASentence()
        {
            KillfeedEntry grenade = new KillfeedEntry(
                Victim, Victim, CauseOfDeath.Explosion, false, false, 0f, WeaponIds.FRAG);

            Assert.True(grenade.Self);
            Assert.Equal("blew themselves up", KillfeedWording.For(in grenade).Sentence);

            KillfeedEntry ownVehicle = new KillfeedEntry(
                Victim, Victim, CauseOfDeath.Vehicle, false, false, 0f,
                WeaponIds.NONE, VehicleIds.JEEP, DeathDetail.WentDownWithVehicle);

            Assert.Equal("destroyed their own Jeep", KillfeedWording.For(in ownVehicle).Sentence);
        }

        [Fact]
        public void AnUnknownVehicleType_IsNotNamed()
            => Assert.Equal(
                "was hit by a vehicle",
                KillfeedWording.For(Kill(World, CauseOfDeath.Vehicle, vehicle: 200)).Sentence);

        // ------------------------------------------------------------------ deploy caption

        [Fact]
        public void TheDeployScreen_NamesTheKillerAndHow()
        {
            Assert.Equal(
                "Killed by Minh  ·  RK-44",
                KillfeedWording.DeployCaption(Kill(Minh, weapon: WeaponIds.RK44), "Minh"));

            Assert.Equal(
                "Killed by Minh  ·  RECON LRR  ·  HEADSHOT",
                KillfeedWording.DeployCaption(
                    Kill(Minh, weapon: WeaponIds.RECON_LRR, headshot: true), "Minh"));

            Assert.Equal(
                "Killed by Minh",
                KillfeedWording.DeployCaption(Kill(Minh), "Minh"));
        }

        [Theory]
        [InlineData(CauseOfDeath.Drown, "You drowned")]
        [InlineData(CauseOfDeath.Fall, "You fell to your death")]
        [InlineData(CauseOfDeath.Explosion, "You were caught in an explosion")]
        public void TheDeployScreen_SaysWhatTheWorldDid_InTheSecondPerson(CauseOfDeath cause, string caption)
            => Assert.Equal(caption, KillfeedWording.DeployCaption(Kill(World, cause), "unused"));

        [Fact]
        public void TheDeployScreen_ForYourOwnGrenade()
        {
            KillfeedEntry grenade = new KillfeedEntry(
                Victim, Victim, CauseOfDeath.Explosion, false, false, 0f, WeaponIds.FRAG);

            Assert.Equal("You blew yourself up", KillfeedWording.DeployCaption(in grenade, "you"));
        }

        [Fact]
        public void TheDeployScreen_ForACrewNobodyShotDown()
            => Assert.Equal(
                "You went down with the Tank",
                KillfeedWording.DeployCaption(
                    Kill(World, CauseOfDeath.Vehicle, vehicle: VehicleIds.TANK,
                         detail: DeathDetail.WentDownWithVehicle),
                    "unused"));

        // ------------------------------------------------------------------ numbering

        [Fact]
        public void EveryPushedKill_IsNumberedInOrder()
        {
            var feed = new KillfeedModel();

            feed.Push(Kill(Minh));
            feed.Push(Kill(Minh));
            feed.Push(Kill(Minh));

            // Index 0 is the newest.
            Assert.Equal(3, feed[0].Sequence);
            Assert.Equal(2, feed[1].Sequence);
            Assert.Equal(1, feed[2].Sequence);
        }

        /// <summary>A kill keeps its number as newer ones push it down: that is what a row is keyed on.</summary>
        [Fact]
        public void AKillsNumber_FollowsItDownTheFeed()
        {
            var feed = new KillfeedModel(capacity: 2);

            feed.Push(Kill(Minh));
            long first = feed[0].Sequence;

            feed.Push(Kill(Minh));
            Assert.Equal(first, feed[1].Sequence);

            feed.Push(Kill(Minh));
            Assert.NotEqual(first, feed[0].Sequence);
            Assert.NotEqual(first, feed[1].Sequence);
        }

        /// <summary>
        /// The case a number derived from the index gets wrong: Prune drops a line from the
        /// MIDDLE (it compacts rather than truncating), and the lines on either side must keep
        /// the numbers their rows are keyed on.
        /// </summary>
        [Fact]
        public void AKillsNumber_SurvivesAPruneFromTheMiddle()
        {
            var feed = new KillfeedModel();

            feed.Push(Kill(Minh, weapon: WeaponIds.RK44));                  // 1, fresh
            feed.Push(new KillfeedEntry(Minh, Victim, CauseOfDeath.Bullet,  // 2, already stale
                false, false, postedAtSeconds: -100f));
            feed.Push(Kill(Minh, weapon: WeaponIds.FRAG));                  // 3, fresh

            feed.Prune(nowSeconds: 1f);
            Assert.Equal(2, feed.Count);

            feed.Push(Kill(Minh));                                          // 4

            Assert.Equal(4, feed[0].Sequence);
            Assert.Equal(3, feed[1].Sequence);
            Assert.Equal(1, feed[2].Sequence);
            Assert.Equal(WeaponIds.RK44, feed[2].WeaponId);
        }

        /// <summary>
        /// A row fading from the last match must not share a number with the first kill of the next.
        /// </summary>
        [Fact]
        public void Numbers_AreNotReusedAfterAReset()
        {
            var feed = new KillfeedModel();

            feed.Push(Kill(Minh));
            feed.Push(Kill(Minh));
            feed.Reset();
            feed.Push(Kill(Minh));

            Assert.Equal(3, feed[0].Sequence);
            Assert.Equal(1, feed.TotalKills);
        }

        // ------------------------------------------------------------------ rows follow kills

        private static (int[] LineRows, bool[] Kept) Assign(long[] rows, float[] costs, long[] lines)
        {
            var lineRows = new int[lines.Length];
            var kept = new bool[rows.Length];
            KillfeedRowAssignment.Assign(rows, costs, lines, lineRows, kept);
            return (lineRows, kept);
        }

        [Fact]
        public void AKillOnScreen_KeepsItsRow_AsANewerOneArrives()
        {
            (int[] lineRows, bool[] kept) = Assign(
                rows: new long[] { 0, 1, 0 }, costs: new[] { 0f, 3f, 0f }, lines: new long[] { 2, 1 });

            Assert.Equal(1, lineRows[1]);   // kill 1 stays in row 1
            Assert.Equal(0, lineRows[0]);   // kill 2 takes the first free row
            Assert.Equal(new[] { true, true, false }, kept);
        }

        /// <summary>
        /// The newest line arrives first. Matched line by line it would take the row of a kill
        /// that is still on screen, and that kill would jump rows and play its arrival again.
        /// </summary>
        [Fact]
        public void TheNewestKill_NeverTakesTheRowOfAKillStillOnScreen()
        {
            (int[] lineRows, bool[] kept) = Assign(
                rows: new long[] { 2, 1 }, costs: new[] { 3f, 3f }, lines: new long[] { 3, 2 });

            Assert.Equal(0, lineRows[1]);   // kill 2 keeps row 0
            Assert.Equal(1, lineRows[0]);   // kill 3 takes the row kill 1 left
            Assert.Equal(new[] { true, true }, kept);
        }

        [Fact]
        public void ANewKill_PrefersAFreeRow_ThenTheFaintestFadingOne()
        {
            (int[] lineRows, _) = Assign(
                rows: new long[] { 9, 8, 0 }, costs: new[] { 1.8f, 1.2f, 0f }, lines: new long[] { 10 });
            Assert.Equal(2, lineRows[0]);

            (lineRows, _) = Assign(
                rows: new long[] { 9, 8 }, costs: new[] { 1.8f, 1.2f }, lines: new long[] { 10 });
            Assert.Equal(1, lineRows[0]);
        }

        [Fact]
        public void ARowWhoseKillLeftThePush_IsNotKept_AndSoLeaves()
        {
            (_, bool[] kept) = Assign(
                rows: new long[] { 4, 3, 2 }, costs: new[] { 3f, 3f, 3f }, lines: new long[] { 4, 3 });

            Assert.Equal(new[] { true, true, false }, kept);
        }

        [Fact]
        public void LinesBeyondTheRows_GoUnshown_AndAnUnusableRowIsNeverUsed()
        {
            (int[] lineRows, bool[] kept) = Assign(
                rows: new long[] { 0, -1 }, costs: new[] { 0f, float.MaxValue }, lines: new long[] { 5, 4 });

            Assert.Equal(0, lineRows[0]);
            Assert.Equal(-1, lineRows[1]);
            Assert.False(kept[1]);
        }

        [Fact]
        public void AnEntryFromTheWire_CarriesTheTail()
        {
            var message = new DeathMessage(
                Victim, Minh, CauseOfDeath.Explosion, 0, 0, 0, (byte)HitboxType.Head,
                WeaponIds.SPEARHEAD, VehicleIds.JEEP, DeathDetail.WentDownWithVehicle);

            KillfeedEntry entry = KillfeedEntry.From(in message, 1.5f);

            Assert.Equal(WeaponIds.SPEARHEAD, entry.WeaponId);
            Assert.Equal(VehicleIds.JEEP, entry.VehicleType);
            Assert.Equal(DeathDetail.WentDownWithVehicle, entry.Detail);
            Assert.True(entry.Headshot);
            Assert.False(entry.Self);
        }
    }
}
