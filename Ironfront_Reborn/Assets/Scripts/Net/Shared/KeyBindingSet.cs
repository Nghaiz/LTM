using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The keys bound to every <see cref="GameAction"/>: two slots each, primary and secondary, and
    /// no key in two places at once. Pure data, so the rules are testable without a keyboard;
    /// <see cref="GameKeys"/> holds the live set and reads the keyboard through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A key lives in one slot.</b> Binding a key that another action holds takes it from that
    /// action and says so (<see cref="Bind"/> returns what lost it), which is how most shooters
    /// treat a clash: the player chose the key for this action just now, so this action keeps it,
    /// and the screen tells them which action is now unbound instead of silently firing both.
    /// </para>
    /// <para>
    /// <b>Escape is never bound.</b> It opens the pause menu and backs out of every screen, and it
    /// is the key that cancels a rebind, so it cannot be taken by an action.
    /// </para>
    /// </remarks>
    public sealed class KeyBindingSet
    {
        public const int Slots = 2;

        private readonly KeyCode[] _keys = new KeyCode[GameActionCatalog.All.Count * Slots];

        public KeyBindingSet() => ResetToDefaults();

        /// <summary>What <see cref="Bind"/> did.</summary>
        public readonly struct BindResult
        {
            public BindResult(bool accepted, bool displaced, GameAction displacedAction, int displacedSlot)
            {
                Accepted = accepted;
                Displaced = displaced;
                DisplacedAction = displacedAction;
                DisplacedSlot = displacedSlot;
            }

            /// <summary>False when the key may not be bound (Escape).</summary>
            public bool Accepted { get; }

            /// <summary>Whether another action lost this key to make room.</summary>
            public bool Displaced { get; }

            public GameAction DisplacedAction { get; }
            public int DisplacedSlot { get; }
        }

        /// <summary>The key in <paramref name="slot"/> (0 primary, 1 secondary) of <paramref name="action"/>.</summary>
        public KeyCode Get(GameAction action, int slot) => _keys[Index(action, slot)];

        /// <summary>Whether <paramref name="key"/> can be bound at all.</summary>
        public static bool IsBindable(KeyCode key) => key != KeyCode.None && key != KeyCode.Escape;

        /// <summary>
        /// Puts <paramref name="key"/> in <paramref name="slot"/> of <paramref name="action"/>, taking it
        /// from whichever slot held it before.
        /// </summary>
        public BindResult Bind(GameAction action, int slot, KeyCode key)
        {
            if (!IsBindable(key)) return new BindResult(false, false, default, 0);

            int target = Index(action, slot);
            bool displaced = false;
            GameAction displacedAction = default;
            int displacedSlot = 0;

            for (int i = 0; i < _keys.Length; i++)
            {
                if (i == target || _keys[i] != key) continue;

                _keys[i] = KeyCode.None;
                if (i / Slots == target / Slots)
                {
                    // The same action's other slot: the key just moves between its own slots.
                    continue;
                }

                displaced = true;
                displacedAction = (GameAction)(i / Slots);
                displacedSlot = i % Slots;
            }

            _keys[target] = key;
            return new BindResult(true, displaced, displacedAction, displacedSlot);
        }

        /// <summary>Leaves <paramref name="slot"/> of <paramref name="action"/> unbound.</summary>
        public void Clear(GameAction action, int slot) => _keys[Index(action, slot)] = KeyCode.None;

        /// <summary>Every action back on the keys the game shipped with.</summary>
        public void ResetToDefaults()
        {
            foreach (GameActionInfo info in GameActionCatalog.All)
            {
                _keys[Index(info.Action, 0)] = info.DefaultPrimary;
                _keys[Index(info.Action, 1)] = info.DefaultSecondary;
            }
        }

        /// <summary>Whether every action is on its shipped keys.</summary>
        public bool IsDefault
        {
            get
            {
                foreach (GameActionInfo info in GameActionCatalog.All)
                {
                    if (Get(info.Action, 0) != info.DefaultPrimary || Get(info.Action, 1) != info.DefaultSecondary)
                        return false;
                }
                return true;
            }
        }

        /// <summary>Whether <paramref name="action"/> has no key in either slot.</summary>
        public bool IsUnbound(GameAction action)
            => Get(action, 0) == KeyCode.None && Get(action, 1) == KeyCode.None;

        /// <summary>Copies every slot of <paramref name="other"/>.</summary>
        public void CopyFrom(KeyBindingSet other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            Array.Copy(other._keys, _keys, _keys.Length);
        }

        /// <summary>
        /// The bindings as text, <c>id=Primary,Secondary;...</c>, keyed by each action's stable id
        /// and Unity's own <see cref="KeyCode"/> names.
        /// </summary>
        public string Serialize()
        {
            var text = new StringBuilder();
            foreach (GameActionInfo info in GameActionCatalog.All)
            {
                if (text.Length > 0) text.Append(';');
                text.Append(info.Id).Append('=')
                    .Append(Get(info.Action, 0)).Append(',')
                    .Append(Get(info.Action, 1));
            }
            return text.ToString();
        }

        /// <summary>
        /// Reads what <see cref="Serialize"/> wrote. Starts from the defaults, so an action added
        /// after the text was saved, or an entry that does not parse, keeps its shipped keys rather
        /// than ending up unbound; a later entry that reuses a key takes it, as <see cref="Bind"/> would.
        /// </summary>
        public static KeyBindingSet Parse(string text)
        {
            var set = new KeyBindingSet();
            if (string.IsNullOrEmpty(text)) return set;

            var byId = new Dictionary<string, GameAction>(StringComparer.Ordinal);
            foreach (GameActionInfo info in GameActionCatalog.All) byId[info.Id] = info.Action;

            // Two passes: clear the slots the text speaks for, then bind, so a key the text moved
            // from one action to another is not displaced by its own old default.
            var parsed = new List<(GameAction Action, KeyCode Primary, KeyCode Secondary)>();
            foreach (string entry in text.Split(';'))
            {
                int equals = entry.IndexOf('=');
                if (equals <= 0) continue;
                if (!byId.TryGetValue(entry.Substring(0, equals).Trim(), out GameAction action)) continue;

                string[] keys = entry.Substring(equals + 1).Split(',');
                if (keys.Length != Slots) continue;
                if (!TryKey(keys[0], out KeyCode primary) || !TryKey(keys[1], out KeyCode secondary)) continue;

                parsed.Add((action, primary, secondary));
            }

            foreach ((GameAction action, _, _) in parsed)
            {
                set.Clear(action, 0);
                set.Clear(action, 1);
            }

            foreach ((GameAction action, KeyCode primary, KeyCode secondary) in parsed)
            {
                if (IsBindable(primary)) set.Bind(action, 0, primary);
                if (IsBindable(secondary)) set.Bind(action, 1, secondary);
            }

            return set;
        }

        private static bool TryKey(string name, out KeyCode key)
            => Enum.TryParse(name.Trim(), ignoreCase: false, out key) && Enum.IsDefined(typeof(KeyCode), key);

        private static int Index(GameAction action, int slot)
        {
            if (slot < 0 || slot >= Slots) throw new ArgumentOutOfRangeException(nameof(slot));
            return (int)action * Slots + slot;
        }
    }
}
