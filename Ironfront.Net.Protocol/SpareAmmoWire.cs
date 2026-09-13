using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// What a reserve count can be, once you admit that "how many rounds" is not the only
    /// answer a weapon can give.
    /// </summary>
    public enum SpareAmmoKind : byte
    {
        /// <summary>A countable number of rounds, <c>0 .. <see cref="SpareAmmo.MaxFiniteRounds"/></c>.</summary>
        Finite = 0,

        /// <summary>
        /// The weapon has no reserve and no ammo bag will ever give it one. Distinct from
        /// <see cref="Finite"/> zero, which is empty-but-refillable.
        /// </summary>
        NoResupply = 1,

        /// <summary>The reserve never runs down. Reloading does not decrement it.</summary>
        Infinite = 2,
    }

    /// <summary>
    /// The reserve-ammunition half of <see cref="SnapshotField.Weapon"/>, and the single codec
    /// for it. protocol-spec.md section 4.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a type and not a cast.</b> The reserve is a <c>short</c> in the weapon model, an
    /// <c>int</c> coming out of the spare-ammo pool APIs, and a <c>u16</c> on
    /// the wire, and the two negative sentinels do not agree across those: the model spells
    /// no-resupply <c>-1</c> and infinite <c>-2</c>, while a pool's <c>Remaining</c> returns
    /// <c>-1</c> for infinite and folds no-resupply into <c>0</c>. Written as an inline
    /// <c>(ushort)</c> at each call site, one of those two meanings of <c>-1</c> silently
    /// becomes the other, and the failure surfaces as a HUD reading <c>65535</c> or as a
    /// bazooka that will not reload. Every conversion goes through the named constructors
    /// below so the question "which <c>-1</c> is this?" is answered once, here.
    /// </para>
    /// <para>
    /// <b>Why the sentinels sit at the top of the range and not below zero.</b> The field is
    /// unsigned on the wire, so there is no below-zero to use. 0xFFFE and 0xFFFF cost two
    /// values out of 65,536 and no weapon in the catalogue carries a five-figure reserve.
    /// </para>
    /// </remarks>
    public readonly struct SpareAmmo : IEquatable<SpareAmmo>
    {
        /// <summary>Wire value for <see cref="SpareAmmoKind.NoResupply"/>.</summary>
        public const ushort NoResupplyEncoded = 0xFFFE;

        /// <summary>Wire value for <see cref="SpareAmmoKind.Infinite"/>.</summary>
        public const ushort InfiniteEncoded = 0xFFFF;

        /// <summary>Largest countable reserve the field can carry: 65,533.</summary>
        public const int MaxFiniteRounds = NoResupplyEncoded - 1;

        /// <summary>The model's spelling of "has no reserve and cannot be resupplied".</summary>
        /// <remarks>Mirrors <c>WeaponConfig.NoResupplySpareAmmo</c>, which lives above this assembly.</remarks>
        public const short ConfiguredNoResupply = -1;

        /// <summary>The model's spelling of "never runs out".</summary>
        /// <remarks>Mirrors <c>WeaponConfig.InfiniteSpareAmmo</c>, which lives above this assembly.</remarks>
        public const short ConfiguredInfinite = -2;

        private SpareAmmo(SpareAmmoKind kind, int rounds)
        {
            Kind   = kind;
            Rounds = rounds;
        }

        public SpareAmmoKind Kind { get; }

        /// <summary>
        /// Rounds held, when <see cref="Kind"/> is <see cref="SpareAmmoKind.Finite"/>. Zero for
        /// both sentinels — read <see cref="Kind"/> before reading this.
        /// </summary>
        public int Rounds { get; }

        /// <summary>The reserve a weapon with no reserve at all reports.</summary>
        public static SpareAmmo NoResupply { get; } = new SpareAmmo(SpareAmmoKind.NoResupply, 0);

        /// <summary>The reserve a weapon that never runs out reports.</summary>
        public static SpareAmmo Infinite { get; } = new SpareAmmo(SpareAmmoKind.Infinite, 0);

        /// <summary>
        /// A countable reserve. Values above <see cref="MaxFiniteRounds"/> clamp rather than
        /// wrapping into a sentinel — a reserve big enough to overflow is a bug worth seeing as
        /// a wrong number, not one worth seeing as "infinite".
        /// </summary>
        public static SpareAmmo Finite(int rounds)
        {
            if (rounds <= 0) return new SpareAmmo(SpareAmmoKind.Finite, 0);
            if (rounds > MaxFiniteRounds) return new SpareAmmo(SpareAmmoKind.Finite, MaxFiniteRounds);
            return new SpareAmmo(SpareAmmoKind.Finite, rounds);
        }

        /// <summary>
        /// Reads a reserve in the <b>weapon model's</b> convention: <c>-1</c> no-resupply,
        /// <c>-2</c> infinite, anything else a count.
        /// </summary>
        /// <remarks>
        /// This is the one that takes a <c>WeaponConfig.SpareAmmo</c> or a
        /// <c>WeaponRuntimeState.SpareAmmo</c>. It is NOT the one that takes a pool's
        /// <c>Remaining</c> — see <see cref="FromPoolRemaining"/>, where <c>-1</c> means the
        /// other thing.
        /// </remarks>
        public static SpareAmmo FromConfigured(short configured) => configured switch
        {
            ConfiguredInfinite   => Infinite,
            ConfiguredNoResupply => NoResupply,
            _                    => Finite(configured),
        };

        /// <summary>
        /// Reads a reserve in the <b>pool's</b> convention: any negative value is infinite,
        /// anything else a count.
        /// </summary>
        /// <remarks>
        /// A pool cannot report no-resupply — it answers <c>0</c> for a weapon that has no
        /// reserve and for one that has spent it, and those are the same to a pool but not to
        /// a player looking at a HUD. A caller that can tell the two apart (it holds the weapon
        /// config, which says whether the weapon resupplies at all) should decide that before
        /// calling, and pass <see cref="NoResupply"/> itself.
        /// </remarks>
        public static SpareAmmo FromPoolRemaining(int remaining) =>
            remaining < 0 ? Infinite : Finite(remaining);

        /// <summary>Packs to the wire's <c>u16</c>.</summary>
        public ushort Encode() => Kind switch
        {
            SpareAmmoKind.Infinite   => InfiniteEncoded,
            SpareAmmoKind.NoResupply => NoResupplyEncoded,
            _                        => (ushort)Rounds,
        };

        /// <summary>Unpacks the wire's <c>u16</c>. Total — every one of the 65,536 values decodes.</summary>
        public static SpareAmmo Decode(ushort encoded) => encoded switch
        {
            InfiniteEncoded   => Infinite,
            NoResupplyEncoded => NoResupply,
            _                 => new SpareAmmo(SpareAmmoKind.Finite, encoded),
        };

        /// <summary>
        /// Whether a reload may draw from this reserve at all. False for an empty finite
        /// reserve and for <see cref="SpareAmmoKind.NoResupply"/>; true for infinite.
        /// </summary>
        public bool CanFeedAReload =>
            Kind == SpareAmmoKind.Infinite || (Kind == SpareAmmoKind.Finite && Rounds > 0);

        public bool Equals(SpareAmmo other) => Kind == other.Kind && Rounds == other.Rounds;

        public override bool Equals(object? obj) => obj is SpareAmmo other && Equals(other);

        public override int GetHashCode() => ((int)Kind << 24) ^ Rounds;

        public override string ToString() => Kind switch
        {
            SpareAmmoKind.Infinite   => "infinite",
            SpareAmmoKind.NoResupply => "no-resupply",
            _                        => Rounds.ToString(),
        };

        public static bool operator ==(SpareAmmo left, SpareAmmo right) => left.Equals(right);

        public static bool operator !=(SpareAmmo left, SpareAmmo right) => !left.Equals(right);
    }
}
