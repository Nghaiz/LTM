using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Whether this process has a local player at all — a screen, a camera, a HUD, and somebody
    /// pressing keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A. The runtime half of phase-00 task 5 (assist step 03): one place that
    /// answers "is there a client here", so that every guard in the original game asks the same
    /// question the same way instead of inventing an answer locally.
    /// </para>
    /// <para>
    /// <b>Phase-00 task 5 asks for <c>NetContext.IsClient</c> and it would be a bug.</b>
    /// <see cref="NetContext.Role"/> starts at <see cref="NetRole.Offline"/> and only becomes
    /// <see cref="NetRole.Client"/> when a bootstrap declares it, so
    /// <c>if (NetContext.IsClient)</c> around a HUD call does not guard the server — it deletes
    /// the HUD from ordinary single-player, where the role is Offline forever.
    /// </para>
    /// <para>
    /// <b>The obvious repair, <c>!NetContext.IsServer</c>, is also wrong.</b> It is exactly the
    /// failure step 03 warns about: "a server that cannot host a local client, and that failure
    /// appears weeks later as the loopback test stopped working".
    /// <c>NetClientBootstrap</c> reads
    /// <c>if (!NetContext.IsServer) NetContext.SetRole(NetRole.Client)</c> — that line exists
    /// precisely so a server and a client can share one process, and in that configuration the
    /// role stays Server while a real player is looking at a real screen.
    /// </para>
    /// <para>
    /// So the role does not answer this question in either direction, and this class does not
    /// consult it. What does answer it is how the process was built and how it was launched: a
    /// dedicated-server build never has a client, and neither does anything started with
    /// <c>-batchmode</c>, which is what phase-00 criterion 2's headless run actually does. Both
    /// are properties of the process rather than of the scene, so they cannot be falsified by a
    /// missing bootstrap component.
    /// </para>
    /// </remarks>
    public static class LocalClient
    {
        /// <summary>
        /// True when a local player, its camera and the user interface exist in this process.
        /// </summary>
        public static bool Exists
        {
            get
            {
#if UNITY_SERVER
                // A dedicated-server build, by definition. Compile-time because it is a
                // property of the binary, not of what the binary is currently doing.
                return false;
#else
                // -batchmode: no window and no input device, whether or not -nographics was
                // also passed. A listen server in the Editor is NOT batch mode and correctly
                // reports true here.
                return !Application.isBatchMode;
#endif
            }
        }
    }
}
