using Ironfront.Net.Unity.Client.Overlay;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// Draws the practice match's killfeed and Tab board on the in-match HUD, as the online
    /// presenter draws a networked match's. Owner's run of 2026-10-12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added by <see cref="MatchHud"/> when the match is offline</b>, onto the HUD it draws on:
    /// the HUD prefab is instantiated for practice too, and this is the one thing that differs
    /// between the two. The work is <see cref="PracticeMatchReadout"/>'s; this only wires it to the
    /// offline game's reports (<see cref="PracticeFeed"/>), the frame clock and the scoreboard key.
    /// </para>
    /// <para>
    /// <b>Offline and local.</b> It reads the game running in this process and the player's own
    /// keyboard. The signed-in name, when there is one, is the session's own field; practice asks
    /// no server anything and plays the same with no internet, as "You".
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class PracticeMatchPresenter : MonoBehaviour
    {
        private readonly PracticeMatchReadout _readout = new PracticeMatchReadout();

        private IMatchHud _hud;

        /// <summary>The board's title: the map scene's name, read once, as the online board's is.</summary>
        private string _mapName = string.Empty;

        /// <summary>The HUD this presenter draws on.</summary>
        public void Bind(IMatchHud hud) => _hud = hud;

        private void Awake()
        {
            _mapName = gameObject.scene.name.ToUpperInvariant();
        }

        private void OnEnable()
        {
            PracticeFeed.Died += OnDied;
            PracticeFeed.FlagChanged += OnFlagChanged;

            // The account's name if one is signed in, read off the session already in memory.
            _readout.PlayerName = AchievementLedger.SignedIn ? AchievementLedger.Session.DisplayName : null;
        }

        private void OnDisable()
        {
            PracticeFeed.Died -= OnDied;
            PracticeFeed.FlagChanged -= OnFlagChanged;

            // A HUD going away leaves no rows and no board frozen on screen.
            _readout.Clear(_hud);
        }

        private void Update()
        {
            bool toggle = GameKeys.Down(GameAction.Scoreboard) && !LocalTextEntry.OwnsKeyboard;
            PracticeMatch match = PracticeFeed.Match;
            _readout.Tick(
                Time.time, _hud, NetClientBindings.PracticeView, toggle, _mapName, PracticeFeed.HasMatch, in match);
        }

        private void OnDied(PracticeDeath death) => _readout.OnDied(in death, Time.time);

        private void OnFlagChanged(PracticeFlagChange change) => _readout.OnFlagChanged(in change, Time.time);
    }
}
