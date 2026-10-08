using System;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The player's key bindings, live: every gameplay key read goes through here, so a key changed
    /// on the settings screen changes what the game does and what every hint on screen says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not Unity's Input Manager.</b> Its buttons and axes (<c>"Fire1"</c>, <c>"Crouch"</c>,
    /// <c>"Horizontal"</c>) are fixed at build time and cannot be rebound by a player. The game now
    /// asks for an <see cref="GameAction"/> and this class answers from the player's own keys
    /// (<see cref="KeyBindingSet"/>), saved in <c>PlayerPrefs</c> under <see cref="PrefsKey"/>.
    /// </para>
    /// <para>
    /// <b>Same feel as before.</b> The defaults are the keys the game shipped with, and the two
    /// axes keep the Input Manager's response: movement is digital (its axes had sensitivity and
    /// gravity 1000, snap on), and lean eases in and out at 10 units a second, as its
    /// <c>"Lean"</c> axis did (<see cref="LeanAxis"/>). Mouse look and the helicopter joystick axes
    /// are not keys and still come from the Input Manager.
    /// </para>
    /// <para>
    /// Nothing here checks whether a text field owns the keyboard; the callers already do, each
    /// with the rule that suits it (<c>LocalTextEntry</c>).
    /// </para>
    /// </remarks>
    public static class GameKeys
    {
        /// <summary>Where the bindings are saved.</summary>
        public const string PrefsKey = "ironfront.keys.v1";

        /// <summary>How fast lean moves toward its target, per second: the Input Manager's "Lean" axis.</summary>
        public const float LeanSensitivity = 10f;

        private static KeyBindingSet _bindings;
        private static int _leanFrame = -1;
        private static float _lean;

        /// <summary>Raised after the bindings change, so on-screen key hints can redraw.</summary>
        public static event Action Changed;

        /// <summary>The live bindings. Loaded from <c>PlayerPrefs</c> on first use.</summary>
        public static KeyBindingSet Bindings
        {
            get
            {
                if (_bindings == null) _bindings = KeyBindingSet.Parse(PlayerPrefs.GetString(PrefsKey, string.Empty));
                return _bindings;
            }
        }

        /// <summary>Replaces the live bindings with <paramref name="bindings"/> and saves them.</summary>
        public static void Apply(KeyBindingSet bindings)
        {
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            Bindings.CopyFrom(bindings);
            PlayerPrefs.SetString(PrefsKey, Bindings.Serialize());
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        /// <summary>Whether a key of <paramref name="action"/> is held this frame.</summary>
        public static bool Held(GameAction action)
        {
            KeyBindingSet keys = Bindings;
            return Key(keys.Get(action, 0)) || Key(keys.Get(action, 1));
        }

        /// <summary>Whether a key of <paramref name="action"/> went down this frame.</summary>
        public static bool Down(GameAction action)
        {
            KeyBindingSet keys = Bindings;
            return KeyDown(keys.Get(action, 0)) || KeyDown(keys.Get(action, 1));
        }

        /// <summary>Whether a key of <paramref name="action"/> came up this frame.</summary>
        public static bool Up(GameAction action)
        {
            KeyBindingSet keys = Bindings;
            return KeyUp(keys.Get(action, 0)) || KeyUp(keys.Get(action, 1));
        }

        /// <summary>
        /// -1, 0 or 1 from a pair of actions, as the Input Manager's movement axes answered: both
        /// held, or neither, is 0.
        /// </summary>
        public static float Axis(GameAction negative, GameAction positive)
            => (Held(positive) ? 1f : 0f) - (Held(negative) ? 1f : 0f);

        /// <summary>Left/right movement: the old <c>"Horizontal"</c> axis.</summary>
        public static float MoveX => Axis(GameAction.MoveLeft, GameAction.MoveRight);

        /// <summary>Forward/back movement: the old <c>"Vertical"</c> axis.</summary>
        public static float MoveZ => Axis(GameAction.MoveBackward, GameAction.MoveForward);

        /// <summary>
        /// Lean, eased like the old <c>"Lean"</c> axis (sensitivity and gravity 10, no snap): it moves
        /// toward the held direction, or back to 0, by 10 units a second. Advanced once a frame
        /// however many times it is read.
        /// </summary>
        public static float LeanAxis
        {
            get
            {
                int frame = Time.frameCount;
                if (frame != _leanFrame)
                {
                    float delta = _leanFrame < 0 ? 0f : Time.unscaledDeltaTime;
                    _leanFrame = frame;
                    _lean = StepLean(_lean, Axis(GameAction.LeanLeft, GameAction.LeanRight), delta);
                }
                return _lean;
            }
        }

        /// <summary>One step of <see cref="LeanAxis"/>'s easing: <paramref name="value"/> toward <paramref name="target"/>.</summary>
        public static float StepLean(float value, float target, float deltaSeconds)
            => Mathf.MoveTowards(value, target, LeanSensitivity * Mathf.Max(0f, deltaSeconds));

        /// <summary>The keys of <paramref name="action"/> as a player reads them: "W / UP", "F", or "UNBOUND".</summary>
        public static string Label(GameAction action)
        {
            KeyBindingSet keys = Bindings;
            KeyCode primary = keys.Get(action, 0);
            KeyCode secondary = keys.Get(action, 1);
            if (primary == KeyCode.None && secondary == KeyCode.None) return "UNBOUND";
            if (primary == KeyCode.None) return KeyName(secondary);
            if (secondary == KeyCode.None) return KeyName(primary);
            return KeyName(primary) + " / " + KeyName(secondary);
        }

        /// <summary>The first key of <paramref name="action"/>, for a single key cap; "?" when unbound.</summary>
        public static string Cap(GameAction action)
        {
            KeyBindingSet keys = Bindings;
            KeyCode key = keys.Get(action, 0) != KeyCode.None ? keys.Get(action, 0) : keys.Get(action, 1);
            return key == KeyCode.None ? "?" : KeyName(key);
        }

        /// <summary>A key's name as it is printed on a key cap, in capitals.</summary>
        public static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.None: return "—";
                case KeyCode.Mouse0: return "LMB";
                case KeyCode.Mouse1: return "RMB";
                case KeyCode.Mouse2: return "MMB";
                case KeyCode.Mouse3: return "MOUSE 4";
                case KeyCode.Mouse4: return "MOUSE 5";
                case KeyCode.Mouse5: return "MOUSE 6";
                case KeyCode.Mouse6: return "MOUSE 7";
                case KeyCode.Return: return "ENTER";
                case KeyCode.KeypadEnter: return "NUM ENTER";
                case KeyCode.LeftShift: return "L-SHIFT";
                case KeyCode.RightShift: return "R-SHIFT";
                case KeyCode.LeftControl: return "L-CTRL";
                case KeyCode.RightControl: return "R-CTRL";
                case KeyCode.LeftAlt: return "L-ALT";
                case KeyCode.RightAlt: return "R-ALT";
                case KeyCode.UpArrow: return "UP";
                case KeyCode.DownArrow: return "DOWN";
                case KeyCode.LeftArrow: return "LEFT";
                case KeyCode.RightArrow: return "RIGHT";
                case KeyCode.Backspace: return "BACKSPACE";
                case KeyCode.CapsLock: return "CAPS";
                case KeyCode.PageUp: return "PG UP";
                case KeyCode.PageDown: return "PG DN";
                case KeyCode.BackQuote: return "`";
                case KeyCode.Minus: return "-";
                case KeyCode.Equals: return "=";
                case KeyCode.LeftBracket: return "[";
                case KeyCode.RightBracket: return "]";
                case KeyCode.Semicolon: return ";";
                case KeyCode.Quote: return "'";
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Slash: return "/";
                case KeyCode.Backslash: return "\\";
            }

            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return ((int)(key - KeyCode.Alpha0)).ToString();
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) return "NUM " + (int)(key - KeyCode.Keypad0);
            if (key >= KeyCode.JoystickButton0 && key <= KeyCode.JoystickButton19)
                return "PAD " + (int)(key - KeyCode.JoystickButton0);

            return key.ToString().ToUpperInvariant();
        }

        /// <summary>
        /// The key pressed this frame, for the rebind prompt: keyboard, mouse buttons and gamepad
        /// buttons, never the joystick-numbered duplicates of a gamepad button.
        /// </summary>
        public static bool TryReadPressedKey(out KeyCode key)
        {
            foreach (KeyCode candidate in CaptureOrder)
            {
                if (!Input.GetKeyDown(candidate)) continue;
                key = candidate;
                return true;
            }

            key = KeyCode.None;
            return false;
        }

        private static KeyCode[] _captureOrder;

        private static KeyCode[] CaptureOrder
        {
            get
            {
                if (_captureOrder != null) return _captureOrder;

                var keys = new System.Collections.Generic.List<KeyCode>();
                foreach (KeyCode key in (KeyCode[])Enum.GetValues(typeof(KeyCode)))
                {
                    // Joystick1Button0 and up repeat JoystickButton0..19 per pad; one name per button.
                    if (key == KeyCode.None || key >= KeyCode.Joystick1Button0) continue;
                    if (!keys.Contains(key)) keys.Add(key);
                }
                _captureOrder = keys.ToArray();
                return _captureOrder;
            }
        }

        private static bool Key(KeyCode key) => key != KeyCode.None && Input.GetKey(key);
        private static bool KeyDown(KeyCode key) => key != KeyCode.None && Input.GetKeyDown(key);
        private static bool KeyUp(KeyCode key) => key != KeyCode.None && Input.GetKeyUp(key);
    }
}
