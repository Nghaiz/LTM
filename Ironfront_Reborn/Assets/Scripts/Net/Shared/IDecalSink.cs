using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Where a blast leaves its scorch mark. Phase C4b.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One method, because one call site: the explosion presenter draws a scorch and nothing else
    /// in the client netcode draws a decal at all. <b>The decal-kind enum deliberately does NOT
    /// cross the seam.</b> A client that could pick any kind would be one argument away from
    /// drawing bullet chips for explosions again — which is exactly the bug debt-closure phase 2
    /// closed (ledger C-7), and it was a wrong enum member, not a wrong call.
    /// </para>
    /// <para>
    /// <b>Absent is a supported state.</b> A build with no decal manager registers nothing and
    /// blasts leave no mark, which is what a headless client and an EditMode test are.
    /// </para>
    /// </remarks>
    public interface IDecalSink
    {
        /// <summary>
        /// Puts a scorch mark of <paramref name="size"/> at <paramref name="position"/>, facing
        /// <paramref name="normal"/>.
        /// </summary>
        /// <remarks>
        /// The caller passes <c>Vector3.up</c>: there is still no surface normal on the wire, so
        /// this projects straight up rather than raycasting for one. A slightly wrong decal
        /// orientation is a cosmetic detail, not a correctness one.
        /// </remarks>
        void AddScorch(Vector3 position, Vector3 normal, float size);
    }
}
