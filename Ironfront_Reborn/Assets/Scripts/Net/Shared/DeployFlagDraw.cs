using System;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The draw a deploy with no flag picked uses: any flag the team owns, each equally likely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One rule, two callers.</b> The server draws this way for a request that names no flag
    /// (<c>ServerCombatBridge.ChooseSpawnIndex</c>), and since phase P35 the client draws the same
    /// way while the player waits, so it knows where the deploy will land and can read the grass
    /// there before the camera jumps. The request then names the drawn flag, which the server
    /// honours like a click, so the player's odds of landing on each flag are unchanged.
    /// </para>
    /// <para>
    /// Reservoir sampling: one pass, no allocation. <paramref name="nextBelow"/> answers a number in
    /// <c>[0, n)</c>, <c>UnityEngine.Random.Range(0, n)</c> in the game, a seeded generator in tests.
    /// </para>
    /// </remarks>
    public static class DeployFlagDraw
    {
        /// <summary>
        /// The index of one of <paramref name="count"/> flags for which <paramref name="eligible"/>
        /// answers true, each equally likely, or -1 when none is.
        /// </summary>
        public static int Uniform(int count, Func<int, bool> eligible, Func<int, int> nextBelow)
        {
            if (eligible == null) throw new ArgumentNullException(nameof(eligible));
            if (nextBelow == null) throw new ArgumentNullException(nameof(nextBelow));

            int chosen = -1;
            int candidates = 0;
            for (int i = 0; i < count; i++)
            {
                if (!eligible(i)) continue;

                candidates++;
                if (nextBelow(candidates) == 0) chosen = i;
            }

            return chosen;
        }
    }
}
