using System.Collections.Generic;
using System.Linq;
using Ironfront.Net.Protocol;
using Ironfront.Tools.SpecChecker;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V3 task 10 — the vehicle-registry gate's own red paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every failure this gate exists to catch is silent on both sides of the wire.</b> A
    /// reassigned <c>networkId</c> breaks no build and no test: it makes a server that spawns
    /// type 4 and a client that instantiates type 4 disagree about which vehicle that is, at
    /// runtime, for everyone. So the gate is only worth having if it demonstrably fires — and a
    /// gate nobody has watched fail is unproven.
    /// </para>
    /// <para>
    /// The fixtures are records rather than files under <c>Assets/Prefab</c>, because a broken
    /// fixture on disk would be scanned by the real gate and would fail the build it is testing.
    /// </para>
    /// </remarks>
    public sealed class VehicleRegistryGateTests
    {
        [Fact]
        public void TheShippedRegistryIsClean()
        {
            // The green twin. Without it, every red assertion below could be passing because the
            // validator rejects everything — and a gate that fires on correct input is a gate
            // people delete.
            var failures = new List<string>();
            int count = Program.ValidateVehicleRegistry(Authored(), failures);

            Assert.Empty(failures);
            Assert.Equal(VehicleIds.MAX_ASSIGNED, count);
        }

        [Fact]
        public void ADuplicateIdIsReported()
        {
            List<Program.VehiclePrefabRecord> prefabs = Authored();
            prefabs[4] = new Program.VehiclePrefabRecord("tank", VehicleIds.JEEP);

            Assert.Contains(
                Failures(prefabs),
                f => f.Contains("id 1 is on both") && f.Contains("tank"));
        }

        [Fact]
        public void AnIdOutsideOneToTwoFiftyFiveIsReported()
        {
            List<Program.VehiclePrefabRecord> prefabs = Authored();
            prefabs[0] = new Program.VehiclePrefabRecord("jeep", 0);

            Assert.Contains(Failures(prefabs), f => f.Contains("Valid ids are 1..255"));
        }

        [Fact]
        public void ANegativeIdIsReported()
        {
            List<Program.VehiclePrefabRecord> prefabs = Authored();
            prefabs[0] = new Program.VehiclePrefabRecord("jeep", -1);

            Assert.Contains(Failures(prefabs), f => f.Contains("Valid ids are 1..255"));
        }

        [Fact]
        public void AnIdTheRegistryDoesNotKnowIsReported()
        {
            List<Program.VehiclePrefabRecord> prefabs = Authored();
            prefabs[0] = new Program.VehiclePrefabRecord("jeep", VehicleIds.MAX_ASSIGNED + 1);

            Assert.Contains(Failures(prefabs), f => f.Contains("which VehicleIds does not know"));
        }

        [Fact]
        public void AnIdNoPrefabCarriesIsReported()
        {
            // The reverse direction, and the one a per-prefab loop alone cannot see: the server
            // would keep spawning a type no client can ever instantiate.
            List<Program.VehiclePrefabRecord> prefabs = Authored();
            prefabs.RemoveAt(4);

            Assert.Contains(
                Failures(prefabs),
                f => f.Contains($"declares id {VehicleIds.TANK}")
                     && f.Contains("but no prefab carries it"));
        }

        [Fact]
        public void ARenamedPrefabIsReported()
        {
            List<Program.VehiclePrefabRecord> prefabs = Authored();
            prefabs[0] = new Program.VehiclePrefabRecord("jeep_v2", VehicleIds.JEEP);

            Assert.Contains(
                Failures(prefabs),
                f => f.Contains("was renamed or reassigned"));
        }

        [Fact]
        public void AnUnauthoredVehiclePrefabIsReported()
        {
            // This is the state every vehicle prefab was in before phase-V3. Skipping it rather
            // than failing is exactly how it would have stayed that way, so it is a failure and
            // not a silent pass.
            List<Program.VehiclePrefabRecord> prefabs = Authored();
            prefabs[0] = new Program.VehiclePrefabRecord("jeep", null);

            Assert.Contains(Failures(prefabs), f => f.Contains("has no serialized networkId"));
        }

        [Fact]
        public void AnEmptyScanIsAFailureRatherThanAPass()
        {
            // A checker that reports green because it found nothing to check has replaced a
            // silent bug with a louder silence.
            var failures = new List<string>();
            int count = Program.ValidateVehicleRegistry(
                new List<Program.VehiclePrefabRecord>(), failures);

            Assert.Equal(0, count);
            Assert.Contains(failures, f => f.Contains("parsed 0 authored vehicle prefabs"));
        }

        // ------------------------------------------------------------------ helpers

        private static List<string> Failures(IReadOnlyList<Program.VehiclePrefabRecord> prefabs)
        {
            var failures = new List<string>();
            Program.ValidateVehicleRegistry(prefabs, failures);

            Assert.NotEmpty(failures);
            return failures;
        }

        /// <summary>
        /// The shipped registry as records, built from <see cref="VehicleIds"/> itself so adding
        /// a sixth vehicle does not need an edit here — only the assertions that name a specific
        /// id do.
        /// </summary>
        private static List<Program.VehiclePrefabRecord> Authored()
            => Enumerable
                .Range(1, VehicleIds.MAX_ASSIGNED)
                .Select(id => new Program.VehiclePrefabRecord(
                    VehicleIds.NameOf((byte)id), id))
                .ToList();
    }
}
