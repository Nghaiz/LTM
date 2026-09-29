using System;
using System.Globalization;
using System.Text;
using Ironfront.Net.Replication.Ai;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>
    /// One round told every <c>every</c> seconds: who holds which flag, the score, and what each
    /// side's squads are doing -- for reading a policy's behaviour, not for scoring it.
    /// </summary>
    public sealed class RoundTrace : ISidePolicy
    {
        private readonly ISidePolicy _inner;
        private readonly float _every;
        private readonly StringBuilder _log;
        private float _next;

        public RoundTrace(ISidePolicy inner, float every, StringBuilder log)
        {
            _inner = inner;
            _every = every;
            _log = log;
        }

        public void Tick(ConquestSim sim, int team)
        {
            _inner.Tick(sim, team);
            if (team != 0 || sim.Time < _next) return;
            _next = sim.Time + _every;

            var line = new StringBuilder();
            line.Append(string.Create(CultureInfo.InvariantCulture, $"t={sim.Time,5:0}s score {sim.Score(0),3}-{sim.Score(1),-3} flags "));
            for (int f = 0; f < sim.Owner.Length; f++)
                line.Append(sim.Owner[f] switch { 0 => 'B', 1 => 'R', _ => '.' });
            for (int t = 0; t < 2; t++)
            {
                int bots = 0, attack = 0, defend = 0, flank = 0, none = 0, dug = 0, engaged = 0;
                foreach (SimSquad s in sim.Squads)
                {
                    if (s.Team != t) continue;
                    bots += s.Size;
                    if (s.DugIn) dug++;
                    if (s.Engaged) engaged++;
                    switch (s.Role)
                    {
                    case SquadRole.Attack: attack++; break;
                    case SquadRole.Defend: defend++; break;
                    case SquadRole.Flank: flank++; break;
                    default: none++; break;
                    }
                }
                line.Append(t == 0 ? " | blue " : " | red ")
                    .Append(bots).Append(" bots: A").Append(attack).Append(" D").Append(defend)
                    .Append(" F").Append(flank).Append(" -").Append(none)
                    .Append(" dug ").Append(dug).Append(" fighting ").Append(engaged);
            }
            _log.Append(line).Append('\n');
        }
    }
}
