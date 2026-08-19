using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The second half of a server tick: capture the simulated world and send it — after Unity
    /// has simulated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At +200 this runs after the default-order <c>Actor</c> and <c>AiActorController</c>
    /// <c>FixedUpdate</c>s and after the physics step, so the snapshot describes the world at
    /// the end of the tick it is labelled with. Capturing before the simulation instead is the
    /// classic off-by-one-tick bug: everything looks correct, and every client is exactly one
    /// tick behind the server forever.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(200)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ServerTickLoop))]
    public sealed class ServerSnapshotStage : MonoBehaviour
    {
        private ServerTickLoop _loop;

        private void Awake() => _loop = GetComponent<ServerTickLoop>();

        private void FixedUpdate()
        {
            if (_loop != null) _loop.RunSnapshotStage();
        }
    }
}
