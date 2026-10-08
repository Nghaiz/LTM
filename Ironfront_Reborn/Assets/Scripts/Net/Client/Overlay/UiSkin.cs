#nullable enable

using UnityEngine;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// The assets a runtime-built screen needs and cannot name by path: the UI pack's fonts and
    /// painted backgrounds. One asset at <c>Resources/IronfrontUi/UiSkin</c>, written by
    /// <c>BuildUiSkin</c> in the Editor; icons sit beside it in <c>Resources/IronfrontUi/Icons</c>
    /// and are loaded by name (<see cref="Icon"/>).
    /// </summary>
    [CreateAssetMenu(menuName = "Ironfront/UI skin", fileName = "UiSkin")]
    public sealed class UiSkin : ScriptableObject
    {
        public const string ResourcePath = "IronfrontUi/UiSkin";
        public const string IconFolder = "IronfrontUi/Icons/";
        public const string BadgeFolder = "IronfrontUi/Achievements/";

        [SerializeField] private Font? _regular;
        [SerializeField] private Font? _bold;
        [SerializeField] private Font? _black;
        [SerializeField] private Sprite? _menuBackground;
        [SerializeField] private Sprite? _multiplayerBackground;
        [SerializeField] private Sprite? _logo;

        private static UiSkin? _loaded;
        private static Font? _fallback;

        /// <summary>The skin, loaded once. Never null: a missing asset falls back to Unity's built-in font.</summary>
        public static UiSkin Current
        {
            get
            {
                if (_loaded != null) return _loaded;
                _loaded = Resources.Load<UiSkin>(ResourcePath);
                if (_loaded == null)
                {
                    Debug.LogError("[ui] Resources/" + ResourcePath + " is missing; run \"Ironfront/Net/Build UI skin\". "
                                   + "Screens fall back to the built-in font.");
                    _loaded = CreateInstance<UiSkin>();
                }
                return _loaded;
            }
        }

        public Font Regular => _regular != null ? _regular : Fallback;
        public Font Bold => _bold != null ? _bold : Regular;
        public Font Black => _black != null ? _black : Bold;
        public Sprite? MenuBackground => _menuBackground;
        public Sprite? MultiplayerBackground => _multiplayerBackground;
        public Sprite? Logo => _logo;

        private static Font Fallback
        {
            get
            {
                if (_fallback == null) _fallback = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _fallback;
            }
        }

        /// <summary>A white UI glyph from <c>Resources/IronfrontUi/Icons</c>, tinted where it is drawn; null when absent.</summary>
        public static Sprite? Icon(string name) => Resources.Load<Sprite>(IconFolder + name);

        /// <summary>An achievement's badge from <c>Resources/IronfrontUi/Achievements</c>; null when absent.</summary>
        public static Sprite? Badge(string id) => Resources.Load<Sprite>(BadgeFolder + id);

        /// <summary>Set by the Editor builder; not for game code.</summary>
        public void Assign(Font regular, Font bold, Font black, Sprite? menuBackground, Sprite? multiplayerBackground, Sprite? logo)
        {
            _regular = regular;
            _bold = bold;
            _black = black;
            _menuBackground = menuBackground;
            _multiplayerBackground = multiplayerBackground;
            _logo = logo;
        }
    }
}
