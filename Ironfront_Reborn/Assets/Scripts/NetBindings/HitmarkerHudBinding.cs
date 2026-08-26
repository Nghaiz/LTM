using Ironfront.Net.Unity.Client;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// The <c>Assembly-CSharp</c> half of <see cref="IHitmarkerHud"/>. Phase C4a.
    /// </summary>
    /// <remarks>
    /// A one-line forward, and that is the whole seam. <c>IngameUi.Hit</c> already no-ops without
    /// an instance, so no guard is added here — one would be a second copy of a decision the HUD
    /// already makes.
    /// </remarks>
    internal sealed class HitmarkerHudBinding : IHitmarkerHud
    {
        /// <inheritdoc/>
        public void ShowHit(int severity) => IngameUi.Hit(severity);
    }
}
