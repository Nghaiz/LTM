using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Regression pins over <c>Assembly-CSharp/*.cs</c>, read as text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These prove SHAPE, not BEHAVIOUR, and the distinction is not a hedge.</b>
    /// <c>Assembly-CSharp</c> does not compile under <c>dotnet test</c> — it needs
    /// <c>UnityEngine</c> — so a behavioural assertion on <c>Car.FixedUpdate</c> requires
    /// opening the Editor. A test that reads the file and asserts the invariant costs
    /// milliseconds and catches the failure that actually happens: someone reintroducing the
    /// per-frame form during an unrelated edit, months later, with nobody watching.
    /// </para>
    /// <para>
    /// Every test here is paired with a behavioural check on Dev A's Editor list
    /// (phase-v0 § 7). Neither half is sufficient alone.
    /// </para>
    /// <para>
    /// <b>When one of these goes red after a harmless refactor</b>, that is a prompt to
    /// re-read the invariant in the failure message, not to delete the test. They match on the
    /// invariant (no <c>WaitForSeconds</c> in this method; a <c>fixedDeltaTime</c> is present),
    /// never on formatting.
    /// </para>
    /// </remarks>
    public sealed class VehicleSourceInvariantTests
    {
        // ------------------------------------------------------------------ Task 2: timestep

        [Fact]
        public void CarDriveCodeIsNotInUpdate()
        {
            string source = ReadScript("Car.cs");
            string update = MethodBody(source, "Car.cs", "private void Update()");

            Assert.Contains("protected override void FixedUpdate()", source);

            // Reads of steerAngle are fine in Update -- the steering-wheel prop is cosmetic.
            // Writes to a WheelCollider are not: PhysX samples those once per fixed step.
            AssertAbsent(update, ".motorTorque", "Car.Update", "wheel torque is a physics write");
            AssertAbsent(update, ".brakeTorque", "Car.Update", "wheel braking is a physics write");
            AssertAbsent(update, ".steerAngle", "Car.Update", "wheel steering is a physics write");
            AssertAbsent(update, "CarInput()", "Car.Update", "driver input is consumed at fixed rate");
        }

        [Fact]
        public void HelicopterRotorSpeedIsNotIntegratedInUpdate()
        {
            string source = ReadScript("Helicopter.cs");
            string update = MethodBody(source, "Helicopter.cs", "private void Update()");

            // rotorSpeed multiplies every force in FixedUpdate, so integrating it per frame
            // made lift itself framerate-dependent.
            Assert.DoesNotMatch(new Regex(@"rotorSpeed\s*=(?!=)"), update);
            AssertAbsent(update, "Damage(", "Helicopter.Update", "damage-per-frame is not a rate");
            Assert.Contains("Time.fixedDeltaTime * 0.3f", source);
            Assert.Contains("Damage(Time.fixedDeltaTime * 30f)", source);
        }

        // ------------------------------------------------------------------ Task 3: turrets

        [Theory]
        [InlineData("TankTurret.cs")]
        [InlineData("MountedTurret.cs")]
        public void TurretSlewUsesADeltaTime(string file)
        {
            string source = ReadScript(file);

            // Before V0 a grep for Time.deltaTime across both files returned ZERO hits -- the
            // slew was a raw per-frame delta with no time term at all. That is the bug.
            Assert.Contains("Time.fixedDeltaTime", source);
            Assert.Contains("TurretAimCore.Step(ref _aim", source);

            string update = MethodBody(source, file, "protected override void Update()");
            AssertAbsent(update, "TurretAimCore.Step", $"{file} Update", "aim integrates at fixed rate (D4)");
            AssertAbsent(update, "Time.deltaTime", $"{file} Update", "no frame-rate term may reach the aim");
        }

        [Theory]
        [InlineData("TankTurret.cs")]
        [InlineData("MountedTurret.cs")]
        public void TurretsExposeAPublicAimSetter(string file)
        {
            string source = ReadScript(file);

            // Neither turret had any setter at all before V0: both GetInput() methods were
            // private and read Input.GetAxis directly, so nothing outside the class could aim.
            Assert.Contains("public void SetAim(float yaw, float pitch)", source);
            Assert.Contains("public float Yaw", source);
            Assert.Contains("public float Pitch", source);
            Assert.Contains("protected virtual Vector2 GetInput()", source);
        }

        [Theory]
        [InlineData("TankTurret.cs")]
        [InlineData("MountedTurret.cs")]
        public void TurretAimIsNotReadBackOutOfAnEngineObject(string file)
        {
            // Stripped: the files' own comments name MAX_TURN_DELTA to explain what it was and
            // why 5 degrees PER FRAME was the bug. Documenting an invariant must not break it.
            string source = StripComments(ReadScript(file));

            // D3: the joint/transform is an OUTPUT. Reading the angle back out round-trips
            // through Quaternion.eulerAngles, which is not injective. The one permitted read is
            // the Awake seed, which is why these patterns are the ACCUMULATING forms.
            Assert.DoesNotMatch(new Regex(@"localEulerAngles\w*\.\w\s*\+="), source);
            Assert.DoesNotContain("targetRotation.eulerAngles;", source);
            Assert.DoesNotContain("MAX_TURN_DELTA", source);
        }

        // ------------------------------------------------------------------ Task 4: health

        [Fact]
        public void VehicleHasSingleHealthWritePath()
        {
            string source = StripComments(ReadScript("Vehicle.cs"));

            // Matches `health =` but not maxHealth/newHealth/showDamage, and not `==`.
            MatchCollection writes = Regex.Matches(source, @"(?<![A-Za-z0-9_])health\s*=(?!=)");
            Assert.True(
                writes.Count == 1,
                $"Vehicle.cs has {writes.Count} assignments to health; exactly one is allowed, inside ApplyHealth. " +
                "Two write paths each running their own burning/particle ladder is the derived-state " +
                "divergence development-principles.md forbids.");

            string applyHealth = MethodBody(
                source, "Vehicle.cs",
                "private void ApplyHealth(float newHealth, float appliedDamage, int attackerActorId)");
            Assert.Matches(new Regex(@"(?<![A-Za-z0-9_])health\s*=(?!=)"), applyHealth);

            Assert.Contains("public void SetHealthAuthoritative(float value)", source);
            Assert.Contains("public float Health", source);
            Assert.Contains("public float MaxHealth", source);
        }

        [Fact]
        public void VehicleDamageCarriesAttacker()
        {
            string source = ReadScript("Vehicle.cs");

            Assert.Contains("public void Damage(float amount, int attackerActorId)", source);
            // The one-argument overload must survive so every existing call site compiles
            // untouched -- AutoDamage, OnCollisionEnter, Helicopter's inverted-flight path and
            // ActorManager.Explode.
            Assert.Contains("public void Damage(float amount)", source);
            Assert.Contains("public const int NoAttacker = -1;", source);
        }

        [Fact]
        public void BurnCountdownUsesFixedDeltaTime()
        {
            string source = ReadScript("Vehicle.cs");
            string fixedUpdate = MethodBody(StripComments(source), "Vehicle.cs", "protected virtual void FixedUpdate()");

            // Time.deltaTime returns fixedDeltaTime inside the fixed loop, so the original was
            // right by accident. V4 drives this countdown from the 30 Hz netcode accumulator,
            // where the accident stops holding.
            AssertAbsent(fixedUpdate, "Time.deltaTime", "Vehicle.FixedUpdate", "a fixed step must not read a frame delta");
            Assert.Contains("burnTime -= Time.fixedDeltaTime;", source);
        }

        // ------------------------------------------------------------------ Task 5: clamping

        [Theory]
        [InlineData("Boat.cs", "BoatInput")]
        [InlineData("Tank.cs", "CarInput")]
        [InlineData("Car.cs", "CarInput")]
        public void VehicleInputIsClampedOnEveryVehicle(string file, string accessor)
        {
            string source = StripComments(ReadScript(file));

            Assert.Matches(new Regex(@"Clamp2\(Driver\(\)\.controller\." + accessor + @"\(\)\)"), source);
            Assert.DoesNotMatch(new Regex(@"=\s*Driver\(\)\.controller\." + accessor + @"\(\)\s*;"), source);
        }

        [Fact]
        public void HelicopterInputIsClamped()
        {
            string source = StripComments(ReadScript("Helicopter.cs"));
            Assert.Matches(new Regex(@"Clamp4\(Driver\(\)\.controller\.HelicopterInput\(\)\)"), source);
        }

        [Fact]
        public void ClampRoutesThroughTheEngineFreeBoundary()
        {
            string source = StripComments(ReadScript("Vehicle.cs"));

            // Mathf.Clamp(float.NaN, -1f, 1f) returns NaN, so a Mathf-based Clamp2 is a range
            // limiter rather than a validation boundary.
            string clamp2 = MethodBody(source, "Vehicle.cs", "protected static Vector2 Clamp2(Vector2 v)");
            string clamp4 = MethodBody(source, "Vehicle.cs", "protected static Vector4 Clamp4(Vector4 v)");

            AssertAbsent(clamp2, "Mathf.Clamp(", "Vehicle.Clamp2", "Mathf.Clamp passes NaN through");
            AssertAbsent(clamp4, "Mathf.Clamp(", "Vehicle.Clamp4", "Mathf.Clamp passes NaN through");
            Assert.Contains("VehicleInputClamp.Axis", clamp2);
            Assert.Contains("VehicleInputClamp.Axis", clamp4);
        }

        [Fact]
        public void BoatUsesLocalTorqueAxis()
        {
            string source = StripComments(ReadScript("Boat.cs"));

            // AddRelativeTorque interprets its argument in the BODY's local space; transform.up
            // is world-space. The two coincide only while the hull is level and unrotated.
            Assert.DoesNotContain("AddRelativeTorque(base.transform.", source);
            Assert.Contains("AddRelativeTorque(Vector3.up * turnSpeed", source);
        }

        // ------------------------------------------------------------------ Task 6: AutoDamage

        [Fact]
        public void RepairDoesNotStackAutoDamage()
        {
            string source = StripComments(ReadScript("Vehicle.cs"));
            string repair = MethodBody(source, "Vehicle.cs", "public bool Repair(float amount)");

            // InvokeRepeating STACKS. Repair used to arm unconditionally, including on an
            // occupied vehicle, and OccupantLeft then armed a second one without cancelling.
            Assert.Matches(
                new Regex(@"if\s*\(IsEmpty\(\)\)\s*\{[^}]*InvokeRepeating\(""AutoDamage""", RegexOptions.Singleline),
                repair);

            string occupantLeft = MethodBody(source, "Vehicle.cs", "public void OccupantLeft(Seat seat, Actor leaver)");
            Assert.Matches(
                new Regex(@"CancelInvoke\(""AutoDamage""\);\s*InvokeRepeating\(""AutoDamage""", RegexOptions.Singleline),
                occupantLeft);

            // The magic numbers at the three call sites become the constants that were already
            // declared and unused. Drift between two literal copies is how this bug got in.
            Assert.DoesNotMatch(new Regex(@"InvokeRepeating\(""AutoDamage"",\s*50f"), source);
            Assert.DoesNotContain("maxHealth * 0.07f", source);
            Assert.Contains("AUTO_DAMAGE_START_TIME, AUTO_DAMAGE_PERIOD", source);
            Assert.Contains("maxHealth * AUTO_DAMAGE_PERCENT", source);
        }

        // ------------------------------------------------------------------ Task 7: explosion

        [Fact]
        public void ExplosionFalloffRoutesThroughExplosionRanges()
        {
            string source = StripComments(ReadScript("ActorManager.cs"));

            // Mathf.Clamp01 SATURATES rather than excludes, so normalizing over damageRange
            // while querying over balanceRange made the 6-9 m band a flat plateau.
            Assert.DoesNotContain("Mathf.Clamp01(magnitude / configuration.damageRange)", source);
            Assert.DoesNotContain("Mathf.Clamp01(num3 / configuration.damageRange)", source);
            Assert.Contains("new ExplosionRanges(configuration.damageRange, configuration.balanceRange)", source);
            Assert.Contains("ranges.TryGetDamageT(", source);
            Assert.Contains("ranges.GetBalanceT(", source);
        }

        // ------------------------------------------------------------------ Task 8: seat timer

        [Fact]
        public void ActorSeatCollisionTimerIsTickCounted()
        {
            string source = StripComments(ReadScript("Actor.cs"));

            // A wall-clock coroutine that re-reads seat state when it wakes is a race against
            // every network-driven seat change arriving inside its window.
            Assert.DoesNotContain("WaitForSeconds", source);
            Assert.DoesNotContain("ReactivateCollisionsWith", source);

            Assert.Contains("private TickTimer collisionReactivateTimer;", source);
            Assert.Contains("REACTIVATE_COLLISION_TICKS", source);
            Assert.Contains("collisionReactivateTimer.Cancel();", source);
            Assert.Contains("collisionReactivateTimer.Tick()", source);
        }

        // ------------------------------------------------------------------ Task 9: headless

        /// <summary>
        /// The ten sites from the design doc's headless audit. A dedicated server strips
        /// renderers, particle systems and audio sources, so each of these was an NRE on a
        /// build that has to survive spawn, damage, death and respawn.
        /// </summary>
        [Theory]
        // file,             required guard,                                      site
        [InlineData("VehicleSpawner.cs", "Renderer marker = GetComponent<Renderer>();", "spawner marker mesh")]
        [InlineData("VehicleSpawner.cs", "GameManager.instance == null ||", "noVehicles suppression")]
        [InlineData("Vehicle.cs", "if (damageParticles != null)", "damage smoke, play and stop")]
        [InlineData("Vehicle.cs", "if (impactAudio != null)", "collision impact audio")]
        [InlineData("Vehicle.cs", "if (deathParticles != null)", "death particles")]
        [InlineData("Vehicle.cs", "if (explosionSound != null)", "explosion sound")]
        [InlineData("Vehicle.cs", "ActorManager.instance != null &&", "debug OnGUI, dereferenced before its own guard")]
        [InlineData("Vehicle.cs", "if (spawner != null)", "scene-placed vehicle has no spawner")]
        [InlineData("Helicopter.cs", "if (rotor != null)", "rotor transform and its renderers")]
        [InlineData("Helicopter.cs", "if (solidRotor != null)", "solid rotor renderer, dereferenced every frame")]
        [InlineData("Helicopter.cs", "if (blurredRotor != null)", "blurred rotor renderer, dereferenced every frame")]
        public void HeadlessDereferencesAreGuarded(string file, string guard, string site)
        {
            string source = ReadScript(file);
            Assert.True(source.Contains(guard, StringComparison.Ordinal),
                $"{file}: the headless guard for {site} is gone. Expected to find: {guard}");
        }

        [Fact]
        public void HeadlessGuardsProtectCosmeticCallsOnly()
        {
            string explode = MethodBody(StripComments(ReadScript("Vehicle.cs")), "Vehicle.cs", "protected virtual void Explode()");

            // The impulse is what throws the wreck -- gameplay, and it must still run on a
            // headless build. Where a guarded block also contains gameplay, the gameplay stays
            // outside the guard; where a field is genuinely required, it is NOT guarded, so a
            // bad prefab still throws (development-principles.md, "Errors Over Silent Fallbacks").
            int impulseAt = explode.IndexOf("AddForce(", StringComparison.Ordinal);
            int firstGuardAt = explode.IndexOf("!= null", StringComparison.Ordinal);

            Assert.True(impulseAt >= 0, "Vehicle.Explode no longer applies the wreck impulse.");
            Assert.True(firstGuardAt >= 0, "Vehicle.Explode has no null guards at all.");
            Assert.True(impulseAt < firstGuardAt,
                "Vehicle.Explode's rigidbody impulse is gameplay and must run before, and outside of, the cosmetic guards.");
        }

        // ------------------------------------------------------------------ criterion 14

        /// <summary>
        /// Acceptance criterion 14 in its mechanically checkable half: no file under
        /// <c>Vehicles/</c> may reach for the engine, allocate, or use the constructs
        /// conventions.md § 3.2 bans on the hot path.
        /// </summary>
        [Fact]
        public void TheEngineFreeSeamStaysEngineFree()
        {
            string folder = Path.Combine(RepoRoot(), "Ironfront.Net.Replication", "Vehicles");
            string[] files = Directory.GetFiles(folder, "*.cs");

            Assert.True(files.Length >= 6, $"Expected the six seam types under {folder}, found {files.Length} files.");

            foreach (string path in files)
            {
                string body = StripComments(File.ReadAllText(path));
                string name = Path.GetFileName(path);

                AssertAbsent(body, "UnityEngine", name, "the seam must build without Unity");
                AssertAbsent(body, "System.Linq", name, "conventions.md § 3.2");
                AssertAbsent(body, "foreach", name, "conventions.md § 3.2");

                // Allocation, named concretely rather than by banning the `new` keyword: these
                // are structs and `new TurretAimState()` allocates nothing. What would allocate
                // is a heap collection or an array copy.
                AssertAbsent(body, "new[]", name, "no allocation on the hot path");
                AssertAbsent(body, "List<", name, "no allocation on the hot path");
                AssertAbsent(body, "Dictionary<", name, "no allocation on the hot path");
                AssertAbsent(body, ".ToArray()", name, "no allocation on the hot path");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void AssertAbsent(string haystack, string needle, string where, string why)
        {
            Assert.True(
                !haystack.Contains(needle, StringComparison.Ordinal),
                $"{where} must not contain \"{needle}\" — {why}.");
        }

        private static string ReadScript(string fileName)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", fileName);

            Assert.True(File.Exists(path), $"Expected to find {fileName} at {path}.");
            return File.ReadAllText(path);
        }

        /// <summary>Walks up from the test binary to the directory holding Ironfront.sln.</summary>
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

        /// <summary>
        /// The body of one method, brace-matched from its signature. Comments and string
        /// literals are removed first so a brace inside either cannot unbalance the scan.
        /// </summary>
        private static string MethodBody(string source, string file, string signature)
        {
            string stripped = StripComments(source);
            int at = stripped.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{file}: no method with signature \"{signature}\". Was it renamed?");

            int open = stripped.IndexOf('{', at + signature.Length);
            Assert.True(open >= 0, $"{file}: \"{signature}\" has no body.");

            int depth = 0;
            for (int i = open; i < stripped.Length; i++)
            {
                if (stripped[i] == '{') depth++;
                else if (stripped[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return stripped.Substring(open + 1, i - open - 1);
                }
            }

            throw new InvalidOperationException($"{file}: unbalanced braces after \"{signature}\".");
        }

        /// <summary>
        /// Blanks comment text with spaces, preserving every offset so an index into the result
        /// still lines up with the original.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what lets a test assert "Actor.cs contains no <c>WaitForSeconds</c>" while
        /// the file's own comment explains what <c>WaitForSeconds</c> used to do there. Without
        /// it every invariant would be broken by the comment documenting it.
        /// </para>
        /// <para>
        /// <b>String literals are deliberately left intact</b> — several invariants are
        /// literally about them (<c>InvokeRepeating("AutoDamage", …)</c>). A brace inside a
        /// string would therefore unbalance <see cref="MethodBody"/>; no file this suite reads
        /// contains one, and <see cref="MethodBody"/> throws rather than returning nonsense if
        /// that ever changes.
        /// </para>
        /// </remarks>
        private static string StripComments(string source)
        {
            char[] output = source.ToCharArray();
            int i = 0;

            while (i < output.Length)
            {
                char c = output[i];

                if (c == '/' && i + 1 < output.Length && output[i + 1] == '/')
                {
                    while (i < output.Length && output[i] != '\n') { output[i] = ' '; i++; }
                }
                else if (c == '/' && i + 1 < output.Length && output[i + 1] == '*')
                {
                    while (i + 1 < output.Length && !(output[i] == '*' && output[i + 1] == '/'))
                    {
                        if (output[i] != '\n') output[i] = ' ';
                        i++;
                    }
                    if (i < output.Length) { output[i] = ' '; i++; }
                    if (i < output.Length) { output[i] = ' '; i++; }
                }
                else
                {
                    i++;
                }
            }

            return new string(output);
        }
    }
}
