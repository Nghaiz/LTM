namespace Ironfront.Net.Unity
{
    /// <summary>Bridge from the multiplayer menu assembly to the legacy settings owner.</summary>
    public interface IGameSettingsLauncher
    {
        bool IsAvailable { get; }
        void ShowSettings();
    }
}
