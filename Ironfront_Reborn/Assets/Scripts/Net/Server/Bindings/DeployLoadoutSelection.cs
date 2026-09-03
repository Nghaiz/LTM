namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The weapon network ids one specific actor's client chose for its own deploy, stamped with
    /// the actor id it is valid for. Ledger <b>X-11</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Set by <c>ServerCombatBridge.PlaceAtSpawn</c> immediately before it calls
    /// <c>NetServerActor.EquipLoadout</c>, and consumed by <c>Actor.SpawnLoadoutWeapons</c></b> on
    /// the far side of an asmdef boundary this type cannot itself cross: <c>Actor</c> compiles
    /// into <c>Assembly-CSharp</c>, unreachable from this assembly, which is why the value has to
    /// be picked up rather than passed as a parameter.
    /// </para>
    /// <para>
    /// <b>Why a stamped static and not an <c>IGameplayActorSource</c> parameter.</b> The
    /// sanctioned crossing for this boundary is a method that interface declares and
    /// <c>ActorGameplaySource</c> implements — every other member of it works exactly that way —
    /// and that route was tried first. It needs one new forwarding line on <c>NetServerActor</c>
    /// (the class every <c>IGameplayActorSource</c> member is reached through), and that file is
    /// out of scope for this change. This static is the documented fallback the scope forced, not
    /// a shortcut taken instead of looking.
    /// </para>
    /// <para>
    /// <b>The ordering IS the correctness argument, and the consumer's guards exist to make a
    /// violation of it loud instead of silent.</b> A bare "set, call, clear" has a window where a
    /// second deploy landing inside the first one's window would arm the WRONG body from a stale
    /// value — unlikely today, since the tick loop is single-threaded and one request is handled
    /// to completion before the next, but a silent wrong-body arm is exactly the failure class
    /// this whole fix exists to close. <see cref="NetServerBindings.TryConsumeDeploySelection"/>
    /// is where the three guards live: the actor id must match, the read is one-shot (cleared in
    /// the same call, whether or not the id matched), and a mismatch is logged rather than
    /// silently substituted.
    /// </para>
    /// <para>
    /// <b>If <c>PlaceAtSpawn</c> is ever changed to call <c>EquipLoadout</c> asynchronously, on a
    /// different thread, or more than once before the pending value is consumed, this whole
    /// scheme breaks.</b> The guards turn a silent wrong-body arm into a logged one; they do not
    /// make the temporal coupling safe to ignore.
    /// </para>
    /// </remarks>
    public readonly struct DeployLoadoutSelection
    {
        /// <summary>The actor this selection is valid for. A consumer for any other id must refuse it.</summary>
        public readonly ushort ActorId;

        /// <summary>Weapon network id for the primary slot. 0 = left empty.</summary>
        public readonly byte Primary;
        /// <summary>Weapon network id for the secondary slot. 0 = left empty.</summary>
        public readonly byte Secondary;
        /// <summary>Weapon network id for the first gear slot. 0 = left empty.</summary>
        public readonly byte Gear1;
        /// <summary>Weapon network id for the second gear slot. 0 = left empty.</summary>
        public readonly byte Gear2;
        /// <summary>Weapon network id for the third gear slot. 0 = left empty.</summary>
        public readonly byte Gear3;

        public DeployLoadoutSelection(
            ushort actorId, byte primary, byte secondary, byte gear1, byte gear2, byte gear3)
        {
            ActorId   = actorId;
            Primary   = primary;
            Secondary = secondary;
            Gear1     = gear1;
            Gear2     = gear2;
            Gear3     = gear3;
        }
    }
}
