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
    /// only a body the viewer can see on their own screen, on either side, within the side's range
    /// times the scope's zoom (<see cref="NameplateRules"/>). The local player has no plate; the
    /// registry does not hold its own body.
    /// </para>
    /// <para>
    /// <b>What "can see" means</b> (owner's report of 2026-09-30, which found a plate floating over
    /// the wall its player hid behind). Lines from the camera to three points ON the body -- head,
    /// chest, hips -- each one inside the view; the body is seen if any line reaches it without
    /// passing terrain, rocks, walls, trees (all on layer 0) or a vehicle. The old test aimed one
    /// line at the plate's anchor, half a metre over the head, which cleared the top of a wall the
    /// whole body was hiding behind; and it let teammates through cover on purpose. A seated body
    /// is tested against the world alone, or its own vehicle would hide every driver.
    /// </para>
    /// <para>
    /// <b>Tested twenty times a second, staggered</b> (<see cref="NameplateSight"/>): a plate holds
    /// through the gap between tests and fades within a quarter of a second of the body ducking
    /// out of sight, so a player behind cover is never marked for long.
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
        /// <summary>Layer 0, the world: terrain, rocks, walls and trees on both maps.</summary>
        private const int WorldMask = 1 << 0;

        /// <summary>Layer 12: a vehicle hides whoever stands behind it.</summary>
        private const int VehicleMask = 1 << 12;

        /// <summary>A body counts as in view this far past the edge of the screen, as a share of it.</summary>
        private const float ViewMargin = 0.02f;

        private NetClientCombatPresenter _combat;
        private RemoteActorRegistry _registry;

        private readonly NameplateSight _sight = new NameplateSight();
        private readonly float[] _heights = new float[3];

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

            // A scope narrows the view and brings bodies nearer; the ranges widen to match.
            float zoom = NameplateRules.ZoomFactor(camera.fieldOfView);

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

                Vector3 feet = view.transform.position;
                Vector3 anchor = feet
                                 + Vector3.up * NameplateRules.AnchorHeight(
                                     state.IsCrouching, state.IsProne, state.IsSeated);

                float distance = Vector3.Distance(eye, anchor);
                bool teammate = hasTeam && state.Team == localTeam;

                if (_sight.IsDue(actorId, now))
                {
                    // Staggered by id, so fifteen bodies do not all cast on the same frame.
                    bool seen = CanSee(
                        camera, eye, feet, state.IsCrouching, state.IsProne, state.IsSeated, _heights);
                    _sight.Report(actorId, seen, now, actorId * 0.0017f);
                }

                float presence = _sight.Presence(actorId, now);
                float opacity = presence * NameplateRules.Opacity(teammate, distance, presence > 0f, zoom);
                if (opacity <= 0f) continue;

                Vector3 screen = camera.WorldToScreenPoint(anchor);
                if (screen.z <= 0f) continue;

                var plate = new Nameplate(
                    actorId, screen.x, screen.y, NameplateRules.Scale(distance, zoom), opacity,
                    name, state.Team, NameplateRules.Health01(state.Health), teammate,
                    distance, state.WeaponId, state.IsSeated, state.IsInWater,
                    isLeader: actorId == _leader0 || actorId == _leader1);

                hud.SetNameplate(in plate);
            }
        }

        /// <summary>
        /// Whether the viewer can see any of the body's head, chest or hips: a point inside the
        /// view, with nothing solid on the line from the camera to it.
        /// </summary>
        /// <remarks>Static and internal so an edit-mode test can put a real wall in front of it.</remarks>
        internal static bool CanSee(
            Camera camera, Vector3 eye, Vector3 feet, bool crouching, bool prone, bool seated, float[] heights)
        {
            int points = NameplateRules.SightHeights(crouching, prone, seated, heights);

            // A seated body's own vehicle wraps round it; testing it would hide every driver.
            int mask = seated ? WorldMask : WorldMask | VehicleMask;

            for (int i = 0; i < points; i++)
            {
                Vector3 point = feet + Vector3.up * heights[i];

                Vector3 viewport = camera.WorldToViewportPoint(point);
                if (viewport.z <= 0f) continue;
                if (viewport.x < -ViewMargin || viewport.x > 1f + ViewMargin) continue;
                if (viewport.y < -ViewMargin || viewport.y > 1f + ViewMargin) continue;

                if (!Physics.Linecast(eye, point, mask, QueryTriggerInteraction.Ignore)) return true;
            }

            return false;
        }
    }
}
