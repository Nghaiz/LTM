using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Turns <see cref="Physics.autoSyncTransforms"/> off for an online client's match and
    /// pushes moved transforms into physics once a frame instead, first thing in Update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> With auto-sync on, every rigidbody read and physics query made after a
    /// transform moved first re-syncs everything that moved, and each sync costs a job dispatch
    /// whether or not much moved: 63 of them a frame in a 100-bot Forest Lake match, about 3 ms
    /// (development profile, 2026-10-02). Priced in a release player that alternated the two every
    /// 30 seconds through one match (a measuring-only switch, 129 five-second windows): one sync a frame
    /// took the mean frame from 46.6 to 41.4 ms, Update from 10.3 to 7.2 ms, and p99 from 124 to
    /// 88 ms, with no more prediction corrections than auto-sync had.
    /// </para>
    /// <para>
    /// <b>What a client gives up</b> is a same-frame view of a transform written by script: a
    /// query made after such a write, before the next frame's sync, sees where the collider was.
    /// The physics step still syncs before it simulates, a remote vehicle writes its body as well
    /// as its transform (<see cref="NetClientVehicle"/>), and the local body syncs before every
    /// move (<see cref="NetMovementAgent.CharacterMove"/>), which are the writes a client acts on.
    /// </para>
    /// <para>
    /// <b>Online client only.</b> The server and practice run the original game's simulation,
    /// written against auto-sync, and keep it: <see cref="NetClientBootstrap"/> adds this past
    /// both its offline and its dedicated-server returns, and a process that turns out to be the
    /// server as well (a loopback session) gets auto-sync back here. Leaving the match puts it
    /// back too (<see cref="OnDisable"/>), so the menu and a later practice map see it on.
    /// </para>
    /// <para>
    /// <b>Switched off at the first Update, not in Awake</b>: the map's own start-up, every
    /// <c>Start</c> included, still runs with auto-sync as it always has.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-32000)]
    [DisallowMultipleComponent]
    public sealed class ClientPhysicsSync : MonoBehaviour
    {
        private bool _engaged;

        /// <summary>Whether this component has turned auto-sync off and is syncing each frame.</summary>
        public bool IsEngaged => _engaged;

        private void Update()
        {
            if (NetContext.IsServer)
            {
                Release();
                enabled = false;
                return;
            }

            Physics.SyncTransforms();
            if (_engaged) return;

            _engaged = true;
            Physics.autoSyncTransforms = false;
        }

        private void OnDisable()
        {
            Release();
        }

        private void Release()
        {
            if (!_engaged) return;
            _engaged = false;
            Physics.autoSyncTransforms = true;
        }
    }
}
