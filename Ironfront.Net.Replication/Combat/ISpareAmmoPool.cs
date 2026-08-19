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

        /// <summary>
        /// Puts rounds back, clamped at <paramref name="cap"/>, and reports how many landed.
        /// The ammo-bag half of phase-V7 task 7.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The mirror of <c>Actor.ResupplyAmmo</c>, which fills each of the five loadout slots
        /// and clamps to <c>configuration.spareAmmo</c> (<c>Actor.cs:1156</c>). It is on this
        /// interface rather than beside it because a resupply that bypassed the pool would be a
        /// second writer of the same number, and the entire reason
        /// <see cref="ISpareAmmoPool"/> exists is that infantry spare and mounted spare are
        /// different storage that must not reach each other.
        /// </para>
        /// <para>
        /// <b>V7-D9: enforced here, displayed by prediction.</b> The server owns the pool so a
        /// resupply cannot be forged, and the number is deliberately not snapshotted —
        /// <c>SnapshotField</c> is 8/8. A client's HUD count is its own prediction, and a bag
        /// resupply makes that prediction too <b>low</b>, never too high, so the error never
        /// tells a player they have ammo they do not. The next reload corrects it through the
        /// <c>AmmoInClip</c> the snapshot already carries.
        /// </para>
        /// </remarks>
        /// <param name="cap">
        /// The authored ceiling for this slot. Rounds above it are discarded rather than
        /// banked.
        /// </param>
        /// <returns>Rounds actually added. Zero when the slot was already at the cap.</returns>
        int Give(ushort ownerId, byte slot, ref WeaponRuntimeState state, int count, int cap);
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

        /// <summary>
        /// Always zero: a pool that never runs out has nothing to top up, and reporting rounds
        /// added would shorten a medipack-style lifetime for a resupply that changed nothing.
        /// </summary>
        public int Give(ushort ownerId, byte slot, ref WeaponRuntimeState state, int count, int cap)
            => 0;
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

        public int Give(ushort ownerId, byte slot, ref WeaponRuntimeState state, int count, int cap)
        {
            if (count <= 0) return 0;

            // INFINITE has nothing to add to, and the -1 NO-RESUPPLY sentinel is a statement
            // that an ammo bag may not refill this weapon at all -- which is precisely the
            // question being asked here. Both refuse, for different reasons, and neither may be
            // decremented or incremented into a value that is no longer a sentinel.
            if (state.SpareAmmo == WeaponConfig.InfiniteSpareAmmo) return 0;
            if (state.SpareAmmo < 0) return 0;
            if (state.SpareAmmo >= cap) return 0;

            int room    = cap - state.SpareAmmo;
            int granted = room < count ? room : count;
            state.SpareAmmo = (short)(state.SpareAmmo + granted);
            return granted;
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
        private readonly short[] _caps;
        private readonly short[] _resupplyPerPulse;
        private readonly int _actorCapacity;

        public ActorSpareAmmoPool(int maxActors = Protocol.ProtocolConstants.MAX_ACTORS)
        {
            _actorCapacity    = maxActors + 1;
            _rounds           = new short[_actorCapacity * SlotsPerActor];
            _caps             = new short[_actorCapacity * SlotsPerActor];
            _resupplyPerPulse = new short[_actorCapacity * SlotsPerActor];
        }

        /// <summary>Sets one slot's spare rounds. Loadout assignment and resupply.</summary>
        /// <remarks>
        /// <b>Also raises the slot's resupply ceiling to match.</b> <c>Actor.ResupplyAmmo</c>
        /// clamps to <c>configuration.spareAmmo</c> (<c>Actor.cs:1156</c>) — the authored
        /// loadout figure — and the loadout is exactly what this method is called with. Deriving
        /// the ceiling from the largest amount ever assigned means an ammo bag refills a slot to
        /// what the player spawned with, without a second table for the caller to keep in step.
        /// A later <c>Set</c> to a smaller number is a spend, not a demotion, so the ceiling
        /// only ever rises.
        /// </remarks>
        public bool Set(ushort actorId, byte slot, short rounds)
        {
            if (!TryIndex(actorId, slot, out int index)) return false;

            _rounds[index] = rounds;
            if (rounds > _caps[index]) _caps[index] = rounds;
            return true;
        }

        /// <summary>
        /// Declares one slot's authored figures at spawn: what it starts with, its ceiling, and
        /// how much one ammo-bag pulse adds. The engine-free mirror of
        /// <c>weapon.configuration.spareAmmo</c> and <c>.resupplyNumber</c>, which
        /// <c>Actor.ResupplyAmmo</c> (<c>Actor.cs:1203-1220</c>) reads straight off the weapon.
        /// </summary>
        /// <remarks>
        /// <b>Both figures are per weapon, so neither can be a constant here.</b> A rifle and a
        /// launcher refill by different amounts to different ceilings, and a resupply that
        /// picked one number for all five slots would be a balance change wearing a netcode
        /// commit's clothes. Passing <paramref name="resupplyPerPulse"/> as 0 is how a weapon
        /// that <c>AllowsResupply()</c> refuses is expressed.
        /// </remarks>
        public bool SetLoadout(
            ushort actorId, byte slot, short rounds, short cap, short resupplyPerPulse)
        {
            if (!TryIndex(actorId, slot, out int index)) return false;

            _rounds[index]           = rounds;
            _caps[index]             = cap;
            _resupplyPerPulse[index] = resupplyPerPulse;
            return true;
        }

        /// <summary>Overrides the resupply ceiling for one slot, independent of what it holds.</summary>
        public bool SetCap(ushort actorId, byte slot, short cap)
        {
            if (!TryIndex(actorId, slot, out int index)) return false;

            _caps[index] = cap;
            return true;
        }

        /// <summary>The resupply ceiling for one slot. Zero means an ammo bag adds nothing.</summary>
        public short CapOf(ushort actorId, byte slot)
            => TryIndex(actorId, slot, out int index) ? _caps[index] : (short)0;

        /// <summary>Rounds one ammo-bag pulse adds to this slot.</summary>
        public short ResupplyPerPulseOf(ushort actorId, byte slot)
            => TryIndex(actorId, slot, out int index) ? _resupplyPerPulse[index] : (short)0;

        /// <summary>
        /// Runs one ammo-bag pulse against a slot, using that slot's own authored figures. The
        /// overload a deployable calls, since it has no <see cref="WeaponRuntimeState"/> and no
        /// business choosing either number.
        /// </summary>
        public int Give(ushort actorId, byte slot)
        {
            short amount = ResupplyPerPulseOf(actorId, slot);
            if (amount <= 0) return 0;

            WeaponRuntimeState unused = default;
            return Give(actorId, slot, ref unused, amount, CapOf(actorId, slot));
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

        public int Give(ushort ownerId, byte slot, ref WeaponRuntimeState state, int count, int cap)
        {
            if (count <= 0) return 0;
            if (!TryIndex(ownerId, slot, out int index)) return 0;

            short held = _rounds[index];
            if (held == WeaponConfig.InfiniteSpareAmmo) return 0;
            if (held < 0) return 0;
            if (held >= cap) return 0;

            int room    = cap - held;
            int granted = room < count ? room : count;
            _rounds[index] = (short)(held + granted);
            return granted;
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
