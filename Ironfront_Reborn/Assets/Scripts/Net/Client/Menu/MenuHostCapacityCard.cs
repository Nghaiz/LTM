#nullable enable

using Ironfront.MasterClient;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The create-room form's SERVER CAPACITY card: how loaded the game-server host is, what this
    /// room would add, and whether the chosen map's server is free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The load bar is in percent of the host, not in bots.</b> A room costs the host a share
    /// before its first bot (<see cref="RoomCapacity.MatchCostUnits"/>), so "100 bots in play"
    /// alone does not say how full the host is; the bar does, and the slider beside it says the
    /// same thing in bots. Words come from <see cref="RoomBotChoice"/>.
    /// </para>
    /// <para>
    /// Built by <c>BuildMenuCanvas.BuildCapacityCard</c>. A null capacity is a master that has not
    /// answered yet: the bars empty and the card says it does not know.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuHostCapacityCard : MonoBehaviour
    {
        [SerializeField] private RectTransform? _loadNow;
        [SerializeField] private Image? _loadThisRoom;
        [SerializeField] private Text? _loadText;
        [SerializeField] private Text? _inPlayText;
        [SerializeField] private Text? _mapText;

        private static readonly Color Ready = new Color(0.23f, 0.86f, 0.51f, 1f);
        private static readonly Color Busy = new Color(1f, 0.60f, 0.18f, 1f);
        private static readonly Color Quiet = new Color(0.55f, 0.66f, 0.73f, 1f);

        /// <summary>Draws the host's state with a room of <paramref name="bots"/> on <paramref name="mapName"/>.</summary>
        public void Show(RoomCapacity? capacity, int bots, ushort mapId, string mapName)
        {
            float now = capacity != null ? RoomBotChoice.LoadFraction(capacity) : 0f;
            float share = capacity != null && RoomBotChoice.IsAllowed(bots, capacity)
                ? Mathf.Min(RoomBotChoice.ShareFraction(capacity, bots), 1f - now)
                : 0f;

            if (_loadNow != null)
            {
                _loadNow.anchorMin = new Vector2(0f, 0f);
                _loadNow.anchorMax = new Vector2(now, 1f);
                _loadNow.offsetMin = Vector2.zero;
                _loadNow.offsetMax = Vector2.zero;
            }

            if (_loadThisRoom != null)
            {
                RectTransform rect = _loadThisRoom.rectTransform;
                rect.anchorMin = new Vector2(now, 0f);
                rect.anchorMax = new Vector2(now + share, 1f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                _loadThisRoom.color = ColourOf(RoomBotChoice.TierHex(RoomBotChoice.TierOf(bots)));
            }

            if (_loadText != null)
            {
                _loadText.text = capacity != null
                    ? RoomBotChoice.LoadText(capacity, RoomBotChoice.IsAllowed(bots, capacity) ? bots : 0)
                    : "Server load unknown until the room list answers.";
            }

            if (_inPlayText != null)
                _inPlayText.text = capacity != null ? RoomBotChoice.InPlayText(capacity) : string.Empty;

            if (_mapText != null)
            {
                _mapText.text = RoomBotChoice.MapText(capacity, mapId, mapName) ?? string.Empty;
                _mapText.color = capacity == null ? Quiet
                    : RoomBotChoice.MapIsBusy(capacity, mapId) ? Busy
                    : Ready;
            }
        }

        private static Color ColourOf(string rgb)
            => ColorUtility.TryParseHtmlString("#" + rgb, out Color colour) ? colour : Color.white;
    }
}
