using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// The practice match as its killfeed and Tab board read it: <see cref="MatchScoreboard"/>'s
    /// score and flags, and <see cref="ActorManager"/>'s soldiers. Owner's run of 2026-10-12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In <c>Assembly-CSharp</c> beside <see cref="IronfrontNetBindings"/>, which registers it, for
    /// that file's reason: it is the only assembly that can name both the legacy game and the
    /// seam the client reads.
    /// </para>
    /// <para>
    /// Everything it reads is this process's own offline match. It asks no server and no network
    /// anything, so the practice board works without an internet connection.
    /// </para>
    /// </remarks>
    internal sealed class PracticeMatchBinding : IPracticeMatchView
    {
        public PracticeScore Score
        {
            get
            {
                MatchScoreboard board = MatchScoreboard.Current;
                return new PracticeScore(
                    board.BlueScore, board.RedScore, board.BlueFlags, board.RedFlags, board.WinningTeam);
            }
        }

        public int FillActors(PracticeActor[] actors)
        {
            ActorManager manager = ActorManager.instance;
            List<Actor> soldiers = manager != null ? manager.actors : null;
            if (soldiers == null || actors == null) return 0;

            int count = 0;
            for (int i = 0; i < soldiers.Count && count < actors.Length; i++)
            {
                Actor soldier = soldiers[i];
                if (soldier == null) continue;

                actors[count++] = new PracticeActor(
                    soldier.GetInstanceID(),
                    soldier.team == 0 || soldier.team == 1 ? (byte)soldier.team : TeamId.None,
                    !soldier.aiControlled,
                    !soldier.dead,
                    soldier.IsSeated());
            }

            return count;
        }
    }
}
