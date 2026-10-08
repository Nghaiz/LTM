using System.Collections.Generic;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>What one bindable action is called, what it does and which keys it starts on.</summary>
    public readonly struct GameActionInfo
    {
        public GameActionInfo(
            GameAction action, string id, string name, string description, GameActionGroup group,
            KeyCode defaultPrimary, KeyCode defaultSecondary = KeyCode.None)
        {
            Action = action;
            Id = id;
            Name = name;
            Description = description;
            Group = group;
            DefaultPrimary = defaultPrimary;
            DefaultSecondary = defaultSecondary;
        }

        public GameAction Action { get; }

        /// <summary>The name a saved binding is stored under. Never change one: it would forget the player's key.</summary>
        public string Id { get; }

        /// <summary>The label on the settings screen and in the guide, e.g. "Lean left".</summary>
        public string Name { get; }

        /// <summary>One line on what the key does, for the guide's controls tab.</summary>
        public string Description { get; }

        public GameActionGroup Group { get; }
        public KeyCode DefaultPrimary { get; }
        public KeyCode DefaultSecondary { get; }
    }

    /// <summary>
    /// The one list of bindable actions and their default keys, which are the keys the game shipped
    /// with before bindings existed (<c>ProjectSettings/InputManager.asset</c> and the literal
    /// <c>KeyCode</c>s the scripts read), so a player who never opens the controls tab plays exactly
    /// as before.
    /// </summary>
    public static class GameActionCatalog
    {
        public static readonly IReadOnlyList<GameActionInfo> All = new[]
        {
            new GameActionInfo(GameAction.MoveForward, "move-forward", "Move forward", "Walk or run forward; drive forward in a vehicle.", GameActionGroup.Movement, KeyCode.W, KeyCode.UpArrow),
            new GameActionInfo(GameAction.MoveBackward, "move-backward", "Move backward", "Back off; brake and reverse in a vehicle.", GameActionGroup.Movement, KeyCode.S, KeyCode.DownArrow),
            new GameActionInfo(GameAction.MoveLeft, "move-left", "Strafe left", "Step left; steer left in a vehicle.", GameActionGroup.Movement, KeyCode.A, KeyCode.LeftArrow),
            new GameActionInfo(GameAction.MoveRight, "move-right", "Strafe right", "Step right; steer right in a vehicle.", GameActionGroup.Movement, KeyCode.D, KeyCode.RightArrow),
            new GameActionInfo(GameAction.Jump, "jump", "Jump", "Jump over low cover; swim up in water.", GameActionGroup.Movement, KeyCode.Space),
            new GameActionInfo(GameAction.Crouch, "crouch", "Crouch", "Crouch behind cover; steadier aim, slower feet.", GameActionGroup.Movement, KeyCode.LeftControl, KeyCode.C),
            new GameActionInfo(GameAction.Sprint, "sprint", "Sprint", "Run flat out. You cannot shoot while sprinting.", GameActionGroup.Movement, KeyCode.LeftShift),
            new GameActionInfo(GameAction.LeanLeft, "lean-left", "Lean left", "Peek round the left of cover.", GameActionGroup.Movement, KeyCode.Q),
            new GameActionInfo(GameAction.LeanRight, "lean-right", "Lean right", "Peek round the right of cover.", GameActionGroup.Movement, KeyCode.E),

            new GameActionInfo(GameAction.Fire, "fire", "Fire", "Shoot, throw, or fire the vehicle's gun.", GameActionGroup.Combat, KeyCode.Mouse0),
            new GameActionInfo(GameAction.Aim, "aim", "Aim down sights", "Look down the sights or through the scope.", GameActionGroup.Combat, KeyCode.Mouse1),
            new GameActionInfo(GameAction.Reload, "reload", "Reload", "Reload the weapon in your hands.", GameActionGroup.Combat, KeyCode.R),
            new GameActionInfo(GameAction.Weapon1, "weapon-1", "Primary weapon", "Switch to slot 1, your primary.", GameActionGroup.Combat, KeyCode.Alpha1),
            new GameActionInfo(GameAction.Weapon2, "weapon-2", "Secondary weapon", "Switch to slot 2, your sidearm.", GameActionGroup.Combat, KeyCode.Alpha2),
            new GameActionInfo(GameAction.Weapon3, "weapon-3", "Gear slot 1", "Switch to slot 3 (grenade, launcher, tool).", GameActionGroup.Combat, KeyCode.Alpha3),
            new GameActionInfo(GameAction.Weapon4, "weapon-4", "Gear slot 2", "Switch to slot 4.", GameActionGroup.Combat, KeyCode.Alpha4),
            new GameActionInfo(GameAction.Weapon5, "weapon-5", "Gear slot 3", "Switch to slot 5.", GameActionGroup.Combat, KeyCode.Alpha5),

            new GameActionInfo(GameAction.Use, "use", "Enter / exit vehicle", "Board the vehicle you look at, or get out.", GameActionGroup.Vehicles, KeyCode.F),
            new GameActionInfo(GameAction.NightVision, "night-vision", "Night vision", "Goggles on or off in a Night Mode match.", GameActionGroup.Vehicles, KeyCode.N),

            new GameActionInfo(GameAction.Map, "map", "Map (hold)", "Hold to open the full map; the wheel zooms it.", GameActionGroup.Interface, KeyCode.M),
            new GameActionInfo(GameAction.Scoreboard, "scoreboard", "Scoreboard", "Open or close the match scoreboard.", GameActionGroup.Interface, KeyCode.Tab),
            new GameActionInfo(GameAction.Chat, "chat", "Chat", "Open the chat line; Tab switches team / all.", GameActionGroup.Interface, KeyCode.Return, KeyCode.KeypadEnter),
            new GameActionInfo(GameAction.HowToPlay, "how-to-play", "How to play", "Open this guide from the menus and the deploy screen.", GameActionGroup.Interface, KeyCode.H),
            new GameActionInfo(GameAction.ToggleHud, "toggle-hud", "Hide HUD", "Hide or show the on-screen readout.", GameActionGroup.Interface, KeyCode.End),
        };

        /// <summary>The entry for <paramref name="action"/>.</summary>
        public static GameActionInfo Get(GameAction action) => All[(int)action];

        /// <summary>The heading text for <paramref name="group"/>.</summary>
        public static string GroupName(GameActionGroup group)
        {
            switch (group)
            {
                case GameActionGroup.Movement: return "MOVEMENT";
                case GameActionGroup.Combat: return "COMBAT";
                case GameActionGroup.Vehicles: return "VEHICLES & GEAR";
                default: return "INTERFACE";
            }
        }
    }
}
