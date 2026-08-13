using System.Collections.Generic;
using System.Reflection;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Protocol.Tests.Conformance
{
    /// <summary>
    /// Pins the <c>weaponId</c> value space declared in protocol-spec.md § 4.8.
    /// </summary>
    /// <remarks>
    /// <c>tools/SpecChecker</c> compares this table against the spec document and against the
    /// Unity prefab, which covers drift between the three copies. What it cannot cover is
    /// <see cref="WeaponIds"/> being internally inconsistent — an id constant added without its
    /// name row, or a name row that quietly shifts every id after it by one. Those are the
    /// mistakes a person makes while doing the right thing, so they get a test rather than a
    /// convention.
    /// </remarks>
    public sealed class WeaponIdTests
    {
        private static IReadOnlyDictionary<string, byte> DeclaredIds()
        {
            var ids = new Dictionary<string, byte>();
            foreach (FieldInfo field in typeof(WeaponIds)
                         .GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(byte) || !field.IsLiteral) continue;
                if (field.Name == nameof(WeaponIds.NONE) ||
                    field.Name == nameof(WeaponIds.MAX_ASSIGNED)) continue;

                ids[field.Name] = (byte)field.GetRawConstantValue()!;
            }
            return ids;
        }

        [Fact]
        public void ZeroIsReservedAndNeverNamesAWeapon()
        {
            Assert.Equal(0, WeaponIds.NONE);
            Assert.False(WeaponIds.IsKnown(WeaponIds.NONE));
            Assert.Equal(string.Empty, WeaponIds.NameOf(WeaponIds.NONE));
        }

        [Fact]
        public void EveryAssignedIdIsUniqueAndInRange()
        {
            var byId = new Dictionary<byte, string>();

            foreach (KeyValuePair<string, byte> declared in DeclaredIds())
            {
                Assert.InRange(declared.Value, (byte)1, WeaponIds.MAX_ASSIGNED);

                Assert.False(
                    byId.ContainsKey(declared.Value),
                    $"weapon id {declared.Value} is declared twice: " +
                    $"{byId.GetValueOrDefault(declared.Value)} and {declared.Key}. Ids are " +
                    "unique and permanent (protocol-spec.md § 4.8).");

                byId[declared.Value] = declared.Key;
            }

            // Contiguous 1..MAX_ASSIGNED. A hole means an id was deleted rather than retired in
            // place, and the next person to "use the free slot" reassigns a retired weapon's id.
            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
            {
                Assert.True(byId.ContainsKey(id), $"no constant declares weapon id {id}.");
            }
        }

        [Fact]
        public void EveryAssignedIdHasANonEmptyName()
        {
            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
            {
                Assert.True(WeaponIds.IsKnown(id));
                Assert.False(
                    string.IsNullOrWhiteSpace(WeaponIds.NameOf(id)),
                    $"weapon id {id} is declared but has no name row — the names array and the " +
                    "id constants have drifted apart.");
            }
        }

        [Fact]
        public void NamesAreUnique()
        {
            var seen = new Dictionary<string, byte>();
            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
            {
                string name = WeaponIds.NameOf(id);
                Assert.False(
                    seen.ContainsKey(name),
                    $"'{name}' is the name of both id {seen.GetValueOrDefault(name)} and id {id}. " +
                    "SpecChecker matches the prefab by name, so a duplicate makes the drift gate " +
                    "unable to tell the two entries apart.");
                seen[name] = id;
            }
        }

        /// <summary>
        /// An id past this build's <see cref="WeaponIds.MAX_ASSIGNED"/> is what an older client
        /// receives from a newer server, and § 4.8 says it degrades to "draw nothing" rather
        /// than to a dropped snapshot or a throw.
        /// </summary>
        [Fact]
        public void UnknownIdFromANewerBuildDegradesQuietly()
        {
            byte fromTheFuture = (byte)(WeaponIds.MAX_ASSIGNED + 1);

            Assert.False(WeaponIds.IsKnown(fromTheFuture));
            Assert.Equal(string.Empty, WeaponIds.NameOf(fromTheFuture));
            Assert.Equal(string.Empty, WeaponIds.NameOf(byte.MaxValue));
        }
    }
}
