namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Every player action a key can be bound to. The settings screen lists them in this order,
    /// grouped by <see cref="GameActionGroup"/>; the stored bindings name them by
    /// <see cref="GameActionCatalog"/>'s id, never by this number, so the list may grow.
    /// </summary>
    public enum GameAction : byte
    {
        MoveForward,
        MoveBackward,
        MoveLeft,
        MoveRight,
        Jump,
        Crouch,
        Sprint,
        LeanLeft,
        LeanRight,

        Fire,
        Aim,
        Reload,
        Weapon1,
        Weapon2,
        Weapon3,
        Weapon4,
        Weapon5,

        Use,
        NightVision,

        Map,
        Scoreboard,
        Chat,
        HowToPlay,
        ToggleHud,
        ZeroUp,
        ZeroDown,
    }

    /// <summary>The headings the settings screen and the guide's controls tab group actions under.</summary>
    public enum GameActionGroup : byte
    {
        Movement,
        Combat,
        Vehicles,
        Interface,
    }
}
