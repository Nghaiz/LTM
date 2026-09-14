using System;
using System.Collections.Generic;
using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Ledger <b>X-27</b>, second half: <c>-Weapon</c> has to put the named weapon in the
    /// scripted driver's hands, and has to SAY SO when it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row this file exists for was closed once already, and the fix pinned the wrong
    /// bodies.</b> <c>PinnedLoadoutDirectory</c> plus five EditMode tests all passed, and none of
    /// them asked WHO reads the directory. The answer is <c>AiActorController.PinnedOr</c> and
    /// nothing else — while <c>Actor.SpawnLoadoutWeapons</c> arms a claimed player body from
    /// <c>ResolveDeployLoadout()</c>, the ids the client sent, never from
    /// <c>controller.GetLoadout()</c>. So five runs asking for RK-44, SIGNAL DMR, SL-DEFENDER and
    /// BEU AW1 all deployed <c>loadout 1/3/7/5/0</c> under a server log line reading "loadout
    /// pinned for every body the server spawns".
    /// </para>
    /// <para>
    /// <b>What is graded here is the distinction the old pin did not have</b>: a name that
    /// RESOLVED against the weapon catalogue versus one that did not. The old pin could not tell
    /// the difference — it never touched the catalogue — and so reported success either way,
    /// which is worse than not pinning, because every run taken under it is quietly incomparable
    /// to every other. The Unity half (<c>FpsActorController.GetLoadout</c>) is three lines of
    /// wiring around this class and is not reachable from <c>dotnet test</c>.
    /// </para>
    /// </remarks>
    public sealed class ClientLoadoutPinTests
    {
        // A stand-in weapon catalogue. `PinnedOr` is generic precisely so a test can resolve
        // names without Assembly-CSharp's WeaponEntry, which nothing in this project compiles.
        private static readonly Dictionary<string, string> Catalogue = new Dictionary<string, string>
        {
            ["RK-44"] = "weapon:RK-44",
            ["SIGNAL DMR"] = "weapon:SIGNAL DMR",
            ["SL-DEFENDER"] = "weapon:SL-DEFENDER",
            ["BEU AW1"] = "weapon:BEU AW1",
        };

        // Returning null IS the contract -- it is how PinnedOr is told no weapon has that name.
        // The bang is needed only because this file has nullable enabled while the linked
        // ClientLoadoutPin.cs is nullable-oblivious, so `Func<string, string>` cannot express the
        // null the real WeaponManager.EntryNamed also returns.
        private static string Lookup(string name)
        {
            Catalogue.TryGetValue(name, out string? entry);
            return entry!;
        }

        // ------------------------------------------------------------------ a pin that pins

        /// <summary>
        /// The whole point of the row: the named weapon replaces the draw. `drawn` here is the
        /// loadout screen's RK-44 — weaponId 1, the value every one of the five broken runs
        /// deployed with.
        /// </summary>
        [Fact]
        public void APinnedPrimaryReplacesTheDraw()
        {
            var pin = new ClientLoadoutPin("SIGNAL DMR");

            string armed = pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup);

            Assert.Equal("weapon:SIGNAL DMR", armed);
            Assert.False(pin.HasUnresolved);
        }

        /// <summary>
        /// It narrows and never widens, the same property <c>PinnedLoadoutDirectory</c> has:
        /// pinning a primary must not disturb a gear slot the run had no opinion about.
        /// </summary>
        [Fact]
        public void ASlotThePinHasNoNameForKeepsItsDraw()
        {
            var pin = new ClientLoadoutPin("SIGNAL DMR");

            Assert.Equal("weapon:FRAG", pin.PinnedOr("weapon:FRAG", ClientLoadoutSlot.Gear1, Lookup));
            Assert.Null(pin.NameFor(ClientLoadoutSlot.Gear1));
            Assert.False(pin.HasUnresolved);
        }

        /// <summary>Gear is pinned by the same route, for check 4's grenade.</summary>
        [Fact]
        public void APinnedGearReplacesTheDraw()
        {
            var pin = new ClientLoadoutPin(null, gear1: "BEU AW1");

            Assert.Equal("weapon:BEU AW1", pin.PinnedOr("weapon:FRAG", ClientLoadoutSlot.Gear1, Lookup));
            Assert.Equal("weapon:RK-44", pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup));
        }

        /// <summary>Surrounding whitespace is a shell artefact, not a different weapon.</summary>
        [Fact]
        public void NamesAreTrimmed()
        {
            var pin = new ClientLoadoutPin("  SIGNAL DMR ");

            Assert.Equal("SIGNAL DMR", pin.NameFor(ClientLoadoutSlot.Primary));
            Assert.Equal("weapon:SIGNAL DMR", pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup));
        }

        /// <summary>
        /// A pin holding no name at all is refused at construction rather than installed. An
        /// installed pin that overrides nothing still logs "pinned" — which is the exact defect
        /// this row reopened for, so it must not be constructible.
        /// </summary>
        [Fact]
        public void APinThatOverridesNothingIsRefused()
        {
            Assert.Throws<ArgumentException>(() => new ClientLoadoutPin(null));
            Assert.Throws<ArgumentException>(() => new ClientLoadoutPin("   ", "", null));
        }

        // --------------------------------------------------------- a pin that CANNOT pin, loudly

        /// <summary>
        /// An unmatched name keeps the drawn weapon — a body holding the wrong gun still yields
        /// a readable run, whereas arming it with nothing is X-11 — and is recorded as
        /// unresolved so nothing downstream reads the run as pinned.
        /// </summary>
        [Fact]
        public void AnUnresolvableNameKeepsTheDrawAndIsRecorded()
        {
            var pin = new ClientLoadoutPin("SIGNAL-DMR");

            string armed = pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup);

            Assert.Equal("weapon:RK-44", armed);
            Assert.True(pin.HasUnresolved);
        }

        /// <summary>
        /// The catalogue matches exactly, so a case difference is a miss. Stated as a test
        /// because "SL-Defender" looks like it ought to work and reads, in a log, exactly like a
        /// pin that took.
        /// </summary>
        [Fact]
        public void ResolutionIsExactSoCaseMattersAndSaysSo()
        {
            var pin = new ClientLoadoutPin("sl-defender");

            pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup);

            Assert.True(pin.HasUnresolved);
            Assert.Contains("case and spacing", pin.Describe());
        }

        /// <summary>
        /// The line a failed pin emits carries the marker <c>run-lane-b.ps1</c> greps. If this
        /// string and that grep ever drift apart, an unresolved pin stops failing the run and
        /// the row is silently open again — which is why both are asserted here rather than only
        /// the flag.
        /// </summary>
        [Fact]
        public void TheFailureLineCarriesTheMarkerTheRunnerGreps()
        {
            var pin = new ClientLoadoutPin("NOT A WEAPON");
            pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup);

            string line = pin.Describe();

            Assert.StartsWith(ClientLoadoutPin.UnresolvedMarker, line);
            Assert.Contains("NOT A WEAPON", line);
            Assert.Equal("[lane-b] loadout pin UNRESOLVED", ClientLoadoutPin.UnresolvedMarker);
        }

        /// <summary>The success line carries the other marker, for the same reason.</summary>
        [Fact]
        public void TheSuccessLineCarriesTheAppliedMarker()
        {
            var pin = new ClientLoadoutPin("SIGNAL DMR");
            pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup);

            string line = pin.Describe();

            Assert.StartsWith(ClientLoadoutPin.AppliedMarker, line);
            Assert.Contains("SIGNAL DMR", line);
            Assert.Equal("[lane-b] loadout pin applied", ClientLoadoutPin.AppliedMarker);
        }

        /// <summary>
        /// One miss among several hits is still a failure. A partially pinned run is not the
        /// experiment it was asked for, and averaging it with a fully pinned one is the mistake
        /// this row exists to stop.
        /// </summary>
        [Fact]
        public void OneUnresolvedSlotFailsTheWholePinEvenWhenAnotherResolved()
        {
            var pin = new ClientLoadoutPin("SIGNAL DMR", gear1: "BEU AW2");

            pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup);
            pin.PinnedOr("weapon:FRAG", ClientLoadoutSlot.Gear1, Lookup);

            Assert.True(pin.HasUnresolved);

            string line = pin.Describe();
            Assert.StartsWith(ClientLoadoutPin.UnresolvedMarker, line);
            Assert.Contains("BEU AW2", line);

            // The half that DID work is named too: a reader diagnosing the run needs to know
            // which slot held what, not merely that something went wrong.
            Assert.Contains("SIGNAL DMR", line);
        }

        /// <summary>
        /// Nothing to say before anything has been armed. A caller that logged this
        /// unconditionally would print an empty verdict on a run that never deployed, which
        /// reads as a clean pin.
        /// </summary>
        [Fact]
        public void DescribeIsNullBeforeAnySlotHasBeenAsked()
        {
            Assert.Null(new ClientLoadoutPin("SIGNAL DMR").Describe());
            Assert.False(new ClientLoadoutPin("SIGNAL DMR").TryTakeReport(out _));
        }

        // ------------------------------------------------------------------------- the once-latch

        /// <summary>
        /// The report is taken once. <c>GetLoadout</c> runs on every respawn, and a line per life
        /// would bury the one that matters in a log the runner then greps.
        /// </summary>
        [Fact]
        public void TheReportIsTakenOnceEvenAcrossRespawns()
        {
            var pin = new ClientLoadoutPin("SIGNAL DMR");
            pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup);

            Assert.True(pin.TryTakeReport(out string first));
            Assert.NotNull(first);

            pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup);

            Assert.False(pin.TryTakeReport(out string second));
            Assert.Null(second);

            // Taking the report does not erase the verdict: the run's own grading still reads it.
            Assert.Equal(first, pin.Describe());
        }

        /// <summary>
        /// Re-arming the same slot every life must not grow the line. A run is 10 minutes of
        /// respawns and the naive list would carry one entry per deploy.
        /// </summary>
        [Fact]
        public void RepeatedArmingDoesNotGrowTheLine()
        {
            var pin = new ClientLoadoutPin("SIGNAL DMR", gear1: "NOPE");

            for (int life = 0; life < 5; life++)
            {
                pin.PinnedOr("weapon:RK-44", ClientLoadoutSlot.Primary, Lookup);
                pin.PinnedOr("weapon:FRAG", ClientLoadoutSlot.Gear1, Lookup);
            }

            string line = pin.Describe();

            Assert.Equal(1, CountOf(line, "SIGNAL DMR"));
            Assert.Equal(1, CountOf(line, "NOPE"));
        }

        // ------------------------------------------------------------------------------- guards

        /// <summary>
        /// A lookup nobody supplied is a programming error, not a deferral: silently keeping the
        /// draw would make an un-wired caller indistinguishable from an unpinned run.
        /// </summary>
        [Fact]
        public void AMissingLookupThrows()
        {
            var pin = new ClientLoadoutPin("SIGNAL DMR");

            Assert.Throws<ArgumentNullException>(
                () => pin.PinnedOr<string>("weapon:RK-44", ClientLoadoutSlot.Primary, null));
        }

        /// <summary>
        /// A slot value this pin has never heard of defers rather than throwing, so adding a
        /// ClientLoadoutSlot cannot turn a pinned run into a crash.
        /// </summary>
        [Fact]
        public void AnUnknownSlotDefers()
        {
            var pin = new ClientLoadoutPin("SIGNAL DMR");

            Assert.Null(pin.NameFor((ClientLoadoutSlot)99));
            Assert.Equal("weapon:FRAG", pin.PinnedOr("weapon:FRAG", (ClientLoadoutSlot)99, Lookup));
            Assert.False(pin.HasUnresolved);
        }

        /// <summary>
        /// The installed pin is a plain static that can be cleared. Unset is the shipped state —
        /// only <c>LaneBHarness</c> ever assigns it — and clearing has to work, because
        /// <c>NetClientBindings.ResetOnLoad</c> is what stops a pin from surviving a Play session
        /// into the next one when domain reload is off.
        /// </summary>
        [Fact]
        public void TheActivePinInstallsAndClears()
        {
            ClientLoadoutPin previous = ClientLoadoutPin.Active;
            try
            {
                Assert.Null(previous);

                var pin = new ClientLoadoutPin("SIGNAL DMR");
                ClientLoadoutPin.Active = pin;
                Assert.Same(pin, ClientLoadoutPin.Active);

                ClientLoadoutPin.Active = null;
                Assert.Null(ClientLoadoutPin.Active);
            }
            finally
            {
                ClientLoadoutPin.Active = previous;
            }
        }

        private static int CountOf(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
                 i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }
}
