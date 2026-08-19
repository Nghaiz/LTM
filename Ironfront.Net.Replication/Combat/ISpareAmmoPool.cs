namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// Where a reload draws its rounds from. The engine-free mirror of
    /// <c>Weapon.RemoveSpareAmmo</c>, which the game overrides at <c>MountedWeapon</c>. V6-D6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two owners, one interface, and that is the whole point of the seam.</b> Infantry spare
    /// ammo lives on the <c>Actor</c> across five loadout slots (<c>Actor.RemoveSpareAmmo</c>); a
    /// mounted weapon's lives on the weapon itself (<c>MountedWeapon.RemoveSpareAmmo</c>). Both
    /// answer "give me up to N rounds" and neither can answer it for the other. Written as two
    /// branches inside one method they would be one edit away from a mounted reload silently
    /// draining the gunner's rifle magazines — a double-spend with no error anywhere, which is
    /// exactly the risk V6 § 5 scores. Written as two implementations they are structurally
    /// different objects and the test that grades them cannot pass by accident.
    /// </para>
    /// <para>
    /// <b>Long-lived, so the interface call allocates nothing.</b> Every implementation here is
    /// either a stateless singleton or a fixed-capacity table built once; the call sits on the
    /// 30 Hz reload path.
    /// </para>
    /// </remarks>
    public interface ISpareAmmoPool
    {
        /// <summary>
        /// Removes up to <paramref name="count"/> rounds and returns how many were actually
        /// available.
        /// </summary>
        /// <param name="ownerId">
        /// Whose pool. An <c>actorId</c> for <see cref="ActorSpareAmmoPool"/>; unused by the
        /// implementations whose storage is the weapon's own state.
        /// </param>
        /// <param name="slot">The loadout slot, for a pool that keeps more than one.</param>
        /// <param name="state">
        /// The weapon's runtime state, by reference, because the mounted pool's storage IS
        /// <see cref="WeaponRuntimeState.SpareAmmo"/>. A pool that keeps its own table ignores it.
        /// </param>
        /// <param name="count">Rounds wanted. Never negative.</param>
        /// <returns>Rounds granted, in <c>[0, count]</c>.</returns>
        int Take(ushort ownerId, byte slot, ref WeaponRuntimeState state, int count);

        /// <summary>Rounds this pool could grant right now, or <c>-1</c> for unlimited.</summary>
        int Remaining(ushort ownerId, byte slot, in WeaponRuntimeState state);
    }

    /// <summary>
    /// A pool that never runs out. What every caller written before V6 was implicitly using.
    /// </summary>
    /// <remarks>
    /// Named rather than expressed as a <c>null</c> pool, because a null that silently means
    /// "infinite" is exactly the undocumented fallback <c>development-principles.md</c> forbids —
    /// and the difference between "no pool was wired" and "this weapon genuinely has infinite
    /// spare" is the difference between a bug and a design decision.
    /// </remarks>
    public sealed class UnlimitedSpareAmmoPool : ISpareAmmoPool
    {
        public static readonly UnlimitedSpareAmmoPool Instance = new UnlimitedSpareAmmoPool();

        private UnlimitedSpareAmmoPool() { }

        public int Take(ushort ownerId, byte slot, ref WeaponRuntimeState state, int count)
            => count < 0 ? 0 : count;

        public int Remaining(ushort ownerId, byte slot, in WeaponRuntimeState state) => -1;
    }

    /// <summary>
    /// The per-weapon pool a mounted weapon owns, held in
    /// <see cref="WeaponRuntimeState.SpareAmmo"/>. Mirrors <c>MountedWeapon.cs</c>'s override.
    /// </summary>
    /// <remarks>
    /// Stateless: the storage is the caller's struct, so one instance serves every mounted weapon
    /// on the server and the Actor's five-slot pool is never even reachable from here.
    /// </remarks>
    public sealed class MountedSpareAmmoPool : ISpareAmmoPool
    {
        public static readonly MountedSpareAmmoPool Instance = new MountedSpareAmmoPool();

        private MountedSpareAmmoPool() { }

        public int Take(ushort ownerId, byte slot, ref WeaponRuntimeState state, int count)
        {
            if (count <= 0) return 0;

            // The -2 sentinel is INFINITE and must never be decremented — decrementing it turns
            // it into -3, which is neither sentinel and reads as a negative count everywhere
            // downstream. Weapon.cs:571 has spelled this out since before the netcode existed.
            if (state.SpareAmmo == WeaponConfig.InfiniteSpareAmmo) return count;

            // -1 is NO RESUPPLY, which is a statement about whether an ammo bag may refill this
            // weapon — not about whether it has rounds. It carries no rounds, so it grants none.
            if (state.SpareAmmo <= 0) return 0;

            int granted = state.SpareAmmo < count ? state.SpareAmmo : count;
            state.SpareAmmo = (short)(state.SpareAmmo - granted);
            return granted;
        }

        public int Remaining(ushort ownerId, byte slot, in WeaponRuntimeState state)
        {
            if (state.SpareAmmo == WeaponConfig.InfiniteSpareAmmo) return -1;
            return state.SpareAmmo > 0 ? state.SpareAmmo : 0;
        }
    }

    /// <summary>
    /// The infantry pool: five slots per actor, mirroring <c>Actor.spareAmmo[5]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pool a mounted reload must NOT reach.</b> It exists in V6 so that the distinction
    /// is enforced by which object the caller holds, rather than by remembering to branch — and
    /// so that <c>AMountedReloadDrawsFromThePerWeaponPoolNotTheActorPool</c> has two real tables
    /// to compare rather than one table and a comment.
    /// </para>
    /// <para>
    /// Fixed-capacity and allocated once, indexed by <c>actorId</c> the way every other
    /// per-actor table on the server is.
    /// </para>
    /// </remarks>
    public sealed class ActorSpareAmmoPool : ISpareAmmoPool
    {
        /// <summary>Loadout slots per actor. <c>Actor.cs</c> has hardcoded 5 since the freeze.</summary>
        public const int SlotsPerActor = 5;

        private readonly short[] _rounds;
        private readonly int _actorCapacity;

        public ActorSpareAmmoPool(int maxActors = Protocol.ProtocolConstants.MAX_ACTORS)
        {
            _actorCapacity = maxActors + 1;
            _rounds        = new short[_actorCapacity * SlotsPerActor];
        }

        /// <summary>Sets one slot's spare rounds. Loadout assignment and resupply.</summary>
        public bool Set(ushort actorId, byte slot, short rounds)
        {
            if (!TryIndex(actorId, slot, out int index)) return false;

            _rounds[index] = rounds;
            return true;
        }

        public int Take(ushort ownerId, byte slot, ref WeaponRuntimeState state, int count)
        {
            if (count <= 0) return 0;
            if (!TryIndex(ownerId, slot, out int index)) return 0;

            short held = _rounds[index];
            if (held == WeaponConfig.InfiniteSpareAmmo) return count;
            if (held <= 0) return 0;

            int granted = held < count ? held : count;
            _rounds[index] = (short)(held - granted);
            return granted;
        }

        public int Remaining(ushort ownerId, byte slot, in WeaponRuntimeState state)
        {
            if (!TryIndex(ownerId, slot, out int index)) return 0;

            short held = _rounds[index];
            if (held == WeaponConfig.InfiniteSpareAmmo) return -1;
            return held > 0 ? held : 0;
        }

        /// <summary>Zeroes every slot. Round teardown.</summary>
        public void Reset()
        {
            for (int i = 0; i < _rounds.Length; i++) _rounds[i] = 0;
        }

        private bool TryIndex(ushort actorId, byte slot, out int index)
        {
            index = 0;
            if (actorId == 0 || actorId >= _actorCapacity) return false;
            if (slot >= SlotsPerActor) return false;

            index = actorId * SlotsPerActor + slot;
            return true;
        }
    }
}
