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
    /// Every test here is paired with a behavioural check on the client track's Editor list
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

        /// <summary>
        /// Acceptance criterion 3: the authoritative aim is never recovered from an engine
        /// object.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Pinned by WHERE the aim is written rather than by banning an <c>eulerAngles</c>
        /// substring, because the substring test is the one that passes for the wrong reason.
        /// <c>MountedTurret.Update</c> legitimately reads <c>localEulerAngles</c> to preserve
        /// the two components it does not own before writing the one it does; a text ban would
        /// have to carve that out, and the carve-out is where a real read-back hides.
        /// </para>
        /// <para>
        /// <b>The V0 implementation seeds <c>_aim</c> from the authored pose in <c>Awake</c>,
        /// which the phase file does not specify.</b> Without it a turret snaps to (0, 0) on
        /// the first fixed step. It is a one-time initialization, not a per-step round trip,
        /// and this test is what holds it to that: the seed may exist, and it may exist only
        /// there. The deviation is recorded in the phase file § 7.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("TankTurret.cs")]
        [InlineData("MountedTurret.cs")]
        public void TurretAimIsWrittenOnlyBySeedSetterAndStep(string file)
        {
            // Stripped: the files' own comments name MAX_TURN_DELTA to explain what it was and
            // why 5 degrees PER FRAME was the bug. Documenting an invariant must not break it.
            string source = StripComments(ReadScript(file));

            (int start, int end) awake = MethodSpan(source, file, "protected override void Awake()");
            (int start, int end) setAim = MethodSpan(source, file, "public void SetAim(float yaw, float pitch)");

            MatchCollection writes = Matches(source, @"_aim\.(Yaw|Pitch)\s*=(?!=)");
            Assert.True(writes.Count > 0, $"{file}: nothing writes _aim at all — was the field renamed?");

            foreach (System.Text.RegularExpressions.Match write in writes)
            {
                bool inSeed = write.Index > awake.start && write.Index < awake.end;
                bool inSetter = write.Index > setAim.start && write.Index < setAim.end;
                Assert.True(inSeed || inSetter,
                    $"{file}: _aim is assigned outside Awake and SetAim at offset {write.Index}. " +
                    "Every other write must go through TurretAimCore.Step(ref _aim, …) — a value " +
                    "recovered from a Quaternion has already round-tripped through eulerAngles, " +
                    "which is not injective.");
            }

            // The integration step itself must not touch the engine at all.
            string fixedUpdate = MethodBody(source, file, "private void FixedUpdate()");
            AssertAbsent(fixedUpdate, "eulerAngles", $"{file} FixedUpdate", "the aim integrates from the field, never from the engine");
            AssertAbsent(fixedUpdate, "targetRotation.", $"{file} FixedUpdate", "the joint is an output of _aim");

            Assert.DoesNotMatch(new Regex(@"localEulerAngles\w*\.\w\s*\+="), source);
            Assert.DoesNotContain("MAX_TURN_DELTA;", source);
        }

        /// <summary>
        /// The port must reproduce the shipped aim gain at the design framerate, not just be
        /// framerate-independent at some gain.
        /// </summary>
        /// <remarks>
        /// The shipped <c>Clamp(z - input.x, z - 5f, z + 5f)</c> is algebraically
        /// <c>z -= Clamp(input.x, -5f, +5f)</c> — a 1:1 mouse-degrees mapping with a speed
        /// limit, NOT a rate at full deflection. Feeding that raw number to a rate integrator
        /// that normalizes to [-1, 1] first multiplies sensitivity by MAX_TURN_DELTA. Every
        /// framerate-independence test still passes when that happens, because it compares
        /// new-against-new; only the conversion constant catches it.
        /// </remarks>
        [Theory]
        [InlineData("TankTurret.cs", "5f")]
        [InlineData("MountedTurret.cs", "10f")]
        public void TurretInputIsNormalizedByTheLegacyPerFrameStep(string file, string legacyStep)
        {
            string source = StripComments(ReadScript(file));

            Assert.Contains($"LEGACY_STEP_DEG = {legacyStep}", source);
            Assert.Contains("LEGACY_STEP_DEG * LEGACY_FRAME_RATE", source);

            string getInput = MethodBody(source, file, "protected virtual Vector2 GetInput()");

            // Bot facing is a STATE: divides by the constant the shipped code saturated at.
            Assert.Contains("/ LEGACY_STEP_DEG", getInput);
            // Mouse motion is a DISTANCE: divides by the arc this step can cover.
            Assert.Contains("Time.fixedDeltaTime", getInput);
        }

        /// <summary>
        /// <c>Input.GetAxis("Mouse X")</c> is the delta since the last RENDERED frame, refreshed
        /// once per <c>Update</c>. Sampling it from a fixed step drops motion at high framerates
        /// and double-counts it at low ones — which would reinstate, in a lossier form, exactly
        /// the framerate dependence this phase exists to remove.
        /// </summary>
        [Theory]
        [InlineData("TankTurret.cs")]
        [InlineData("MountedTurret.cs")]
        public void MouseDeltaIsLatchedPerFrameNotSampledPerStep(string file)
        {
            string source = StripComments(ReadScript(file));

            string update = MethodBody(source, file, "protected override void Update()");
            Assert.Contains("AccumulateMouseAim();", update);

            string accumulate = MethodBody(source, file, "private void AccumulateMouseAim()");
            Assert.Contains("_pendingMouseAim +=", accumulate);

            // The only place the per-frame delta may be read.
            MatchCollection reads = Matches(source, @"Input\.GetAxis\(");
            (int start, int end) span = MethodSpan(source, file, "private void AccumulateMouseAim()");
            foreach (System.Text.RegularExpressions.Match read in reads)
            {
                Assert.True(read.Index > span.start && read.Index < span.end,
                    $"{file}: Input.GetAxis is read outside AccumulateMouseAim at offset {read.Index}. " +
                    "A per-rendered-frame delta consumed from a fixed step is lossy.");
            }

            // And the latch drains exactly once per step, inside GetInput.
            string getInput = MethodBody(source, file, "protected virtual Vector2 GetInput()");
            Assert.Contains("_pendingMouseAim = Vector2.zero;", getInput);
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
        [InlineData("Vehicle.cs", "if (audio != null)", "engine audio, stopped and reset on death")]
        [InlineData("Vehicle.cs", "if (explosionSound != null)", "explosion sound")]
        [InlineData("Vehicle.cs", "ActorManager.instance != null &&", "debug OnGUI, dereferenced before its own guard")]
        [InlineData("Vehicle.cs", "if (spawner != null)", "scene-placed vehicle has no spawner")]
        [InlineData("Helicopter.cs", "if (rotor != null)", "rotor transform and its renderers")]
        [InlineData("Helicopter.cs", "if (solidRotor != null)", "solid rotor renderer, dereferenced every frame")]
        [InlineData("Helicopter.cs", "if (blurredRotor != null)", "blurred rotor renderer, dereferenced every frame")]
        public void HeadlessDereferencesAreGuarded(string file, string guard, string site)
        {
            // Stripped. Several of these guards are DOCUMENTED by a comment naming the guarded
            // field, so reading raw source would let the comment satisfy the test after someone
            // deleted the guard itself.
            string source = StripComments(ReadScript(file));
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

        // ------------------------------------------------------------------ criterion 13

        /// <summary>
        /// Acceptance criterion 13's second half: no file under <c>Vehicles/</c> may reach for
        /// the engine, allocate, or use the constructs conventions.md § 3.2 bans on the hot
        /// path. (Criterion <b>14</b>, zero wire change, is a property of the diff rather than
        /// of any file, so it is checked in the PR against <c>origin/main</c> — no unit test can
        /// see it.)
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

        private static MatchCollection Matches(string source, string pattern)
        {
            return Regex.Matches(source, pattern);
        }

        /// <summary>The body text of one method, brace-matched from its signature.</summary>
        private static string MethodBody(string source, string file, string signature)
        {
            string stripped = StripComments(source);
            (int start, int end) span = MethodSpan(stripped, file, signature);
            return stripped.Substring(span.start, span.end - span.start);
        }

        /// <summary>
        /// The half-open <c>[start, end)</c> offsets of one method's body within
        /// <paramref name="source"/>, brace-matched from its signature. Offsets are into the
        /// comment-stripped text, which <see cref="StripComments"/> keeps aligned with the
        /// original.
        /// </summary>
        /// <remarks>
        /// <b>Comments are stripped first; string literals are NOT</b> (several invariants are
        /// literally about string literals — <c>InvokeRepeating("AutoDamage", …)</c>). A brace
        /// inside a literal would therefore skew the depth counter: an unmatched <c>{</c>
        /// throws, which is loud and fine, but an unmatched <c>}</c> would close the scan early
        /// and yield a truncated body on which every absence assertion passes vacuously. Audited
        /// 2026-08-17 across all ten scanned files: zero braces in any string or char literal,
        /// zero verbatim/interpolated/raw strings, zero block comments, brace counts balanced
        /// per file. Nothing enforces that going forward — if a <c>$"…{x}…"</c> log line ever
        /// lands in one of these files, this helper is where it will surface.
        /// </remarks>
        private static (int start, int end) MethodSpan(string source, string file, string signature)
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
                        return (open + 1, i);
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
