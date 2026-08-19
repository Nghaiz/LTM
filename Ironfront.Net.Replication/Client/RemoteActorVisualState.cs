using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// What a remote actor should look like this frame, decoded from the snapshot fields the
    /// client already receives and — until phase-V10 — threw away. phase-V10 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the testable half of <c>RemoteActorView</c>.</b> The Unity component pushes
    /// animator parameters, swaps a weapon model and enables a rig; none of that runs in CI.
    /// The decision of <i>what</i> those should be is a pure function of one
    /// <see cref="ActorSnapshotEntry"/>, so it lives here and is graded.
    /// </para>
    /// <para>
    /// <b>Deliberately not rendered in V10, and named rather than left silent:</b>
    /// <see cref="IsSeated"/> is V5's vehicle work, <see cref="IsInWater"/> has no cosmetic in
    /// the original game, and <see cref="Health"/> is carried but never drawn on a remote actor.
    /// All three are decoded here so that "the client discards this field" stops being true —
    /// an unconsumed capability nobody writes down is exactly how six router events came to be
    /// dead.
    /// </para>
    /// </remarks>
    public readonly struct RemoteActorVisualState
    {
        public readonly ushort ActorId;

        /// <summary>Upper-body aim, in degrees. Also the origin ray for the tracer.</summary>
        public readonly float PitchDegrees;

        public readonly bool IsAlive;
        public readonly bool IsCrouching;
        public readonly bool IsProne;
        public readonly bool IsSprinting;
        public readonly bool IsAiming;
        public readonly bool IsRagdoll;

        /// <summary>Decoded and deliberately not rendered in V10. V5 owns seats.</summary>
        public readonly bool IsSeated;

        /// <summary>Decoded and deliberately not rendered — the original has no water cosmetic.</summary>
        public readonly bool IsInWater;

        /// <summary>Carried into the view and deliberately not drawn on a remote actor.</summary>
        public readonly byte Health;

        /// <summary>Selects the weapon model, and therefore whose cosmetics a shot plays.</summary>
        public readonly byte WeaponId;

        public readonly byte AmmoInClip;

        /// <summary>Material and insignia — and, for the local actor, the value the minimap reads.</summary>
        public readonly byte Team;

        public RemoteActorVisualState(
            ushort actorId, float pitchDegrees, ActorStateFlags flags,
            byte health, byte weaponId, byte ammoInClip, byte team)
        {
            ActorId      = actorId;
            PitchDegrees = pitchDegrees;
            IsAlive      = (flags & ActorStateFlags.IsAlive)     != 0;
            IsCrouching  = (flags & ActorStateFlags.IsCrouching) != 0;
            IsProne      = (flags & ActorStateFlags.IsProne)     != 0;
            IsSprinting  = (flags & ActorStateFlags.IsSprinting) != 0;
            IsAiming     = (flags & ActorStateFlags.IsAiming)    != 0;
            IsRagdoll    = (flags & ActorStateFlags.IsRagdoll)   != 0;
            IsSeated     = (flags & ActorStateFlags.IsSeated)    != 0;
            IsInWater    = (flags & ActorStateFlags.IsInWater)   != 0;
            Health       = health;
            WeaponId     = weaponId;
            AmmoInClip   = ammoInClip;
            Team         = team;
        }

        /// <summary>
        /// Whether this actor may play a cosmetic at all. A corpse fires nothing: a fire event
        /// that crosses a death in flight would otherwise flash a muzzle on a ragdoll.
        /// </summary>
        public bool CanPlayCosmetics => IsAlive && !IsRagdoll;

        /// <summary>
        /// Stance, collapsed to the one the animator draws. Prone outranks crouch — an actor
        /// reporting both is lying, and lying down is the more specific claim.
        /// </summary>
        public RemoteActorStance Stance
        {
            get
            {
                if (IsProne) return RemoteActorStance.Prone;
                return IsCrouching ? RemoteActorStance.Crouching : RemoteActorStance.Standing;
            }
        }

        /// <summary>Decodes one snapshot entry into the pose it describes.</summary>
        public static RemoteActorVisualState From(in ActorSnapshotEntry entry)
            => new RemoteActorVisualState(
                entry.ActorId,
                Quantize.UnpackPitchByte(entry.Pitch),
                entry.StateFlags,
                entry.Health,
                entry.WeaponId,
                entry.AmmoInClip,
                entry.Team);
    }

    /// <summary>The three body poses the original game's animator distinguishes.</summary>
    public enum RemoteActorStance : byte
    {
        Standing = 0,
        Crouching = 1,
        Prone = 2,
    }
}
