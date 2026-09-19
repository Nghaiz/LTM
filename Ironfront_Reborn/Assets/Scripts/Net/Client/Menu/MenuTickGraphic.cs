#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// Shows and hides several graphics together from one <see cref="Toggle"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because <see cref="Toggle.graphic"/> drives exactly one <see cref="Graphic"/>.</b> Its
    /// transition calls <c>CrossFadeAlpha</c> on that object's own canvas renderer and does not
    /// reach children, so a multi-part visual cannot be bound through it.
    /// </para>
    /// <para>
    /// The prototype's checkbox tick is exactly that: <c>.check input:checked+span::after</c> is a
    /// 10x6 box with its top and right borders removed and the rest rotated, which is two strokes
    /// at right angles. A checkmark is not a quad, so binding one stroke to
    /// <see cref="Toggle.graphic"/> would leave the other drawn permanently and the box would
    /// appear ticked in both states.
    /// </para>
    /// <para>
    /// <see cref="Graphic.enabled"/> rather than an alpha fade, so the strokes are removed from the
    /// canvas entirely when unchecked instead of drawn at zero alpha.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuTickGraphic : MonoBehaviour
    {
        [SerializeField] private Toggle? _toggle;
        [SerializeField] private Graphic[] _graphics = System.Array.Empty<Graphic>();

        /// <summary>
        /// Binds <paramref name="graphics"/> to <paramref name="toggle"/> and applies its current
        /// state, so the tick is correct before the first click.
        /// </summary>
        public void Configure(Toggle toggle, params Graphic[] graphics)
        {
            _toggle = toggle;
            _graphics = graphics;
            Apply(toggle.isOn);
            toggle.onValueChanged.AddListener(Apply);
        }

        private void OnDestroy()
        {
            // The listener outlives this component otherwise, and a static scene reload with
            // domain reload disabled would hand the next run a callback into a destroyed object.
            if (_toggle != null) _toggle.onValueChanged.RemoveListener(Apply);
        }

        private void Apply(bool visible)
        {
            foreach (Graphic graphic in _graphics)
                if (graphic != null) graphic.enabled = visible;
        }
    }
}
