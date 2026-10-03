using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// "F  DRIVE THE JEEP" over a vehicle the seat key can reach, while the player is looking at it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same vehicle the key picks.</b> <see cref="ClientSeatRequester.TryGetEnterCandidate"/>
    /// runs the requester's own nearest-seat search, so the prompt never names a vehicle the key
    /// would not ask for, and disappears while seated, dead or waiting on an answer.
    /// </para>
    /// <para>
    /// <b>Only while approaching it, never in the way.</b> The prompt hangs over the vehicle, and
    /// shows only while its seat is in front of the camera within <see cref="FacingHalfAngle"/> of
    /// where the player looks. Standing beside a jeep looking elsewhere shows nothing: owner ruling
    /// 2026-10-03, after a first version docked the prompt mid-screen with an arrow whenever any
    /// seat was in reach, which got in the way of players who did not want the vehicle.
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

        /// <summary>Above the driver's seat, metres: over the roof of a jeep, the hatch of a tank.</summary>
        private const float LiftMetres = 1.4f;

        /// <summary>How far off the view direction the seat may be, degrees, for the prompt to show.</summary>
        private const float FacingHalfAngle = 35f;

        /// <summary>Canvas units the prompt keeps from the crosshair.</summary>
        private const float CrosshairClearance = 110f;

        /// <summary>Canvas units the prompt keeps from every screen edge.</summary>
        private const float EdgeMargin = 24f;

        /// <summary>
        /// Canvas units kept clear at the top of the screen: the score bar, the radar and the
        /// top-right row live there.
        /// </summary>
        private const float TopReserve = 280f;

        private const float FadeSpeed = 10f;

        private static readonly Color EnemyInk = new Color(1f, 0.45f, 0.38f, 1f);

        private ClientSeatRequester _requester;
        private RemoteActorRegistry _crew;
        private float _nextLookup;
        private int _shownVehicle = int.MinValue;
        private int _shownCrew = -1;
        private bool _shownEnemy;
        private Color _detailInk;

        private void Awake()
        {
            if (_group != null) _group.alpha = 0f;
            if (_detail != null) _detailInk = _detail.color;
            if (_key != null) _key.text = SeatPromptWording.Key;
        }

        private void LateUpdate()
        {
            bool showing = TryPlace();
            if (_group == null) return;
            _group.alpha = Mathf.MoveTowards(_group.alpha, showing ? 1f : 0f, Time.unscaledDeltaTime * FadeSpeed);
        }

        private bool TryPlace()
        {
            Camera view = Camera.main;
            if (view == null || _panel == null) return false;

            // Offline, or the host playing on its own server: SampleUseRay enters seats there.
            if (!NetContext.IsClient)
            {
                OfflineSeatProbe probe = NetClientBindings.OfflineSeatCandidate;
                if (probe == null || !probe(out Transform offlineVehicle, out Vector3 offlineSeat,
                        out VehicleKind kind, out int crew, out int seats, out bool enemy))
                    return false;
                if (!TryPosition(view, offlineSeat)) return false;
                Describe(offlineVehicle.GetInstanceID(), offlineVehicle.name, kind, crew, seats, enemy);
                return true;
            }

            if (!FindRequester()) return false;
            if (!_requester.TryGetEnterCandidate(out NetClientVehicle vehicle, out Vector3 seat)) return false;
            if (vehicle.Body == null || vehicle.Body.Transform == null) return false;
            if (!TryPosition(view, seat)) return false;

            Describe(vehicle);
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

            Describe(vehicle.VehicleId, vehicle.Body.Transform.name, vehicle.Kind, crew, vehicle.SeatCount, enemy);
        }

        private void Describe(int identity, string objectName, VehicleKind kind, int crew, int seats, bool enemy)
        {
            if (identity == _shownVehicle && crew == _shownCrew && enemy == _shownEnemy) return;
            _shownVehicle = identity;
            _shownCrew = crew;
            _shownEnemy = enemy;

            string name = SeatPromptWording.VehicleName(objectName, kind);
            bool canBoard = SeatPromptWording.CanBoard(crew, seats);
            if (_action != null) _action.text = SeatPromptWording.Action(kind, name, crew, seats);
            if (_detail != null)
            {
                _detail.text = SeatPromptWording.Detail(crew, seats, enemy);
                _detail.color = enemy ? EnemyInk : _detailInk;
            }
            if (_keyCap != null) _keyCap.SetActive(canBoard);
        }

        /// <summary>
        /// Puts the prompt over the vehicle, or answers false when the player is not looking at it.
        /// </summary>
        private bool TryPosition(Camera view, Vector3 seat)
        {
            Vector3 toSeat = seat - view.transform.position;
            if (toSeat.sqrMagnitude < 1e-4f) return false;
            if (Vector3.Angle(view.transform.forward, toSeat) > FacingHalfAngle) return false;

            Vector3 screen = view.WorldToScreenPoint(seat + Vector3.up * LiftMetres);
            if (screen.z <= 0.1f) return false;

            var canvas = (RectTransform)_panel.parent;
            Vector2 size = canvas.rect.size;
            // Screen pixels to this Canvas's units, which scale with the window.
            float scale = Screen.width > 0 ? size.x / Screen.width : 1f;
            Vector2 at = new Vector2(screen.x, screen.y) * scale;
            Vector2 half = _panel.rect.size * 0.5f;
            Vector2 centre = size * 0.5f;

            // Over the vehicle, but never across the crosshair: a jeep filling the screen would
            // otherwise put the words exactly where the player aims.
            Vector2 fromCentre = at - centre;
            if (Mathf.Abs(fromCentre.x) < half.x + CrosshairClearance * 0.5f
                && Mathf.Abs(fromCentre.y) < half.y + CrosshairClearance * 0.5f)
            {
                at.y = centre.y + half.y + CrosshairClearance * 0.5f;
            }
            at.x = Mathf.Clamp(at.x, half.x + EdgeMargin, size.x - half.x - EdgeMargin);
            at.y = Mathf.Clamp(at.y, half.y + EdgeMargin, size.y - half.y - TopReserve);

            _panel.anchorMin = Vector2.zero;
            _panel.anchorMax = Vector2.zero;
            _panel.anchoredPosition = at;
            return true;
        }
    }
}
