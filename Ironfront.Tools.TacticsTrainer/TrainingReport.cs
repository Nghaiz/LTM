using System;
using System.Globalization;
using System.Text;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>The run as markdown, for plans/reports.</summary>
    public static class TrainingReport
    {
        /// <summary>
        /// A signed margin. The third section matters: a small negative that rounds to zero is
        /// otherwise printed through the first section behind the minus sign, as "-+0.000".
        /// </summary>
        public const string Signed = "+0.000;-0.000;+0.000";

        public static string Write(TrainingRun run, TimeSpan elapsed)
        {
            var md = new StringBuilder();
            md.Append("# P29 part 4 — the commander v2, trained with CMA-ES against a league\n\n");
            md.Append(Inv($"Seed {run.Seed}, {run.Generations} generations of {run.Population}; each candidate played {run.TrainRounds} training rounds against the original squads and {run.LeagueRounds} against the P28 commander; {run.TestRounds} held-out test rounds; {elapsed.TotalMinutes:0.0} min.\n\n"));
            md.Append("Reproduce: `dotnet run --project Ironfront.Tools.TacticsTrainer -c Release -- train "
                + Inv($"--seed {run.Seed} --generations {run.Generations} --population {run.Population}`\n\n"));
            md.Append("**What was trained, and on what.** The weights (`TacticsProfile`) of the P29 team commander -- which prices "
                + "every target in bots with Lanchester's attrition law, takes the best value per bot while the bots are there, "
                + "remembers the enemies it has seen round each flag, and gathers an assault short of a defended flag -- by the "
                + "CMA-ES of Hansen's tutorial, in `ConquestSim`, the abstract conquest model of P28 part 4 now carrying the squad "
                + "upkeep the game has since #395. Fitness is the mean margin against a league: "
                + Inv($"{ProfileTrainer.OriginalWeight:P0} the original game's squads, {1 - ProfileTrainer.OriginalWeight:P0} the P28 commander as it shipped in v2.1.0 ")
                + "(frozen in `Baselines/`). A round is a win (+1), a loss (-1), or, when the 20-minute clock runs out, the lead "
                + "as a share of the 200-point margin. Strategic weights in an abstract model: not a neural network, and "
                + "nothing here changes how a bot aims, moves or takes cover.\n\n");

            md.Append("## Held-out test rounds\n\n");
            md.Append("| | won | lost | drawn | win rate | mean margin |\n|---|---|---|---|---|---|\n");
            Row(md, "original vs original (sanity)", run.OriginalMirror);
            Row(md, "P28 commander (v2.1.0) vs original", run.V1Test);
            Row(md, "P29 commander, start weights, vs original", run.StartTest);
            Row(md, "**P29 commander, trained, vs original**", run.TrainedTest);
            Row(md, "**P29 commander, trained, vs P28 commander**", run.TrainedVsV1);
            Row(md, "P29 trained vs itself (sanity)", run.TrainedMirror);
            md.Append('\n');

            md.Append("### By side size and map\n\n| slice | P28 vs original | P29 vs original | P29 vs P28 |\n|---|---|---|---|\n");
            foreach ((string label, EvaluationScore v1, EvaluationScore trained, EvaluationScore trainedVsV1) in run.BySlice)
            {
                md.Append("| ").Append(label).Append(" | ").Append(Short(v1)).Append(" | ").Append(Short(trained))
                  .Append(" | ").Append(Short(trainedVsV1)).Append(" |\n");
            }
            md.Append('\n');

            md.Append(Inv($"Training fitness: start {run.StartTrainFitness.ToString(Signed, CultureInfo.InvariantCulture)}, trained {run.TrainedTrainFitness.ToString(Signed, CultureInfo.InvariantCulture)}. "));
            md.Append(Inv($"The result was chosen from {run.Finalists} finalists on {run.ValidationRounds} validation rounds, where it scored {run.TrainedValidation.ToString(Signed, CultureInfo.InvariantCulture)}.\n\n"));

            md.Append("## Trained profile\n\n```\n").Append(ProfileVector.Describe(run.Trained)).Append("```\n\n");
            md.Append("## Start profile\n\n```\n").Append(ProfileVector.Describe(run.Start)).Append("```\n\n");

            md.Append("## Generations\n\n| gen | sigma | best of generation | best so far |\n|---|---|---|---|\n");
            foreach (GenerationRecord g in run.History)
            {
                md.Append(Inv($"| {g.Generation} | {g.Sigma:0.000} | {g.BestOfGeneration.ToString(Signed, CultureInfo.InvariantCulture)} | {g.BestSoFar.ToString(Signed, CultureInfo.InvariantCulture)} |\n"));
            }
            return md.ToString();
        }

        private static void Row(StringBuilder md, string label, EvaluationScore s)
            => md.Append(Inv($"| {label} | {s.Wins} | {s.Losses} | {s.Draws} | {s.WinRate:P0} | {s.MeanMargin.ToString(Signed, CultureInfo.InvariantCulture)} |\n"));

        private static string Short(EvaluationScore s)
            => Inv($"{s.Wins}-{s.Losses}-{s.Draws} ({s.WinRate:P0}, {s.MeanMargin.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture)})");

        private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
