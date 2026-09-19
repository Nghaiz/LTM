#nullable enable

using System;

namespace Ironfront.Net.Unity.Client.Menu
{
    public readonly struct MenuTransitionFrame
    {
        public MenuTransitionFrame(float alpha, float scale, bool active, bool interactable)
        {
            Alpha = alpha;
            Scale = scale;
            Active = active;
            Interactable = interactable;
        }

        public float Alpha { get; }
        public float Scale { get; }
        public bool Active { get; }
        public bool Interactable { get; }
    }

    public static class MenuTransitionState
    {
        public const float HiddenScale = 1.008f;

        /// <summary>
        /// Decides whether a visibility request needs to start (or reverse) an animation.
        /// Re-applying the same controller state must not restart an animation which is already
        /// travelling to that state; room snapshots can legitimately cause Apply several times
        /// per second.
        /// </summary>
        public static bool ShouldRestart(
            bool currentTarget,
            bool requestedTarget,
            bool animationRunning,
            bool active,
            float alpha)
        {
            if (currentTarget != requestedTarget) return true;
            if (animationRunning) return false;

            return requestedTarget
                ? !active || alpha < 0.999f
                : active || alpha > 0.001f;
        }

        public static MenuTransitionFrame Evaluate(
            float startAlpha,
            bool targetVisible,
            float normalizedTime)
        {
            float time = Math.Clamp(normalizedTime, 0f, 1f);
            float inverse = 1f - time;
            float eased = 1f - (inverse * inverse * inverse);
            float targetAlpha = targetVisible ? 1f : 0f;
            float alpha = startAlpha + ((targetAlpha - startAlpha) * eased);
            float scale = targetVisible
                ? HiddenScale + ((1f - HiddenScale) * eased)
                : 1f + ((HiddenScale - 1f) * eased);
            bool complete = time >= 1f;

            return new MenuTransitionFrame(
                alpha,
                scale,
                active: targetVisible || !complete,
                interactable: targetVisible && complete);
        }
    }
}
