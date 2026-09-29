using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Draws a body swimming the way the original draws it: its own swim animation, at the surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The original swims by ragdoll.</b> <c>Actor.Update</c> fells a body in water and its
    /// animator's "Ragdoll Layer" plays <c>Swim Idle</c> / <c>Swim Forward</c> as the pose the active
    /// ragdoll's muscles chase, while buoyancy holds the hips and head up. A networked body is never
    /// ragdolled for water (a player moves by <c>MovementCore</c>; a bot's ragdoll lives on the
    /// server), so the same two clips are played here straight on the animated body: with that
    /// layer's weight up and <c>ragdolled</c> + <c>swim</c> set, its state machine walks
    /// None, Writhe, Swim Idle, and on to Swim Forward while the body moves. Measured on the proxy
    /// 2026-09-29: the clip lies the body forward, head up and arms reaching, with no physics at all.
    /// </para>
    /// <para>
    /// <b>Drawn at the surface by its head, not at the capsule.</b> The two clips hold the body
    /// very differently: Swim Idle treads water upright, its head bone 1.25 m over the root, and
    /// Swim Forward leans into the stroke with the head 0.85 m over it (measured on the proxy
    /// 2026-09-30). One depth for both drew a treading body with its chest out of the water
    /// (owner report 2026-09-30: "2/3 of my body is still above it"), so every swimmer -- a player
    /// on its capsule, a bot on its ragdoll's pelvis -- is placed by where its head is in the pose
    /// it is drawn in (<see cref="RootHeight"/>), and the crossfade between the two carries the
    /// depth with it.
    /// </para>
    /// </remarks>
    public static class SwimPresentation
    {
        /// <summary>
        /// How far over the surface a swimmer's head bone is held. On this rig the bone sits at the
        /// base of the head, so the head is out of the water and the shoulders are awash.
        /// </summary>
        public const float HeadAboveSurface = 0.05f;

        /// <summary>
        /// How far the head bone stands over the root in Swim Idle: what a body is placed by before
        /// its own pose has been read.
        /// </summary>
        public const float IdleHeadAboveRoot = 1.25f;

        /// <summary>
        /// The height to draw a swimming body's root at, <paramref name="headAboveRoot"/> being how far
        /// its head bone stands over its root in the pose it is drawn in: low enough that the head
        /// sits <see cref="HeadAboveSurface"/> over the water.
        /// </summary>
        public static float RootHeight(float waterHeight, float headAboveRoot)
            => waterHeight + HeadAboveSurface - headAboveRoot;

        private const string RagdollLayerName = "Ragdoll Layer";

        private static readonly int Ragdolled   = Animator.StringToHash("ragdolled");
        private static readonly int Swim        = Animator.StringToHash("swim");
        private static readonly int SwimForward = Animator.StringToHash("swim forward");

        /// <summary>
        /// Whether a body swims: alive, in water, and not in a seat. The one rule every swimmer is
        /// drawn by -- the local player, another player and a bot alike.
        /// </summary>
        public static bool Swims(bool alive, bool inWater, bool seated) => alive && inWater && !seated;

        /// <summary>
        /// Puts <paramref name="animator"/> into the swim, or takes it out: <paramref name="swimming"/>,
        /// and whether the swimmer is <paramref name="moving"/> through the water.
        /// </summary>
        /// <remarks>
        /// <paramref name="ragdolledOtherwise"/> is what <c>ragdolled</c> should say when the body is
        /// NOT swimming, so a caller that drives that parameter itself keeps driving it.
        /// </remarks>
        public static void Apply(Animator animator, bool swimming, bool moving, bool ragdolledOtherwise = false)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return;

            int layer = animator.GetLayerIndex(RagdollLayerName);
            if (layer >= 0) animator.SetLayerWeight(layer, swimming ? 1f : 0f);

            animator.SetBool(Ragdolled, swimming || ragdolledOtherwise);
            animator.SetBool(Swim, swimming);
            animator.SetBool(SwimForward, swimming && moving);
        }
    }
}
