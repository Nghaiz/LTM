using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The first half of a server tick: receive, then apply input — before Unity simulates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C.
    /// </para>
    /// <para>
    /// <b>Why this is a separate component (phase-01 trap 1).</b> The tick has to straddle
    /// Unity's own simulation: input must land <i>before</i> <c>Actor</c> and
    /// <c>AiActorController</c> run, and the snapshot must be captured <i>after</i>. Unity
    /// gives a script one execution order, not two, so the loop is split into this stage at
    /// -200 and <see cref="ServerSnapshotStage"/> at +200 with
    /// <see cref="ServerTickLoop"/> holding the state between them.
    /// </para>
    /// <para>
    /// The order is declared in the attribute rather than in
    /// <c>ProjectSettings/ScriptExecutionOrder</c>. The project settings file is Dev A's, so
    /// the alternative was a cross-owner dependency for a value that never changes — and one
    /// that would be invisible in a diff and silently absent for anyone who dropped these
    /// scripts into a different project.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ServerTickLoop))]
    public sealed class ServerInputStage : MonoBehaviour
    {
        private ServerTickLoop _loop;

        private void Awake() => _loop = GetComponent<ServerTickLoop>();

        private void FixedUpdate()
        {
            if (_loop != null) _loop.RunInputStage();
        }
    }
}
