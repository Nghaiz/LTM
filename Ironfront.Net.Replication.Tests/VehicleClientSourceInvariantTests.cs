using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// V5's named hazards, pinned over the Unity sources as text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These prove SHAPE, not BEHAVIOUR</b>, exactly as
    /// <see cref="VehicleSourceInvariantTests"/> does and for the same reason: nothing under
    /// <c>Assets/Scripts</c> compiles under <c>dotnet test</c>, so a behavioural assertion needs
    /// the Editor. What a text pin catches is the failure that actually happens — somebody
    /// reintroducing the removed form during an unrelated edit, months later, with nobody
    /// watching.
    /// </para>
    /// <para>
    /// <b>Each one is written so it can go red.</b> A check nobody has watched fail is unproven;
    /// where these assert an absence they also assert the presence of the thing that replaced
    /// it, so deleting the feature outright fails rather than passing vacuously.
    /// </para>
    /// </remarks>
    public sealed class VehicleClientSourceInvariantTests
    {
        // ---------------------------------------------------- V5-D9: no OptionsUi on the server

        [Fact]
        public void NoOptionsUiReadOnAnyServerRolePath()
        {
            // OptionsUi.GetOptions() reads this user's PlayerPrefs -- mouse sensitivity and four
            // helicopter invert flags. On a headless authority that is an NRE waiting for the
            // first networked helicopter AND an authority hole: the server would be scaling a
            // client's control vector by a number only that client is entitled to choose.
            // V5-D9 puts the scaling on the sender, and this is the gate that keeps it there.
            // Matched on the accessor call rather than the type name, so a comment explaining
            // why the server must not read it does not fail the check that says so.
            const string OptionsRead = "OptionsUi.GetOptions(";

            foreach (string file in ScriptsUnder("Net", "Server"))
            {
                Assert.False(
                    File.ReadAllText(file).Contains(OptionsRead, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} reads OptionsUi on a server-role path. "
                    + "Client-local options must be applied by the sender (V5-D9).");
            }

            // The one file that legitimately reads them is LocalInputSource, and it is never
            // installed at server role.
            string controller = ReadScript("Assembly-CSharp", "FpsActorController.cs");

            Assert.Matches(
                new Regex(@"inputSource\s*==\s*NullInputSource\.Instance\s*&&\s*!\s*Ironfront\.Net\.Unity\.NetContext\.IsServer"),
                controller);

            // And the network source has no route to them at all.
            Assert.DoesNotContain(
                OptionsRead, ReadScript("Net", "Input", "NetInputSource.cs"), StringComparison.Ordinal);
        }

        [Fact]
        public void TheHelicopterScalingLivesOnTheSenderRatherThanTheController()
        {
            string local = ReadScript("Net", "Input", "LocalInputSource.cs");
            string controller = ReadScript("Assembly-CSharp", "FpsActorController.cs");

            // Moved, not deleted: the sensitivity product and all four invert flags now live in
            // the one place UnityEngine.Input is allowed to be read.
            Assert.Contains("helicopterSensitivity", local, StringComparison.Ordinal);
            Assert.Contains("heliInvertPitch", local, StringComparison.Ordinal);
            Assert.Contains("heliInvertYaw", local, StringComparison.Ordinal);
            Assert.Contains("heliInvertRoll", local, StringComparison.Ordinal);
            Assert.Contains("heliInvertThrottle", local, StringComparison.Ordinal);

            // HelicopterInput() is component order and nothing else now. The raw Input.GetAxis
            // branch it used to carry was the accepted debt V5-D8 closes.
            string helicopterInput = MethodBody(
                controller, "FpsActorController.cs", "public override Vector4 HelicopterInput()");

            Assert.DoesNotContain("Input.GetAxis", helicopterInput, StringComparison.Ordinal);
            Assert.DoesNotContain("OptionsUi.GetOptions(", helicopterInput, StringComparison.Ordinal);
            Assert.Contains("inputSource.HeliYaw", helicopterInput, StringComparison.Ordinal);
            Assert.Contains("inputSource.HeliCollective", helicopterInput, StringComparison.Ordinal);
            Assert.Contains("inputSource.HeliRoll", helicopterInput, StringComparison.Ordinal);
            Assert.Contains("inputSource.HeliPitch", helicopterInput, StringComparison.Ordinal);
        }

        // -------------------------------------------- V5-D7: aiControlled must not move

        [Fact]
        public void AiControlledIsUnchangedForANetworkedDriver()
        {
            // Actor.aiControlled is frozen in Awake from an exact type comparison and then read
            // by UI, LOD and weapon culling. A new ActorController subclass for network input
            // would flip it for every networked player at once -- silently, because nothing
            // about the symptom points at the controller.
            string actor = ReadScript("Assembly-CSharp", "Actor.cs");
            Assert.Contains(
                "aiControlled = controller.GetType() == typeof(AiActorController);",
                actor, StringComparison.Ordinal);

            var subclassPattern = new Regex(@":\s*(ActorController|FpsActorController|AiActorController)\b");

            foreach (string file in ScriptsUnder("Net"))
            {
                Assert.False(
                    subclassPattern.IsMatch(File.ReadAllText(file)),
                    $"{Path.GetFileName(file)} declares an ActorController subclass. V5-D7 "
                    + "extends the IInputSource seam instead, precisely so aiControlled does "
                    + "not move.");
            }

            // The seam that replaces it, present rather than merely un-broken. It lives in
            // NetBindings/ because Ironfront.Net.Unity.Server is an asmdef and no asmdef can
            // reference Assembly-CSharp, where the controller and the input source both are.
            Assert.Contains(
                "SetInputSource",
                ReadScript("NetBindings", "NetDriverInputSink.cs"),
                StringComparison.Ordinal);
        }

        [Fact]
        public void TheInputSourceSeamHasAProductionCallSite()
        {
            // Before V5 a repository-wide grep for SetInputSource returned the definition, one
            // comment and the test project. Nothing noticed, because server movement bypasses
            // the controller entirely -- so the moment a networked player drove, the vehicle
            // read a keyboard that was not there.
            var callSites = new List<string>();

            // The whole script tree, not just Net/: the call site sits in NetBindings/, outside
            // every asmdef, because that is the only assembly that can see both halves.
            foreach (string file in ScriptsUnder())
            {
                string text = File.ReadAllText(file);
                if (text.Contains(".SetInputSource(", StringComparison.Ordinal))
                    callSites.Add(Path.GetFileName(file));
            }

            Assert.NotEmpty(callSites);
            Assert.Contains("NetDriverInputSink.cs", callSites);
        }

        // ------------------------------------------ V5-D3: the client drive-path guards

        [Theory]
        [InlineData("Car.cs")]
        [InlineData("Boat.cs")]
        [InlineData("Tank.cs")]
        [InlineData("Helicopter.cs")]
        public void EveryVehicleSubtypeGuardsItsDrivePathOnNetworkDriven(string fileName)
        {
            // A replicated vehicle whose drive path still runs steers a body PhysX will not
            // move, and integrates values -- steerAngle, rotorSpeed -- from an input this peer
            // does not have. Both are why the subtype tail exists.
            string source = ReadScript("Assembly-CSharp", fileName);

            Assert.Matches(new Regex(@"if\s*\(\s*NetworkDriven\s*\)"), source);
        }

        [Fact]
        public void ARemoteVehicleGoesKinematic()
        {
            // The whole of V5-D3: a dynamic replicated body runs local PhysX AGAINST the
            // snapshots, and the jitter that produces looks exactly like a network problem.
            string vehicle = ReadScript("Assembly-CSharp", "Vehicle.cs");

            Assert.Contains("public void SetNetworkDriven(bool value)", vehicle, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"rigidbody\.isKinematic\s*=\s*value"), vehicle);
        }

        [Fact]
        public void TheTwoCosmeticsThatLostTheirSimulationAreDrivenFromTheSubtypeTail()
        {
            // Design section 5 reserved the tail for exactly these two. Without them a remote
            // car corners with its steering wheel dead centre and a remote helicopter flies past
            // with a stationary, solid rotor.
            Assert.Contains(
                "steerAngle = VehicleSubtypeTail.UnpackSteerAngle",
                ReadScript("Assembly-CSharp", "Car.cs"),
                StringComparison.Ordinal);

            Assert.Contains(
                "rotorSpeed = VehicleSubtypeTail.UnpackHelicopter",
                ReadScript("Assembly-CSharp", "Helicopter.cs"),
                StringComparison.Ordinal);
        }

        [Fact]
        public void AClientDoesNotSpawnItsOwnVehicles()
        {
            // Every vehicle in a networked world arrives from S_VEHICLE_SPAWN with the id the
            // server gave it. Letting the local pad run too puts two vehicles on it -- one
            // replicated, one on a spawn timer with no reason to agree with the server's -- and
            // neither looks wrong on its own.
            string spawner = ReadScript("Assembly-CSharp", "VehicleSpawner.cs");

            Assert.Matches(
                new Regex(@"VehiclesAreSuppressed\(\)[\s\S]{0,400}?NetContext\.IsClient"),
                spawner);

            Assert.Contains(
                "OnVehicleSpawn",
                ReadScript("Net", "Client", "RemoteVehicleRegistry.cs"),
                StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ helpers

        private static string ReadScript(params string[] relativeParts)
        {
            var parts = new List<string> { RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts" };
            parts.AddRange(relativeParts);

            string path = Path.Combine(parts.ToArray());
            Assert.True(File.Exists(path), $"Expected to find a script at {path}.");
            return File.ReadAllText(path);
        }

        private static IEnumerable<string> ScriptsUnder(params string[] relativeParts)
        {
            var parts = new List<string> { RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts" };
            parts.AddRange(relativeParts);

            string root = Path.Combine(parts.ToArray());
            Assert.True(Directory.Exists(root), $"Expected a directory at {root}.");

            string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);

            // A scan that found nothing must never read as a pass -- the same trap
            // tools/UnitySyntaxCheck guards against.
            Assert.NotEmpty(files);
            return files;
        }

        /// <summary>The body of one method, brace-matched from its signature.</summary>
        private static string MethodBody(string source, string fileName, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{fileName} no longer declares '{signature}'.");

            int open = source.IndexOf('{', start);
            Assert.True(open >= 0, $"{fileName}: '{signature}' has no body.");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"{fileName}: '{signature}' has an unbalanced body.");
            return string.Empty;
        }

        private static string RepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Ironfront.sln found walking up from {AppContext.BaseDirectory}.");
        }
    }
}
