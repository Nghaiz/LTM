using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// The trigger state of the most recently PROCESSED input frame, per session.
    /// protocol-spec handoff section 5.1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Processed, not arrived, and the distinction is the whole reason this is state rather
    /// than a function of the frame.</b> Input is sent with
    /// <see cref="ProtocolConstants.INPUT_REDUNDANCY"/> copies, so one frame arrives up to three
    /// times; a rising edge computed against the last ARRIVED frame would see
    /// <c>false -> true</c> once and then <c>true -> true</c> twice, which is correct only by
    /// luck. The dedup that makes this sound already exists — <c>ClientSession.EnqueueInput</c>
    /// and <c>InputAuthority.TryAccept</c> both drop a frame at or below
    /// <c>LastProcessedInputTick</c> — so this struct is only ever advanced by a frame that
    /// survived it, and a second dedup mechanism beside that one would be a second thing to keep
    /// in step.
    /// </para>
    /// <para>
    /// <b>Not part of <see cref="WeaponRuntimeState"/>, deliberately.</b> That struct is parked
    /// per weapon id by <c>ClientSession.SwitchWeaponTo</c>, and a sprint block parked under the
    /// rifle would be restored — stale — when the player switched back to it. Sprinting is a
    /// fact about the body, not about the gun in its hands.
    /// </para>
    /// </remarks>
    public struct EffectiveTrigger
    {
        /// <summary>
        /// Whether the effective trigger was down on the last processed frame. The semi-auto
        /// edge is measured against this.
        /// </summary>
        public bool WasEffective;

        /// <summary>
        /// Server time before which no shot may be taken, because the actor was sprinting.
        /// </summary>
        /// <remarks>
        /// <see cref="float.NegativeInfinity"/> when no block is running, so
        /// <c>now &gt;= SprintFireBlockedUntil</c> is true without a second "is one running?"
        /// flag to keep in step with it.
        /// </remarks>
        public float SprintFireBlockedUntil;

        /// <summary>
        /// True when the sprint rule is what lowered the weapon, so the sprint rule is allowed
        /// to raise it again.
        /// </summary>
        /// <remarks>
        /// Without this the raise would stomp a holster that belongs to somebody else — a
        /// weapon parked mid-switch by <c>ClientSession.SwitchWeaponTo</c> is holstered on
        /// purpose, and un-holstering it because the player happens not to be sprinting would
        /// let a shot leave a weapon that is still in a bag.
        /// </remarks>
        public bool LoweredBySprint;

        /// <summary>A session that has processed no frame yet, and is under no sprint block.</summary>
        public static EffectiveTrigger Idle => new EffectiveTrigger
        {
            WasEffective = false,
            SprintFireBlockedUntil = float.NegativeInfinity,
            LoweredBySprint = false,
        };

        /// <summary>
        /// Re-arms the semi-auto edge without touching the sprint block. Death and a weapon
        /// switch both want this: neither is a release of the trigger, and both must not leave
        /// the next legal frame looking like a continuation.
        /// </summary>
        public void ReArm() => WasEffective = false;
    }

    /// <summary>
    /// The facts about the BODY that the effective trigger reads, as opposed to the facts about
    /// the weapon (which live in <see cref="WeaponRuntimeState"/>) or about the input (which
    /// live in the frame).
    /// </summary>
    /// <remarks>
    /// Grouped into one struct rather than passed as three bools so that a call site cannot
    /// transpose them. All three are false-is-safe: the default value of this struct is a dead,
    /// undeployed actor, which cannot fire.
    /// </remarks>
    public readonly struct ActorFireEligibility
    {
        public ActorFireEligibility(
            bool isAlive, bool isDeployed, bool isSeatedWithoutCarriedWeapon = false)
        {
            IsAlive = isAlive;
            IsDeployed = isDeployed;
            IsSeatedWithoutCarriedWeapon = isSeatedWithoutCarriedWeapon;
        }

        public bool IsAlive { get; }

        /// <summary>
        /// False between the join handshake and the deploy that puts a body in the world.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="IsAlive"/> because an actor waiting on the deploy screen is
        /// not dead — it has never lived this round — and folding the two would make a queued
        /// input frame from before the deploy fire the moment the body appeared.
        /// </remarks>
        public bool IsDeployed { get; }

        /// <summary>
        /// True for a passenger in a seat whose <c>CanUseCarriedWeapon()</c> is false.
        /// </summary>
        /// <remarks>
        /// A gunner operating a mounted weapon never reaches the carried-weapon path at all —
        /// <c>ServerCombatBridge</c> returns before it — so this is about the seats that forbid
        /// BOTH, and it is stated here rather than inferred from the seat index because the
        /// seat rules are the game's and this library cannot see them.
        /// </remarks>
        public bool IsSeatedWithoutCarriedWeapon { get; }

        /// <summary>An actor standing on its own feet, holding its own weapon.</summary>
        public static ActorFireEligibility OnFoot(bool isAlive)
            => new ActorFireEligibility(isAlive, isDeployed: true);
    }

    /// <summary>What one advance of the trigger state machine decided.</summary>
    public readonly struct TriggerOutcome
    {
        public TriggerOutcome(bool effective, bool risingEdge, bool attemptShot)
        {
            Effective = effective;
            RisingEdge = risingEdge;
            AttemptShot = attemptShot;
        }

        /// <summary>The effective trigger, after every gate in section 5.1.</summary>
        public bool Effective { get; }

        /// <summary><see cref="Effective"/> went <c>false -&gt; true</c> on this frame.</summary>
        public bool RisingEdge { get; }

        /// <summary>
        /// Whether the resolver should be asked for a shot: every effective tick for an
        /// automatic, the rising edge only for a semi-automatic.
        /// </summary>
        public bool AttemptShot { get; }
    }

    /// <summary>
    /// The server's half of <c>FpsActorController.Fire()</c>: which raw Fire bits are allowed to
    /// become trigger pulls. Handoff section 5.1 and 5.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes.</b> <c>LocalInputSource</c> puts the raw Fire and Sprint bits
    /// on the wire, and the original controller refuses to fire while sprinting and for
    /// <see cref="ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS"/> afterwards. The server never
    /// learned that rule, so it accepted the shot and took the round while the client — obeying
    /// its own controller — rendered nothing. The magazine drained with no muzzle flash and no
    /// projectile.
    /// </para>
    /// <para>
    /// <b>A rejected frame has no side effect, which is why the gate runs BEFORE the resolver
    /// rather than refunding after it.</b> Decrementing and adding back is visible to the client
    /// as a flicker, and is a second place the arithmetic can be wrong — handoff section 16
    /// forbids it by name.
    /// </para>
    /// <para>
    /// <b><see cref="InputButtons.Aim"/> is not in the list, and its absence is a rule.</b>
    /// Hip-fire is valid whenever the actor is not sprinting; requiring the aim bit would make
    /// every hip-fired shot vanish on the server while the client rendered it, which is the
    /// same disagreement this class exists to remove, pointed the other way.
    /// </para>
    /// </remarks>
    public static class EffectiveTriggerPolicy
    {
        /// <summary>
        /// Advances the trigger by one PROCESSED frame, applying the sprint rule to the weapon
        /// on the way through.
        /// </summary>
        /// <param name="trigger">The session's trigger state. Advanced in place.</param>
        /// <param name="weapon">
        /// Lowered on a sprint frame and raised again when the sprint ends, matching the
        /// original controller. Nothing else about it is written here.
        /// </param>
        /// <param name="automatic">
        /// <see cref="WeaponConfig.Automatic"/>. A semi-automatic fires on the rising edge only,
        /// so several input frames inside one mouse press spend one round rather than one per
        /// frame.
        /// </param>
        public static TriggerOutcome Advance(
            ref EffectiveTrigger trigger,
            ref WeaponRuntimeState weapon,
            in InputFrame frame,
            in ActorFireEligibility actor,
            bool automatic,
            float nowSeconds)
        {
            if (frame.IsPressed(InputButtons.Sprint))
            {
                if (weapon.Unholstered)
                {
                    weapon.Unholstered = false;
                    trigger.LoweredBySprint = true;
                }

                // Re-armed on every sprint frame, so the window is measured from the END of the
                // sprint rather than from its start. Stamping it once on the leading edge would
                // expire mid-sprint and let a shot out of a lowered weapon.
                trigger.SprintFireBlockedUntil =
                    nowSeconds + ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS;
            }
            else if (trigger.LoweredBySprint)
            {
                // Raised the moment the sprint ends, but still inside the block above: the
                // weapon comes up over the window rather than after it, which is what makes the
                // transition to effective=true at the end of the window a trigger EDGE and not
                // the first frame of a holstered weapon.
                weapon.Unholstered = true;
                trigger.LoweredBySprint = false;
            }

            bool effective =
                frame.IsPressed(InputButtons.Fire)
                && actor.IsAlive
                && actor.IsDeployed
                && !actor.IsSeatedWithoutCarriedWeapon
                && !frame.IsPressed(InputButtons.Sprint)
                && nowSeconds >= trigger.SprintFireBlockedUntil
                && weapon.Unholstered;

            bool risingEdge = effective && !trigger.WasEffective;
            trigger.WasEffective = effective;

            return new TriggerOutcome(effective, risingEdge, automatic ? effective : risingEdge);
        }
    }
}
