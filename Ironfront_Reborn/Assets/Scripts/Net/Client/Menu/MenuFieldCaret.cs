#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// Keeps an input field's caret a fixed width in screen pixels, so it is drawn at every
    /// window size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the caret vanished.</b> <see cref="InputField"/> draws its caret
    /// <see cref="InputField.caretWidth"/> canvas units wide, and the menu canvas scales with the
    /// screen. In the 940×528 playtest windows the scale is about 0.49, so the default one-unit
    /// caret was half a pixel wide — a quad that covers no pixel centre at most text positions,
    /// and is therefore not rasterised at all. The fields took typing with no blinking cursor.
    /// </para>
    /// <para>
    /// The width is set from the root canvas's scale factor, and set again whenever that scale
    /// changes (a resize or a resolution change), which is the only thing that can undo it.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(InputField))]
    public sealed class MenuFieldCaret : MonoBehaviour
    {
        /// <summary>The caret's width on screen.</summary>
        public const float ScreenPixels = 2f;

        private InputField? _field;
        private Canvas? _canvas;
        private float _appliedScale;

        private void OnEnable()
        {
            _field = GetComponent<InputField>();
            Canvas? parent = GetComponentInParent<Canvas>();
            _canvas = parent != null ? parent.rootCanvas : null;
            _appliedScale = 0f;
            Apply();
        }

        private void LateUpdate()
        {
            if (_canvas != null && !Mathf.Approximately(_canvas.scaleFactor, _appliedScale)) Apply();
        }

        private void Apply()
        {
            if (_field == null || _canvas == null) return;

            _appliedScale = _canvas.scaleFactor;
            _field.caretWidth = WidthFor(_appliedScale);
        }

        /// <summary>
        /// The caret width, in canvas units, that is at least <see cref="ScreenPixels"/> on screen.
        /// </summary>
        public static int WidthFor(float scaleFactor)
            => Mathf.Max(1, Mathf.CeilToInt(ScreenPixels / Mathf.Max(0.01f, scaleFactor)));
    }
}
