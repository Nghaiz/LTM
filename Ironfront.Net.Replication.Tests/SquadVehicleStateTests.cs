using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A squad has a vehicle while it is boarding one or sitting in it, and not after it got out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The original answered "was this squad ever given a vehicle".</b> <c>Squad.HasVehicle</c>
    /// returned <c>squadVehicle != null</c>, nothing ever cleared <c>squadVehicle</c>, and
    /// <c>ExitVehicle</c> cleared only <c>hasSquadVehicle</c>, a flag nothing reads. A squad that got
    /// out went on as a mounted squad on foot: no digging in at a flag it held ("Squad dig in while
    /// in vehicle, ignore." on every order tick), no turning to cover when shot at, no boarding
    /// another vehicle, no merging into a nearby squad, and the commander planned for it as driving.
    /// </para>
    /// <para>
    /// <b>A source scan, like <c>SeatedDeathTests</c>, and for its reason:</b> <c>Squad</c> is an
    /// <c>Assembly-CSharp</c> type no test assembly compiles.
    /// </para>
    /// </remarks>
    public class SquadVehicleStateTests
    {
        [Fact]
        public void ASquadHasAVehicleOnlyWhileBoardingOrSeatedInIt()
        {
            string body = Method("HasVehicle").Body!.ToString();

            Assert.Contains("State.EnterVehicle", body);
            Assert.Contains("IsSeated()", body);
            Assert.Contains("seat.vehicle == squadVehicle", body);
        }

        [Fact]
        public void ASquadThatGetsOutLetsGoOfItsVehicleAndItsSeats()
        {
            string body = Method("ExitVehicle").Body!.ToString();

            Assert.Contains("squadVehicle = null", body);
            Assert.Contains("DropSeatClaim", body);
        }

        /// <summary>
        /// A squad gives up on a vehicle it can no longer board: wrecked, driven by someone outside
        /// it, or not boarded in time. The original gave up on a wrecked one only, and no other order
        /// reaches a squad in EnterVehicle, so the bot soak found squads standing for over a minute
        /// where a vehicle had been, while another squad drove it 648 m away.
        /// </summary>
        [Fact]
        public void ASquadGivesUpOnAVehicleItCanNoLongerBoard()
        {
            string failed = Method("BoardingFailed").Body!.ToString();
            Assert.Contains("State.EnterVehicle", failed);
            Assert.Contains("squadVehicle.dead", failed);
            Assert.Contains("IsMember(driver)", failed);
            Assert.Contains("BoardingTimeoutSeconds", failed);

            string orders = AiMethod("AiOrders").Body!.ToString();
            Assert.Contains("squad.BoardingFailed()", orders);
            Assert.Contains("squad.GiveUpBoarding()", orders);
        }

        /// <summary>
        /// A squad aboard, or boarding, holds a flag it has taken from where it is. It used to be
        /// told to dig in, which a crew cannot, and logged "Squad dig in while in vehicle, ignore."
        /// on every order tick: 24, 11 and 6 times in ten minutes on the three maps.
        /// </summary>
        [Fact]
        public void AMountedSquadHoldsATakenFlagWithoutDiggingIn()
        {
            string body = Method("FollowCommand").Body!.ToString();
            int hold = body.IndexOf("commandTarget.owner == team && commandTarget.IsSafe()", StringComparison.Ordinal);
            Assert.True(hold >= 0, "the hold-the-taken-flag branch is gone");

            string branch = body.Substring(hold);
            int vehicleCheck = branch.IndexOf("HasVehicle()", StringComparison.Ordinal);
            int digIn = branch.IndexOf("DigIn()", StringComparison.Ordinal);
            Assert.True(vehicleCheck >= 0 && vehicleCheck < digIn, "DigIn is reached before asking whether the squad is mounted");
        }

        /// <summary>
        /// A member that reached the spot a vehicle was sent to it from goes to where it is now. It
        /// used to need to be within 4 m of the vehicle where it stood, and stood still otherwise.
        /// </summary>
        [Fact]
        public void AMemberFollowsAVehicleThatHasMoved()
        {
            string loop = AiMethod("AiVehicle").Body!.ToString();

            Assert.Contains("Goto(targetVehicle.transform.position)", loop);
        }

        private static MethodDeclarationSyntax Method(string name) =>
            Parse("Assembly-CSharp/Squad.cs").DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == name && m.ParameterList.Parameters.Count == 0);

        private static MethodDeclarationSyntax AiMethod(string name) =>
            Parse("Assembly-CSharp/AiActorController.cs").DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == name && m.ParameterList.Parameters.Count == 0);

        private static SyntaxNode Parse(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot();
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
