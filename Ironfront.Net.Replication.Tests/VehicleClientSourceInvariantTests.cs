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

            // SCOPE WIDENED FROM Net/Server TO ALL OF Net/ BY PHASE C2, and the widening is the
            // point rather than tidying. LocalInputSource used to be the one file under Net/
            // that read OptionsUi directly, so the scan had to stop at Net/Server to leave it
            // alone. C2 moved that read behind ILocalInputEnvironment, so Net/ now contains zero
            // reads and the gate can say so -- a strictly stronger claim that still contains
            // the V5-D9 one. For Net/Input it is also structurally guaranteed: that folder is
            // its own assembly and cannot name OptionsUi at all (check-net-layering RULE 5b).
            // If this ever fails for a file under Net/Input, the asmdef is gone.
            foreach (string file in ScriptsUnder("Net"))
            {
                Assert.False(
                    File.ReadAllText(file).Contains(OptionsRead, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} reads OptionsUi from under Net/. "
                    + "Client-local options must be applied by the sender (V5-D9), and since C2 "
                    + "they reach the sender through ILocalInputEnvironment rather than the UI "
                    + "class.");
            }

            // The one path that legitimately reads them is LocalInputSource, through the
            // binding, and it is never installed at server role.
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

            // Moved, not deleted: the sensitivity product and all four invert flags still live in
            // the one place UnityEngine.Input is allowed to be read.
            //
            // THE SPELLINGS CHANGED IN C2 AND THE INVARIANT DID NOT. These used to be
            // OptionsUi.Options field names (helicopterSensitivity, heliInvertPitch, ...) read
            // directly. Net/Input is now its own assembly and cannot name OptionsUi, so the same
            // five values arrive through HelicopterControlOptions and are spelled accordingly.
            // What this test pins is WHERE the scaling happens, not how the fields are cased --
            // so the names were updated rather than the assertions dropped.
            Assert.Contains("HelicopterSensitivity", local, StringComparison.Ordinal);
            Assert.Contains("InvertPitch", local, StringComparison.Ordinal);
            Assert.Contains("InvertYaw", local, StringComparison.Ordinal);
            Assert.Contains("InvertRoll", local, StringComparison.Ordinal);
            Assert.Contains("InvertThrottle", local, StringComparison.Ordinal);

            // "Moved, not deleted" now spans two files, so pin the far end too: the legacy field
            // names must still be read by SOMETHING, or the seam is dropping them on the floor
            // and every assertion above passes on a helicopter that no longer inverts.
            string binding = ReadScript("NetBindings", "LocalInputEnvironmentBinding.cs");

            Assert.Contains("helicopterSensitivity", binding, StringComparison.Ordinal);
            Assert.Contains("heliInvertPitch", binding, StringComparison.Ordinal);
            Assert.Contains("heliInvertYaw", binding, StringComparison.Ordinal);
            Assert.Contains("heliInvertRoll", binding, StringComparison.Ordinal);
            Assert.Contains("heliInvertThrottle", binding, StringComparison.Ordinal);

            // And the sender must not have kept a back channel to the UI class.
            Assert.DoesNotContain("OptionsUi", local, StringComparison.Ordinal);

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

        [Fact]
        public void TheVehicleStageIsInstalledByTheBootstrapRatherThanAuthored()
        {
            // wired-not-just-present: a component that has to be dragged onto a GameObject on
            // every map is a component that is missing on one of them, and the symptom --
            // vehicles that never move for this client while every other client sees them fine
            // -- reads as a netcode fault rather than an authoring one. Neither of these needs a
            // serialized reference, so there is nothing for an inspector to hold.
            string bootstrap = ReadScript("Net", "Client", "NetClientBootstrap.cs");

            Assert.Contains("EnsureVehicleStage();", bootstrap, StringComparison.Ordinal);
            Assert.Contains("AddComponent<RemoteVehicleRegistry>()", bootstrap, StringComparison.Ordinal);
            Assert.Contains("AddComponent<ClientVehicleStage>()", bootstrap, StringComparison.Ordinal);

            // And the fallback reaches the stage from configuration, not only from the
            // inspector -- otherwise a headless run has no way to flip it.
            Assert.Contains(
                "stage.ApplyConfiguration(Config.PredictLocalVehicle)",
                bootstrap, StringComparison.Ordinal);
        }

        [Fact]
        public void TheStageDoesNotOverwriteAConfigurationItWasAlreadyGiven()
        {
            // NetClientBootstrap runs at a much earlier execution order, so for a stage AUTHORED
            // into a scene it configures the component before that component's own Awake runs.
            // Re-applying the serialized default there would silently undo the environment
            // override on exactly the builds that have no Editor to set the field in.
            string stage = ReadScript("Net", "Client", "ClientVehicleStage.cs");

            Assert.Matches(
                new Regex(@"if\s*\(\s*!\s*_configured\s*\)\s*ApplyConfiguration"), stage);
        }

        // ------------------ X-46: a networked driver's input reaches a player-slot body

        [Theory]
        [InlineData("public override Vector2 CarInput()", "CarAxesFor")]
        [InlineData("public override Vector2 BoatInput()", "CarAxesFor")]
        [InlineData("public override Vector4 HelicopterInput()", "HelicopterAxesFor")]
        public void ASuspendedControllerReturnsTheNetworkRelayRatherThanTheBotsOpinion(
            string signature, string accessor)
        {
            // Ledger X-46. Every vehicle PULLS through `Driver().controller.<Kind>Input()`, and a
            // networked player's server-side body carries an AiActorController because
            // IronfrontNetBindings.CreatePlayerBody instantiates the bot prefab. So these three
            // overrides ARE the driver seam for every real networked driver -- and before this
            // fix they answered with a bot's pathfinding, or with nothing. Measured: 1,285
            // accepted C_VEHICLE_INPUT messages against a hull that never moved
            // (artifacts/lane-a/r5/r5-combat-05).
            string body = MethodBody(
                ReadScript("Assembly-CSharp", "AiActorController.cs"),
                "AiActorController.cs", signature);

            // FIRST, and that ordering is load-bearing: a suspended controller has no path
            // either, so a `hasPath` return placed above this one would answer zero before the
            // relay was ever consulted -- a symptom indistinguishable from the defect.
            Assert.Matches(
                new Regex(
                    @"\A\{\s*if\s*\(\s*!\s*base\.enabled\s*\)\s*\{\s*"
                    + @"return\s+NetVehicleAxisRelay\." + accessor + @"\(\s*this\s*\)\s*;"),
                body);

            // And it is the ONLY reach for the relay in this method, so an ENABLED controller --
            // a genuine bot -- cannot be steered by the network. O-D2.
            Assert.Single(Regex.Matches(body, @"NetVehicleAxisRelay\."));
        }

        [Fact]
        public void TheDriverInputSinkNoLongerReturnsNullForABodyWithNoFpsController()
        {
            // The remark this replaced predicted its own defect in writing -- "a networked PLAYER
            // reaching a driver seat without one means that vehicle will not respond to them at
            // all" -- and then that case turned out to be EVERY networked player, because a
            // player-slot body is the bot prefab. Nothing noticed until R2 gave the shipped
            // client a seat sender and R5 gave lane A one.
            string body = MethodBody(
                ReadScript("NetBindings", "NetDriverInputSink.cs"),
                "NetDriverInputSink.cs", "internal static IDriverInputSink Attach(GameObject gameObject)");

            // The controller path stays FIRST: on a listen server or in the Editor the driver
            // really does have one, and its IInputSource seam is what remembers the keyboard
            // source the player walks with.
            Assert.Matches(
                new Regex(
                    @"FpsActorController\s+controller\s*=[\s\S]{0,120}?"
                    + @"if\s*\(\s*controller\s*!=\s*null\s*\)\s*return\s+new\s+NetDriverInputSink"),
                body);

            // ... and the fallback exists rather than being a null the caller has to interpret.
            Assert.Contains("NetVehicleAxisRelay.Install(gameObject)", body, StringComparison.Ordinal);

            // The one surviving null is a destroyed body, which is the only thing
            // ServerVehicleInputBridge.UnreachableControllers should still count.
            Assert.Matches(
                new Regex(@"if\s*\(\s*gameObject\s*==\s*null\s*\)\s*return\s+null\s*;"), body);
        }

        [Fact]
        public void TheRelayIsNotAControllerAndNotAnInputSource()
        {
            // O-D1, and it is the same hazard AiControlledIsUnchangedForANetworkedDriver guards
            // one folder over: the relay lives in NetBindings/, which that test's Net/ scan does
            // not reach, so the constraint is re-asserted where the file actually is. A second
            // ActorController on the body would make GetComponent<ActorController>()
            // order-dependent AND flip Actor.aiControlled, which is frozen in Awake from an exact
            // type comparison and then read by UI, LOD and weapon culling.
            string relay = ReadScript("NetBindings", "NetVehicleAxisRelay.cs");

            Assert.DoesNotMatch(
                new Regex(@":\s*(ActorController|FpsActorController|AiActorController)"), relay);
            Assert.DoesNotContain("SetInputSource", relay, StringComparison.Ordinal);
            Assert.Contains(": MonoBehaviour", relay, StringComparison.Ordinal);
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
