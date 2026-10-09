namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Receives <c>C_NIGHT_VISION</c> (14.0.4): a player's game saying night vision went on or off.
    /// </summary>
    /// <remarks>
    /// Only the player's own game can see the key press, so this is its word; what it is used for
    /// (NAKED EYE, CREATURE OF THE NIGHT) only rewards NOT turning night vision on, so a game that
    /// lies can only cost its own player.
    /// </remarks>
    public interface INightVisionHandler
    {
        void OnNightVision(ClientSession session, bool on);
    }
}
