using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// "F  DRIVE THE JEEP" over a vehicle the seat key can reach right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same vehicle the key picks.</b> <see cref="ClientSeatRequester.TryGetEnterCandidate"/>
    /// runs the requester's own nearest-seat search, so the prompt never names a vehicle the key
    /// would not ask for, and disappears while seated, dead or waiting on an answer.
    /// </para>
    /// <para>
    /// <b>Placed for any approach.</b> The prompt hangs over the driver's seat while that point is
    /// on screen, kept clear of the crosshair and the screen edges; walking up from behind, or
    /// standing beside the hull looking away, it docks low in the middle of the screen with an
    /// arrow toward the vehicle, so it is never off screen while the key would work.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SeatPromptView : MonoBehaviour
    {
        [SerializeField] private RectTransform _panel;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private Text _key;
        [SerializeField] private GameObject _keyCap;
        [SerializeField] private Text _action;
        [SerializeField] private Text _detail;
        [Tooltip("Points toward the vehicle while the prompt is docked; hidden over the vehicle.")]
        [SerializeField] private RectTransform _pointer;

        /// <summary>Above the driver's seat, metres: over the roof of a jeep, the hatch of a tank.</summary>
        private const float LiftMetres = 1.4f;

        /// <summary>Canvas units the prompt keeps from the crosshair.</summary>
        private const float CrosshairClearance = 110f;

        /// <summary>Canvas units the prompt keeps from every screen edge.</summary>
        private const float EdgeMargin = 24f;

        /// <summary>Where the docked prompt sits, as a share of the screen height.</summary>
        private const float DockHeight = 0.3f;

        private const float FadeSpeed = 10f;

        private static readonly Color EnemyInk = new Color(1f, 0.45f, 0.38f, 1f);

        private ClientSeatRequester _requester;
        private RemoteActorRegistry _crew;
        private float _nextLookup;
        private ushort _shownVehicle;
        private int _shownCrew = -1;
        private bool _shownEnemy;
        private Color _detailInk;

        private void Awake()
        {
            if (_group != null) _group.alpha = 0f;
            if (_detail != null) _detailInk = _detail.color;
            if (_key != null) _key.text = SeatPromptWording.Key;
            if (_pointer != null && _pointer.TryGetComponent(out Image caret)) caret.sprite = HudSprites.Caret();
        }

        private void LateUpdate()
        {
            bool showing = TryPlace();
            if (_group == null) return;
            _group.alpha = Mathf.MoveTowards(_group.alpha, showing ? 1f : 0f, Time.unscaledDeltaTime * FadeSpeed);
        }

        private bool TryPlace()
        {
            if (!FindRequester()) return false;
            if (!_requester.TryGetEnterCandidate(out NetClientVehicle vehicle, out Vector3 seat)) return false;
            if (vehicle.Body == null || vehicle.Body.Transform == null) return false;

            Camera view = Camera.main;
            if (view == null || _panel == null) return false;

            Describe(vehicle);
            Position(view, seat + Vector3.up * LiftMetres);
            return true;
        }

        private bool FindRequester()
        {
            if (_requester != null) return true;
            if (Time.unscaledTime < _nextLookup) return false;
            _nextLookup = Time.unscaledTime + 1f;
            _requester = FindFirstObjectByType<ClientSeatRequester>();
            if (_requester != null) _crew = _requester.GetComponent<RemoteActorRegistry>();
            return _requester != null;
        }

        private void Describe(NetClientVehicle vehicle)
        {
            byte crewTeam = TeamId.None;
            int crew = _crew != null ? _crew.CrewCount(vehicle.Body.Transform, out crewTeam) : 0;
            bool enemy = crew > 0 && crewTeam != TeamId.None
                && NetPresenterGate.TryResolveLocalTeam(out byte local) && local != crewTeam;

            if (vehicle.VehicleId == _shownVehicle && crew == _shownCrew && enemy == _shownEnemy) return;
            _shownVehicle = vehicle.VehicleId;
            _shownCrew = crew;
            _shownEnemy = enemy;

            string name = SeatPromptWording.VehicleName(vehicle.Body.Transform.name, vehicle.Kind);
            bool canBoard = SeatPromptWording.CanBoard(crew, vehicle.SeatCount);
            if (_action != null) _action.text = SeatPromptWording.Action(vehicle.Kind, name, crew, vehicle.SeatCount);
            if (_detail != null)
            {
                _detail.text = SeatPromptWording.Detail(crew, vehicle.SeatCount, enemy);
                _detail.color = enemy ? EnemyInk : _detailInk;
            }
            if (_keyCap != null) _keyCap.SetActive(canBoard);
        }

        private void Position(Camera view, Vector3 world)
        {
            var canvas = (RectTransform)_panel.parent;
            Vector2 size = canvas.rect.size;
            Vector3 screen = view.WorldToScreenPoint(world);

            // Screen pixels to this Canvas's units, which scale with the window.
            float scale = Screen.width > 0 ? size.x / Screen.width : 1f;
            Vector2 at = new Vector2(screen.x, screen.y) * scale;
            Vector2 half = _panel.rect.size * 0.5f;
            Vector2 centre = size * 0.5f;

            bool inFront = screen.z > 0.1f;
            bool onScreen = inFront
                && at.x >= half.x + EdgeMargin && at.x <= size.x - half.x - EdgeMargin
                && at.y >= half.y + EdgeMargin && at.y <= size.y - half.y - EdgeMargin;

            if (onScreen)
            {
                // Over the vehicle, but never across the crosshair: a jeep filling the screen
                // would otherwise put the words exactly where the player aims.
                Vector2 fromCentre = at - centre;
                if (Mathf.Abs(fromCentre.x) < half.x + CrosshairClearance * 0.5f
                    && Mathf.Abs(fromCentre.y) < half.y + CrosshairClearance * 0.5f)
                {
                    at.y = centre.y + half.y + CrosshairClearance * 0.5f;
                }
                SetPointer(false, 0f);
            }
            else
            {
                at = new Vector2(centre.x, size.y * DockHeight);
                // Left or right of where the camera looks; behind reads as whichever side is nearer.
                Vector3 local = view.transform.InverseTransformPoint(world);
                float bearing = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                SetPointer(true, bearing);
            }

            _panel.anchorMin = Vector2.zero;
            _panel.anchorMax = Vector2.zero;
            _panel.anchoredPosition = at;
        }

        private void SetPointer(bool shown, float bearing)
        {
            if (_pointer == null) return;
            if (_pointer.gameObject.activeSelf != shown) _pointer.gameObject.SetActive(shown);
            if (!shown) return;

            bool right = bearing >= 0f;
            float half = _panel.rect.width * 0.5f + 26f;
            _pointer.anchoredPosition = new Vector2(right ? half : -half, 0f);
            // The caret points down; a quarter turn either way points it at the vehicle's side.
            _pointer.localRotation = Quaternion.Euler(0f, 0f, right ? 90f : -90f);
        }
    }
}
