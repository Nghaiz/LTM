namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// The single source of truth for the <c>vehicleType</c> value space.
    /// Mirrors plans/00-shared/protocol-spec.md section 4.9 exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <see cref="WeaponIds"/> again, shipped before the hole rather than after it.
    /// <c>S_VEHICLE_SPAWN</c> carries a <c>u8 networkTypeId</c> and the client instantiates a
    /// prefab from it; without a section saying what a value means, the mapping would live only
    /// in the vehicle prefabs — Unity YAML assets the server cannot open, being a netstandard
    /// library with no Unity reference. That is exactly the defect § 15's 2.0.1 row records for
    /// <c>weaponId</c>, and shipping it a second time knowing it is a hole is not defensible.
    /// </para>
    /// <para>
    /// <b>Ids are permanent and append-only.</b> Reassigning one breaks no build and no test: it
    /// makes a server that spawns type 4 and a client that instantiates type 4 disagree about
    /// which vehicle that is, at runtime, for everyone. A new vehicle takes the next free id; a
    /// retired vehicle's id retires with it and is never recycled.
    /// </para>
    /// <para>
    /// <c>tools/SpecChecker</c> verifies this file against the spec document AND against the
    /// serialized <c>networkId</c> on every vehicle prefab, on every CI run, because drift here
    /// is silent on both sides.
    /// </para>
    /// <para>
    /// <b>This is not <see cref="VehicleKind"/>.</b> The kind is the four-way physics family a
    /// decoder needs in order to read a snapshot entry's subtype tail; the id here is which
    /// prefab to instantiate. Two tank models share a kind and never share an id.
    /// </para>
    /// </remarks>
    public static class VehicleIds
    {
        /// <summary>
        /// No vehicle, or a vehicle this build does not know. Never assigned to a real one.
        /// </summary>
        /// <remarks>
        /// A receiver that reads <see cref="NONE"/> instantiates nothing and ignores the spawn,
        /// rather than guessing at a prefab. It is also what a sender emits for a prefab whose
        /// id is missing or duplicated, so a misconfigured vehicle transmits "unknown" instead
        /// of impersonating whichever vehicle legitimately owns that number.
        /// </remarks>
        public const byte NONE       = 0;

        public const byte JEEP       = 1;
        public const byte QUADBIKE   = 2;
        public const byte RHIB       = 3;
        public const byte HELICOPTER = 4;
        public const byte TANK       = 5;

        /// <summary>The highest id currently assigned. The next new vehicle takes 6.</summary>
        public const byte MAX_ASSIGNED = 5;

        /// <summary>
        /// Prefab names, indexed by id, exactly as the asset files are named. Index 0 is
        /// <see cref="NONE"/>.
        /// </summary>
        /// <remarks>
        /// These are the strings SpecChecker compares against the prefab file names. They are
        /// not sent on the wire and are not localized — they exist so drift between the id
        /// space and the assets is a build failure instead of a bug report about the wrong
        /// vehicle appearing.
        /// </remarks>
        private static readonly string[] Names =
        {
            "",
            "jeep",
            "quadbike",
            "rhib",
            "helicopter",
            "tank",
        };

        /// <summary>True when the id names a vehicle this build knows.</summary>
        public static bool IsKnown(byte vehicleTypeId)
            => vehicleTypeId != NONE && vehicleTypeId <= MAX_ASSIGNED;

        /// <summary>
        /// The physics family each id belongs to, indexed by id. Mirrors the third column of
        /// protocol-spec.md section 4.9.
        /// </summary>
        /// <remarks>
        /// Index 0 is <see cref="NONE"/> and holds a value nothing reads —
        /// <see cref="TryGetKind"/> refuses that id rather than returning the slot, because
        /// <see cref="VehicleKind.Car"/> is the zero value and a table lookup that answers
        /// "Car" for "no vehicle" is a wrong answer dressed as a correct one.
        /// </remarks>
        private static readonly VehicleKind[] Kinds =
        {
            default,                // NONE — never read; see TryGetKind.
            VehicleKind.Car,        // jeep
            VehicleKind.Car,        // quadbike
            VehicleKind.Boat,       // rhib
            VehicleKind.Helicopter, // helicopter
            VehicleKind.Tank,       // tank
        };

        /// <summary>
        /// The <see cref="VehicleKind"/> for an id, per protocol-spec.md section 4.9.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two type fields, and only one of them is authored on the prefab.</b>
        /// <c>S_VEHICLE_SPAWN</c> carries both the kind and the id, and the prefab carries only
        /// <c>networkId</c> — so the sender has to get the kind from somewhere. Deriving it here
        /// keeps the correspondence in the one file the spec is checked against, instead of
        /// adding a second serialized byte to every vehicle prefab that could be authored wrong
        /// with nothing to compare it to.
        /// </para>
        /// <para>
        /// <b>This does not collapse the two fields.</b> A second tank model takes a new id and
        /// a new row here pointing at the same kind, which is the arrangement section 4.9
        /// describes and the reason adding one is not a wire change.
        /// </para>
        /// <para>
        /// <c>tools/SpecChecker</c> compares this table against section 4.9's own third column
        /// on every CI run, so a row added here and forgotten in the document — or the reverse —
        /// fails the build rather than shipping a kind the client decodes a tail with.
        /// </para>
        /// </remarks>
        /// <returns>
        /// <c>false</c> for <see cref="NONE"/> and for any id this build does not know, in which
        /// case <paramref name="kind"/> is <c>default</c> and must not be sent. A sender that
        /// gets <c>false</c> has a misconfigured prefab and should say so, not guess.
        /// </returns>
        public static bool TryGetKind(byte vehicleTypeId, out VehicleKind kind)
        {
            if (!IsKnown(vehicleTypeId))
            {
                kind = default;
                return false;
            }

            kind = Kinds[vehicleTypeId];
            return true;
        }

        /// <summary>
        /// The prefab name for an id, or an empty string for <see cref="NONE"/> and for any id
        /// this build does not know.
        /// </summary>
        /// <remarks>
        /// Empty rather than an exception, for the same reason <see cref="WeaponIds.NameOf"/>
        /// does it: an unknown id is what a client legitimately receives from a NEWER server
        /// that ships a vehicle this build has never heard of, and skipping one spawn beats
        /// dropping the batch that carried it.
        /// </remarks>
        public static string NameOf(byte vehicleTypeId)
            => IsKnown(vehicleTypeId) ? Names[vehicleTypeId] : string.Empty;
    }
}
