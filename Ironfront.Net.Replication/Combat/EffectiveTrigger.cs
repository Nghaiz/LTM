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
        /// True while the sprint rule is HOLDING the weapon down, so the sprint rule is the one
        /// allowed to raise it again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without this the raise would stomp a holster that belongs to somebody else — a
        /// weapon parked mid-switch by <c>ClientSession.SwitchWeaponTo</c> is holstered on
        /// purpose, and un-holstering it because the player happens not to be sprinting would
        /// let a shot leave a weapon that is still in a bag.
        /// </para>
        /// <para>
        /// <b>Custody, and NOT the lowering edge — the difference was a shipped defect.</b> The
        /// name is the older, narrower reading: this was set only on the frame the sprint rule
        /// itself lowered the weapon, so a sprint that BEGAN with the weapon already down
        /// latched nothing and nothing ever raised it again.
        /// <see cref="EffectiveTriggerPolicy.Advance"/> carries the measurement. It is now set
        /// on every sprinting frame, which is the same thing whenever the weapon was up and the
        /// repair whenever it was not.
        /// </para>
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
    /// <para>
    /// <b>The sprint half is split out because the CLIENT predicts against it too.</b>
    /// <see cref="AdvanceSprintBlock"/> and <see cref="SprintAllowsFire"/> are the two members
    /// <c>ClientCombatState</c> calls, and <see cref="Advance"/> is written in terms of those
    /// same two rather than beside them. A second predicate on the client is what
    /// <see cref="ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS"/>'s own remark forbids, one
    /// level up from the number — and the gap it leaves was measured, not feared: on
    /// 2026-09-14 a protocol-10 client held Fire and Sprint for six seconds, predicted 51 shots
    /// the server refused every one of, and drove <c>SnapshotAmmoCorrections</c> from 1 to 19
    /// doing it. A human holding Shift and the left mouse button takes the same path.
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT shared, and why each one stops at this boundary.</b>
    /// The <i>semi-automatic edge</i> is measured against the last PROCESSED frame, and the
    /// client's render loop is neither the input send rate nor the accepted-frame rate — an
    /// edge sampled there would count different edges from this one, which is the disagreement
    /// again in a new place. The <i>holster mutation</i> is not mirrored because the client's
    /// <see cref="WeaponRuntimeState"/> has one writer, <see cref="WeaponRuntimeState.Loaded"/>,
    /// and nothing on that side parks a weapon mid-switch the way <c>ClientSession</c> does, so
    /// lowering it there would invent a second writer for a field nobody raises. The
    /// <i>deployed and seated</i> eligibility is not shared because the client models neither —
    /// <c>ClientCombatState</c> knows only whether it is alive — and inferring them would be a
    /// rule the server never applied.
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
            bool sprinting = frame.IsPressed(InputButtons.Sprint);

            if (sprinting)
            {
                // The sprint rule takes CUSTODY of the weapon, whether it lowered it or found it
                // already down. This used to latch only inside `if (weapon.Unholstered)`,
                // so a sprint that began with the weapon already down set nothing, the raise
                // below never fired, and the weapon stayed holstered for the rest of that life —
                // there is no other writer on this side that would ever put it back up.
                // Measured on Island 2026-09-14: four seconds of held fire AFTER the sprint
                // ended gave 97 [shot] attempts, 0 fired, 97 rejection=Holstered, while the same
                // programme on Dustbowl emptied a magazine. The only difference between the two
                // runs was whether the weapon happened to be up when the sprint started, and
                // deploy is exactly when it is not: a weapon has an unholster time and a player
                // who sprints for cover inside it is the ordinary case.
                //
                // Taking custody of a weapon this rule did not lower is safe, and that is a claim
                // about the ACTIVE weapon specifically. Nothing on the server leaves
                // the active weapon down on purpose: `ClientSession.SwitchWeaponTo` raises the
                // incoming weapon unconditionally and parks the outgoing one under its own id,
                // where `Advance` never sees it — the weapon-in-a-bag the flag's own remark
                // protects is a PARKED state, not this one. So a down active weapon with no flag
                // is state nobody is tracking — `ClientSession.ClearCombatStateOnDeath` is one
                // proven producer of exactly that pair, clearing the trigger while deliberately
                // leaving the weapon alone — and raising it is the repair rather than a stomp.
                weapon.Unholstered = false;
                trigger.LoweredBySprint = true;
            }
            else if (trigger.LoweredBySprint)
            {
                // Raised the moment the sprint ends, but still inside the block below: the
                // weapon comes up over the window rather than after it, which is what makes the
                // transition to effective=true at the end of the window a trigger EDGE and not
                // the first frame of a holstered weapon.
                weapon.Unholstered = true;
                trigger.LoweredBySprint = false;
            }

            AdvanceSprintBlock(ref trigger, sprinting, nowSeconds);

            bool effective =
                frame.IsPressed(InputButtons.Fire)
                && actor.IsAlive
                && actor.IsDeployed
                && !actor.IsSeatedWithoutCarriedWeapon
                && !sprinting
                && SprintAllowsFire(in trigger, nowSeconds)
                && weapon.Unholstered;

            bool risingEdge = effective && !trigger.WasEffective;
            trigger.WasEffective = effective;

            return new TriggerOutcome(effective, risingEdge, automatic ? effective : risingEdge);
        }

        /// <summary>
        /// Stamps the sprint block for one frame, on whichever side is advancing a trigger.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Called on EVERY frame, not only the frames the trigger is down.</b> The block is
        /// measured from the last sprinting frame, so a player who sprints without firing and
        /// pulls the trigger the instant they let go must still be refused. A caller that
        /// stamped only while Fire was pressed would have nothing stamped at that moment and
        /// would predict a shot the server refuses — the same disagreement, one release later.
        /// </para>
        /// <para>
        /// Takes the sprint bit rather than the whole <see cref="InputFrame"/> so the client,
        /// whose sprint state arrives as an <c>IInputSource</c> button word rather than as a
        /// processed frame, reaches the same implementation instead of a copy of it.
        /// </para>
        /// </remarks>
        public static void AdvanceSprintBlock(
            ref EffectiveTrigger trigger, bool sprinting, float nowSeconds)
        {
            if (!sprinting) return;

            // Re-armed on every sprint frame, so the window is measured from the END of the
            // sprint rather than from its start. Stamping it once on the leading edge would
            // expire mid-sprint and let a shot out of a lowered weapon.
            trigger.SprintFireBlockedUntil =
                nowSeconds + ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS;
        }

        /// <summary>
        /// Whether the sprint rule permits a shot at <paramref name="nowSeconds"/>.
        /// </summary>
        /// <remarks>
        /// The whole test, including the sprinting frame itself: a frame that has just called
        /// <see cref="AdvanceSprintBlock"/> with <c>sprinting: true</c> has pushed
        /// <see cref="EffectiveTrigger.SprintFireBlockedUntil"/> a full window into the future,
        /// so a separate "is the player sprinting right now?" clause would be a second
        /// condition saying the same thing. <see cref="Advance"/> keeps its <c>!sprinting</c>
        /// term beside this one only because it reads the bit anyway for the holster.
        /// </remarks>
        public static bool SprintAllowsFire(in EffectiveTrigger trigger, float nowSeconds)
            => nowSeconds >= trigger.SprintFireBlockedUntil;
    }
}
