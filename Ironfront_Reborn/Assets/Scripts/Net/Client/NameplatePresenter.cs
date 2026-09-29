using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Puts a name and a health bar over every other person's head, on both sides. Playtest
    /// 2026-09-28, feature 1; people only since the owner's report of 2026-09-29.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which heads.</b> Every remote body the registry draws that is alive and that
    /// <c>S_PLAYER_LIST</c> names -- a person, never a bot (<see cref="NameplateRules.PlateNameOf"/>):
    /// a plate over each of thirty bots was clutter that hid the few players worth finding. Then
    /// as <see cref="NameplateRules"/> allows: teammates out to 150 m and dimmed behind cover,
    /// enemies only in line of sight and within 70 m. The local player has no plate; the
    /// registry does not hold its own body.
    /// </para>
    /// <para>
    /// <b>Sight is the AI's.</b> Cover is a line from the camera to the plate against layer 0,
    /// the mask <c>AiActorController.CanSeeActor</c> tests its own sight with, so a player sees an
    /// enemy's plate exactly when a bot in their place could see that enemy. Each actor is
    /// re-tested five times a second, staggered, rather than every frame.
    /// </para>
    /// <para>
    /// <b>Last, and every frame.</b> Plates follow heads on screen, so they are placed after the
    /// camera has moved for the frame -- the execution order below -- or they trail it by one
    /// frame and swim whenever the player turns.
    /// </para>
    /// <para>
    /// <b>What a plate says</b> besides the name and health: how far away, the weapon in hand
    /// (or that the player is in a vehicle or the water), and a star when the player tops their
    /// side's board -- the same player the Tab board stars, by the score table's one order.
    /// </para>
    /// <para>
    /// Added at runtime by <see cref="NetClientCombatPresenter"/>, which owns the name tables,
    /// so the map scenes need no new component.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    public sealed class NameplatePresenter : MonoBehaviour
    {
        /// <summary>Layer 0, the world: what <c>AiActorController.CanSeeActor</c> casts against.</summary>
        private const int SightMask = 1 << 0;

        private const float SightRecheckSeconds = 0.2f;

        private NetClientCombatPresenter _combat;
        private RemoteActorRegistry _registry;

        private readonly bool[] _covered = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly float[] _nextSightCheck = new float[ProtocolConstants.MAX_ACTORS];

        /// <summary>Each side's top player, recomputed only when the scores change.</summary>
        private ushort _leader0;
        private ushort _leader1;
        private int _scoresRevision = -1;

        /// <summary>Hands the presenter what it reads. Called once by the combat presenter.</summary>
        internal void Bind(NetClientCombatPresenter combat, RemoteActorRegistry registry)
        {
            _combat = combat;
            _registry = registry;
        }

        private void OnDisable()
        {
            // Plates left up by a teardown would float over nothing with nobody to take them down.
            IMatchHud hud = NetClientBindings.MatchHud;
            if (hud == null) return;

            hud.BeginNameplates();
            hud.EndNameplates();
        }

        private void LateUpdate()
        {
            IMatchHud hud = NetClientBindings.MatchHud;
            if (hud == null) return;

            hud.BeginNameplates();

            Camera camera = Camera.main;
            if (camera != null && _combat != null && _registry != null)
                PlaceNameplates(hud, camera);

            hud.EndNameplates();
        }

        private void PlaceNameplates(IMatchHud hud, Camera camera)
        {
            bool hasTeam = NetClientPresenterGuard.TryResolveLocalTeam(out byte localTeam);
            Vector3 eye = camera.transform.position;
            float now = Time.time;

            PlayerScoreTable scores = _combat.Scores;
            if (scores.Revision != _scoresRevision)
            {
                _scoresRevision = scores.Revision;
                _leader0 = scores.LeaderOf(TeamId.Team0);
                _leader1 = scores.LeaderOf(TeamId.Team1);
            }

            for (ushort actorId = 1; actorId < ProtocolConstants.MAX_ACTORS; actorId++)
            {
                // First, so a bot costs one array read and never a sight cast.
                string name = NameplateRules.PlateNameOf(actorId, _combat.Names);
                if (name == null) continue;

                if (!_registry.TryFindView(actorId, out RemoteActorView view)) continue;
                if (view == null || !view.isActiveAndEnabled || !view.HasState) continue;

                RemoteActorVisualState state = view.State;
                if (!state.IsAlive || state.IsRagdoll) continue;

                Vector3 anchor = view.transform.position
                                 + Vector3.up * NameplateRules.AnchorHeight(
                                     state.IsCrouching, state.IsProne, state.IsSeated);

                float distance = Vector3.Distance(eye, anchor);
                bool teammate = hasTeam && state.Team == localTeam;
                bool covered = IsCovered(actorId, eye, anchor, now);

                float opacity = NameplateRules.Opacity(teammate, distance, covered);
                if (opacity <= 0f) continue;

                Vector3 screen = camera.WorldToScreenPoint(anchor);
                if (screen.z <= 0f) continue;

                var plate = new Nameplate(
                    actorId, screen.x, screen.y, NameplateRules.Scale(distance), opacity,
                    name, state.Team, NameplateRules.Health01(state.Health), teammate,
                    distance, state.WeaponId, state.IsSeated, state.IsInWater,
                    isLeader: actorId == _leader0 || actorId == _leader1);

                hud.SetNameplate(in plate);
            }
        }

        /// <summary>Whether cover stands between the camera and this plate, re-tested on a stagger.</summary>
        private bool IsCovered(ushort actorId, Vector3 eye, Vector3 anchor, float now)
        {
            if (now < _nextSightCheck[actorId]) return _covered[actorId];

            // Staggered by id, so forty actors do not all cast on the same frame.
            _nextSightCheck[actorId] = now + SightRecheckSeconds + actorId * 0.003f;
            _covered[actorId] = Physics.Linecast(eye, anchor, SightMask, QueryTriggerInteraction.Ignore);
            return _covered[actorId];
        }
    }
}
