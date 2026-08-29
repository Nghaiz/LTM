using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-55 / X-56 — the AI null-reference cascade that a match reset started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything under test lives in <c>Assembly-CSharp</c>, which no asmdef may reference and
    /// which <c>dotnet build</c> never compiles, so these read the shipped sources off disk. That
    /// is the same instrument <c>VehicleClientSourceInvariantTests</c> uses and it has the same
    /// limit: it pins the SHAPE of the fix, and the run in the report is what pins the behaviour.
    /// </para>
    /// <para>
    /// Two of these assert ORDER rather than presence, because presence is not the property that
    /// matters: ejecting after the <c>Destroy</c> would read as a complete fix, compile, and
    /// rescue nobody.
    /// </para>
    /// </remarks>
    public sealed class NullReferenceCascadeTests
    {
        // ---------------------------------------------- X-55: the cause

        [Fact]
        public void AVehicleCanEmptyItsSeatsWithoutKillingAnyone()
        {
            string vehicle = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs");

            string body = MethodBody(vehicle, "public void EjectOccupants()");

            Assert.Contains("LeaveSeat()", body, StringComparison.Ordinal);

            // The whole point of a second method: Die's eject deals 200 balance damage and, in an
            // enclosed seat, 200 health. A round transition must not do either.
            Assert.DoesNotContain("Damage(", body, StringComparison.Ordinal);
        }

        [Fact]
        public void AOneSidedSeatBookingIsReportedAndUnparentedRatherThanThrowingOutOfTheLoop()
        {
            // Found by o6-combat-02: a seat can be booked by a body that does not think it is
            // sitting in it (X-58). Actor.LeaveSeat opens with seat.transform and throws on the
            // null half -- which aborted the loop and left every LATER seat of that vehicle
            // un-ejected, the one thing an eject must not do.
            string vehicle = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs");

            string body = MethodBody(vehicle, "public void EjectOccupants()");

            Assert.Contains("occupant.seat == seat", body, StringComparison.Ordinal);

            // Reported, not skipped: a silent `continue` would leave the body welded to a
            // hierarchy that is about to be destroyed, which is the defect this method exists for.
            Assert.Contains("Debug.LogError", body, StringComparison.Ordinal);
            Assert.Contains("SetParent(null", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWorldResetEmptiesTheSeatsBeforeItDestroysTheVehicleAndNotAfter()
        {
            string spawner = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/VehicleSpawner.cs");

            int eject = spawner.IndexOf("EjectOccupants()", StringComparison.Ordinal);
            int destroy = spawner.IndexOf(
                "Destroy(lastSpawnedVehicle.gameObject)", StringComparison.Ordinal);

            Assert.True(eject >= 0, "the world reset does not empty the seats at all");
            Assert.True(destroy >= 0, "the world reset no longer destroys the vehicle");
            Assert.True(
                eject < destroy,
                "the seats are emptied AFTER the Destroy, which rescues nobody: Unity has already "
                + "committed to taking the children with the parent.");
        }

        [Fact]
        public void VehicleDeathStillHurtsThePeopleInside()
        {
            // The companion direction. A fix that made Die stop damaging its occupants would pass
            // every other assertion here and would be a behaviour regression nothing else reads.
            string vehicle = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs");

            string die = MethodBody(vehicle, "public virtual void Die()");

            Assert.Contains("LeaveSeat()", die, StringComparison.Ordinal);
            Assert.Contains("occupant.Damage(200f, 200f", die, StringComparison.Ordinal);
            Assert.Contains("occupant.Damage(0f, 200f", die, StringComparison.Ordinal);
        }

        // ---------------------------------------------- X-55: the backstop

        [Fact]
        public void ADestroyedBotLeavesItsSquadRoster()
        {
            string controller = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs");

            string body = MethodBody(controller, "private void OnDestroy()");

            Assert.Contains("squad.DropMember(this)", body, StringComparison.Ordinal);

            // Guarded, for the reason Die states in its own remark: a squadless body is ORDINARY
            // here -- every networked player slot is one -- so an unguarded DropMember would throw
            // out of OnDestroy on every slot the pool clears.
            Assert.Contains("InSquad()", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheRosterIsMendedAtTheRegisterRatherThanAtItsLoudestReader()
        {
            // A null-guard inside LocalAvoidanceVelocity would silence 2,044 lines per run and
            // leave the corpse in the roster to be averaged into the squad's centre by
            // UpdateGroupedUpFlag, asked for a target by GetTarget, and made leader by DropMember.
            string controller = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs");

            string body = MethodBody(controller, "private Vector3 LocalAvoidanceVelocity()");

            Assert.DoesNotMatch(new Regex(@"member\s*==\s*null"), body);
            Assert.DoesNotMatch(new Regex(@"actor\s*==\s*null"), body);
        }

        [Fact]
        public void DropMemberHasMoreThanTheOneCallerThatOnlyFiresOnDeath()
        {
            // The identity of the defect: the removal EXISTED and was reached from exactly one
            // place -- Die -- so a bot that was destroyed rather than killed kept its slot. This
            // fails again the moment someone deletes the OnDestroy hook and leaves the rest.
            string controller = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs");

            Assert.Equal(2, Regex.Matches(controller, @"squad\.DropMember\(this\)").Count);
        }

        [Fact]
        public void AVehicleDoesNotReportADriverThatDoesNotAgreeItIsSeated()
        {
            // Car.FixedUpdate asks HasDriver() and then reads Driver().controller.CarInput(),
            // whose first act is actor.seat.vehicle. Seat-only was the reading that threw.
            string vehicle = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs");

            string hasDriver = MethodBody(vehicle, "public bool HasDriver()");
            Assert.Contains("driver.seat == seats[0]", hasDriver, StringComparison.Ordinal);
        }

        [Fact]
        public void DriverItselfStaysPermissiveBecauseTheEntrySequenceReadsItMidWay()
        {
            // A strict Driver() was written and reverted. Actor.EnterSeat calls
            // seat.SetOccupant(this) BEFORE assigning its own seat field, and SetOccupant reaches
            // Tank.DriverEntered, which reads Driver().team. Inside that window the halves ALWAYS
            // disagree -- so the strict version threw, aborted EnterSeat, and left the seat booked
            // with the body's half unset: it manufactured the corruption it was meant to survive.
            // Six vehicles reported a one-sided booking in one lane-B run that had none before it.
            string vehicle = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs");

            string body = MethodBody(vehicle, "public Actor Driver()");

            Assert.DoesNotContain("driver.seat", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheOneSidedDriverBookingIsReportedOncePerVehicleAndNotOncePerStep()
        {
            // HasDriver runs inside FixedUpdate for every vehicle in the map. An unconditional
            // Debug.LogError there would bury the thing it is reporting -- the same failure shape
            // as the 4,183-line cascade this phase exists to remove.
            string vehicle = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs");

            string body = MethodBody(vehicle, "public bool HasDriver()");

            Assert.Contains("reportedOneSidedDriverBooking", body, StringComparison.Ordinal);
            Assert.Contains("Debug.LogError", body, StringComparison.Ordinal);
        }

        // ---------------------------------------------- X-57: the half suspension

        [Fact]
        public void ASuspendedBrainDoesNoAiWorkAtAllAndNotJustNoUpdate()
        {
            // Unity does not stop a coroutine when a MonoBehaviour is disabled, so
            // IAiDriver.Suspend's `enabled = false` stopped Update and left all eight AI
            // coroutines running on a body the server was driving.
            string controller = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs");

            string body = MethodBody(controller, "private bool AiWorkAllowed()");

            Assert.Matches(new Regex(@"!\s*base\.enabled"), body);
        }

        [Fact]
        public void TheSuspensionIsGatedAtTheOneGateAndNotAtTheSiteThatHappenedToThrow()
        {
            // PushAntiStuckEvent is where it threw; squad.ExitVehicle() and squad.MoveTo() sit two
            // branches away in the same coroutine, on the same squadless body. Guarding the throw
            // site alone is muting a stack trace, not fixing a suspension.
            string controller = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs");

            string body = MethodBody(controller, "private void PushAntiStuckEvent()");

            Assert.DoesNotContain("InSquad()", body, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"squad\s*==\s*null"), body);

            // And the gate every coroutine already parks on is still the one place that decides:
            // eight AI coroutines plus Update. Counted on the CALL, not on the name -- the name
            // also appears in prose, and a gate that counts prose is the trap check-net-layering
            // documents.
            Assert.Equal(9, Regex.Matches(controller, @"if \(!AiWorkAllowed\(\)\)").Count);
        }

        // ------------------------------------------------ helpers

        /// <summary>
        /// The braced body of the first method whose signature line matches
        /// <paramref name="signature"/>, brace-counted rather than regex-matched.
        /// </summary>
        private static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, $"no method '{signature}' in the source");

            int open = source.IndexOf('{', at);
            Assert.True(open >= 0, $"'{signature}' has no body");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            throw new InvalidOperationException($"unbalanced braces after '{signature}'");
        }

        private static string ReadUnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
