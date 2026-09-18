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
