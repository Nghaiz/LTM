using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Ironfront.Net.Replication.Ai;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>
    /// <c>evaluate</c>: a profile (the shipped one, or it with <c>--set Name=Value;...</c>) against the
    /// original squads on the held-out rounds, by side size and real map; <c>--diagnose</c> adds kills,
    /// deaths and flags held. <c>trace</c>: one round told every 30 s (<c>--plans S</c> logs every plan
    /// for the first S seconds). <c>train</c>: tunes the profile from a seed and writes the report.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string verb = args.Length > 0 ? args[0] : "evaluate";
            ulong seed = ulong.Parse(Option(args, "--seed", "2026093001"), CultureInfo.InvariantCulture);
            switch (verb)
            {
            case "evaluate":
                Evaluate(args, seed);
                return 0;
            case "train":
                return Train(args, seed);
            case "trace":
                Trace(args, seed);
                return 0;
            default:
                Console.Error.WriteLine(
                    "usage: evaluate [--seed N] [--set Name=Value;...] [--diagnose]\n"
                    + "       trace [--seed N] [--map Dustbowl|Island] [--size N] [--original] [--plans S]\n"
                    + "       train [--seed N] [--generations G] [--population L] [--out report.md]");
                return 2;
            }
        }

        private static void Evaluate(string[] args, ulong seed)
        {
            TacticsProfile profile = TacticsProfile.Default();
            foreach (string pair in Option(args, "--set", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split('=');
                ProfileParameter parameter = ProfileVector.Parameters.First(p => p.Name == kv[0]);
                parameter.Set(profile, float.Parse(kv[1], CultureInfo.InvariantCulture));
            }

            List<MatchSpec> rounds = TrainingPlan.TestRounds(seed);
            var clock = Stopwatch.StartNew();
            Console.WriteLine("original vs original: " + Line(Evaluation.Score(rounds, () => new OriginalPolicy(), () => new OriginalPolicy())));
            Console.WriteLine("profile  vs original: " + Line(Evaluation.Score(rounds, profile)));
            Console.WriteLine("  on training rounds: " + Line(Evaluation.Score(TrainingPlan.TrainRounds(seed), profile)));
            foreach (int size in TrainingPlan.TestSizes)
                Console.WriteLine($"  {size,3} a side: " + Line(Evaluation.Score(rounds.FindAll(r => r.BotsPerTeam == size), profile)));
            foreach (SimMap map in TrainingPlan.RealMaps())
                Console.WriteLine($"  {map.Name,-9}: " + Line(Evaluation.Score(rounds.FindAll(r => r.Map.Name == map.Name), profile)));
            Console.WriteLine($"{rounds.Count} rounds in {clock.Elapsed.TotalSeconds:F1}s");

            if (Array.IndexOf(args, "--diagnose") >= 0) Diagnose(rounds, profile);
        }

        /// <summary>Where a profile wins or loses: the kill ratio, and the flags each side held on average.</summary>
        private static void Diagnose(List<MatchSpec> rounds, TacticsProfile profile)
        {
            double kills = 0, deaths = 0, held = 0, enemyHeld = 0, seconds = 0;
            foreach (MatchSpec r in rounds)
            {
                ISidePolicy tested = new CommanderPolicy(profile.Clone()), original = new OriginalPolicy();
                var sim = new ConquestSim(r.Map, r.BotsPerTeam, r.CommanderTeam == 0 ? tested : original,
                    r.CommanderTeam == 1 ? tested : original, new SimRandom(r.Seed));
                SimResult result = sim.Run();
                int side = r.CommanderTeam;
                kills += sim.Deaths[1 - side];
                deaths += sim.Deaths[side];
                held += sim.FlagSeconds[side];
                enemyHeld += sim.FlagSeconds[1 - side];
                seconds += result.Seconds;
            }
            Console.WriteLine(FormattableString.Invariant(
                $"commander: kills {kills:0}, deaths {deaths:0} (k/d {kills / deaths:0.00}); flags held on average {held / seconds:0.00} against {enemyHeld / seconds:0.00}"));
        }

        /// <summary>One round on a real map, the commander as blue against the original as red, told every 30 s.</summary>
        private static void Trace(string[] args, ulong seed)
        {
            string mapName = Option(args, "--map", "Dustbowl");
            int size = int.Parse(Option(args, "--size", "16"), CultureInfo.InvariantCulture);
            bool original = Array.IndexOf(args, "--original") >= 0;
            SimMap map = mapName == "Island" ? SimMap.Island() : SimMap.Dustbowl();
            var log = new System.Text.StringBuilder();
            ISidePolicy blue = original ? new OriginalPolicy() : new CommanderPolicy(TacticsProfile.Default())
            {
                Log = line => log.AppendLine(line),
                LogUntil = float.Parse(Option(args, "--plans", "0"), CultureInfo.InvariantCulture),
            };
            var sim = new ConquestSim(map, size, new RoundTrace(blue, 30f, log), new OriginalPolicy(), new SimRandom(seed));
            SimResult result = sim.Run();
            Console.Write(log);
            Console.WriteLine($"winner {result.Winner}, {result.Score0}-{result.Score1} after {result.Seconds:0}s, eliminated {result.Eliminated}");
        }

        private static int Train(string[] args, ulong seed)
        {
            int generations = int.Parse(Option(args, "--generations", "100"), CultureInfo.InvariantCulture);
            int population = int.Parse(Option(args, "--population", "32"), CultureInfo.InvariantCulture);
            string output = Option(args, "--out", "");
            var clock = Stopwatch.StartNew();

            // From the hand-set numbers (the profile's own initialisers), not from the shipped
            // Default, which is this run's own output: a re-run starts where the first one did.
            TrainingRun run = ProfileTrainer.Train(new TacticsProfile(), seed, generations, population, Console.WriteLine);
            string report = TrainingReport.Write(run, clock.Elapsed);
            Console.WriteLine(report);
            if (output.Length > 0) File.WriteAllText(output, report);
            return 0;
        }

        public static string Line(EvaluationScore s)
            => string.Create(CultureInfo.InvariantCulture,
                $"won {s.Wins}, lost {s.Losses}, drawn {s.Draws} of {s.Rounds} ({s.WinRate:P0}); mean margin {s.MeanMargin:+0.000;-0.000}");

        private static string Option(string[] args, string name, string fallback)
        {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
        }
    }
}
