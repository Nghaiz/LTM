using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The seats of the vehicle the player sits in, bottom-left: the key that takes each one,
    /// what it is for, and whether it is yours, taken or open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> The RHIB's bow gun is seat 5 of 5. Nothing on screen said a vehicle
    /// had seats to change to, or which key changed them, and online the keys did nothing at all
    /// until the server learned to arbitrate a switch (2026-11-08): the owner's report that the
    /// boat's gun "cannot be used" was, for most players, a gun nobody knew how to reach.
    /// Battlefield shows the same list in the same corner for the same reason.
    /// </para>
    /// <para>
    /// <b>Read every frame, redrawn on change.</b> The local seat comes from the player's own body
    /// (<see cref="NetClientBindings.LocalCrew"/>), right online and off; a networked client seats
    /// only its own player, so the other occupants are added from the snapshots
    /// (<see cref="RemoteActorRegistry.SeatTaken"/>). Hidden on foot, dead, and in a vehicle with a
    /// single seat, where there is nothing to switch to.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class VehicleSeatsView : MonoBehaviour
    {
        /// <summary>Rows the builder authors: the seat keys, F1 to F8.</summary>
        public const int MaxSeats = 8;

        /// <summary>The title strip's height, canvas units.</summary>
        public const float HeaderHeight = 30f;

        /// <summary>One seat's row, canvas units.</summary>
        public const float RowHeight = 28f;

        /// <summary>The key hint under the rows, canvas units.</summary>
        public const float FooterHeight = 24f;

        public const float Width = 260f;

        private const float FadeSpeed = 8f;

        private static readonly Color YouBacking = new Color(0.24f, 0.19f, 0.05f, 0.9f);
        private static readonly Color RowRest = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color OpenInk = new Color(0.45f, 1f, 0.62f);

        [SerializeField] private RectTransform _panel;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private Text _title;
        [SerializeField] private GameObject[] _rows;
        [SerializeField] private Image[] _rowBackings;
        [SerializeField] private Text[] _rowKeys;
        [SerializeField] private Text[] _rowRoles;
        [SerializeField] private Text[] _rowStates;

        private readonly LocalSeatLine[] _lines = new LocalSeatLine[MaxSeats];
        private RemoteActorRegistry _crew;
        private float _nextLookup;
        private int _shown = int.MinValue;

        private void Awake()
        {
            if (_group != null) _group.alpha = 0f;
        }

        private void LateUpdate()
        {
            int count = Read(out Transform vehicle, out VehicleKind kind, out int localSeat);
            bool showing = count > 1 && localSeat >= 0;
            if (showing) Draw(vehicle, kind, count, localSeat);
            if (_group == null) return;
            _group.alpha = Mathf.MoveTowards(_group.alpha, showing ? 1f : 0f, Time.unscaledDeltaTime * FadeSpeed);
        }

        private int Read(out Transform vehicle, out VehicleKind kind, out int localSeat)
        {
            vehicle = null;
            kind = default;
            localSeat = -1;
            LocalCrewProbe probe = NetClientBindings.LocalCrew;
            if (probe == null) return 0;

            int count = probe(_lines, out vehicle, out kind, out localSeat);
            if (count <= 0 || vehicle == null || !NetContext.IsClient || !FindCrew()) return count;

            for (int i = 0; i < count; i++)
                if (i != localSeat && _crew.SeatTaken(vehicle, i)) _lines[i].Taken = true;
            return count;
        }

        private bool FindCrew()
        {
            if (_crew != null) return true;
            if (Time.unscaledTime < _nextLookup) return false;
            _nextLookup = Time.unscaledTime + 1f;
            _crew = FindFirstObjectByType<RemoteActorRegistry>();
            return _crew != null;
        }

        private void Draw(Transform vehicle, VehicleKind kind, int count, int localSeat)
        {
            int state = Fingerprint(vehicle.GetInstanceID(), count, localSeat);
            if (state == _shown) return;
            _shown = state;

            if (_title != null) _title.text = SeatPromptWording.VehicleName(vehicle.name, kind);
            if (_panel != null)
                _panel.sizeDelta = new Vector2(Width, HeaderHeight + count * RowHeight + FooterHeight);

            int rows = _rows != null ? _rows.Length : 0;
            for (int i = 0; i < rows; i++)
            {
                bool used = i < count;
                if (_rows[i] != null) _rows[i].SetActive(used);
                if (!used) continue;

                LocalSeatLine line = _lines[i];
                bool mine = i == localSeat;
                Color roleInk = mine ? HudStyle.GoldInk : line.Taken ? HudStyle.Faint : HudStyle.Ink;

                SetText(_rowKeys, i, "F" + (i + 1), mine ? HudStyle.GoldInk : HudStyle.Muted);
                SetText(_rowRoles, i, SeatEntryOrder.Label(line.Role), roleInk);
                SetText(_rowStates, i, mine ? "YOU" : line.Taken ? "TAKEN" : "OPEN",
                    mine ? HudStyle.GoldInk : line.Taken ? HudStyle.Faint : OpenInk);
                if (_rowBackings != null && i < _rowBackings.Length && _rowBackings[i] != null)
                    _rowBackings[i].color = mine ? YouBacking : RowRest;
            }
        }

        private int Fingerprint(int vehicle, int count, int localSeat)
        {
            int hash = vehicle;
            hash = hash * 31 + count;
            hash = hash * 31 + localSeat;
            for (int i = 0; i < count; i++)
                hash = hash * 31 + ((int)_lines[i].Role << 1 | (_lines[i].Taken ? 1 : 0));
            return hash;
        }

        private static void SetText(Text[] texts, int index, string content, Color ink)
        {
            if (texts == null || index >= texts.Length || texts[index] == null) return;
            texts[index].text = content;
            texts[index].color = ink;
        }
    }
}
