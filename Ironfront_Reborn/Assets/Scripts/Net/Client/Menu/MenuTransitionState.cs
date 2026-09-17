#nullable enable

using System;

namespace Ironfront.Net.Unity.Client.Menu
{
    public readonly struct MenuTransitionFrame
    {
        public MenuTransitionFrame(float alpha, bool active, bool interactable)
        {
            Alpha = alpha;
            Active = active;
            Interactable = interactable;
        }

        public float Alpha { get; }
        public bool Active { get; }
        public bool Interactable { get; }
    }

    /// <summary>Pure transition policy shared by the Unity presenter and .NET tests.</summary>
    public static class MenuTransitionState
    {
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
            bool complete = time >= 1f;

            return new MenuTransitionFrame(
                alpha,
                active: targetVisible || !complete,
                interactable: targetVisible && complete);
        }
    }
}
