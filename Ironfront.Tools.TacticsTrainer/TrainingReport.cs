using System;
using System.Globalization;
using System.Text;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>The run as markdown, for plans/reports.</summary>
    public static class TrainingReport
    {
        public static string Write(TrainingRun run, TimeSpan elapsed)
        {
            var md = new StringBuilder();
            md.Append("# P28 part 4 — training the team commander's profile\n\n");
            md.Append(Inv($"Seed {run.Seed}, {run.Generations} generations of {run.Population}, {run.TrainRounds} training rounds per candidate, {run.TestRounds} held-out test rounds; {elapsed.TotalMinutes:0.0} min.\n\n"));
            md.Append("Reproduce: `dotnet run --project Ironfront.Tools.TacticsTrainer -c Release -- train "
                + Inv($"--seed {run.Seed} --generations {run.Generations} --population {run.Population}`\n\n"));
            md.Append("**What was trained, and on what.** The team commander's strategic weights (`TacticsProfile`: how many "
                + "flags it goes for at once, how much it keeps back and where, when it flanks, how far an attack breaks off "
                + "for a flag it passes), by a (mu/mu_w, lambda) evolution strategy, against the original game's squads, in "
                + "`ConquestSim`: an abstract model of the conquest mode with the real maps' flags, the game's capture and "
                + "scoring rules, and the exact `TeamPlanner` the server runs. It is not a neural network, and it does not "
                + "touch how a bot aims, moves or takes cover. A round is scored as a win (+1), a loss (-1), or, when the "
                + "20-minute clock the model adds runs out, the lead as a share of the 200-point victory margin.\n\n");

            md.Append("## Held-out test rounds (the commander's side against the original squads)\n\n");
            md.Append("| | won | lost | drawn | win rate | mean margin |\n|---|---|---|---|---|---|\n");
            Row(md, "original vs original (sanity)", run.OriginalMirror);
            Row(md, "hand-set profile vs original", run.StartTest);
            Row(md, "trained profile vs original", run.TrainedTest);
            Row(md, "trained profile vs hand-set profile", run.TrainedVsStart);
            md.Append('\n');

            md.Append("### By side size and map\n\n| slice | hand-set | trained |\n|---|---|---|\n");
            foreach ((string label, EvaluationScore start, EvaluationScore trained) in run.BySlice)
            {
                md.Append("| ").Append(label).Append(" | ").Append(Short(start)).Append(" | ").Append(Short(trained)).Append(" |\n");
            }
            md.Append('\n');

            md.Append(Inv($"Training fitness (mean margin over the training rounds): hand-set {run.StartTrainFitness:+0.000;-0.000}, trained {run.TrainedTrainFitness:+0.000;-0.000}. "));
            md.Append(Inv($"The result was chosen from {run.Finalists} finalists on {run.ValidationRounds} validation rounds, where it scored {run.TrainedValidation:+0.000;-0.000}.\n\n"));

            md.Append("## Trained profile\n\n```\n").Append(ProfileVector.Describe(run.Trained)).Append("```\n\n");
            md.Append("## Hand-set profile\n\n```\n").Append(ProfileVector.Describe(run.Start)).Append("```\n\n");

            md.Append("## Generations\n\n| gen | sigma | best of generation | best so far |\n|---|---|---|---|\n");
            foreach (GenerationRecord g in run.History)
            {
                md.Append(Inv($"| {g.Generation} | {g.Sigma:0.000} | {g.BestOfGeneration:+0.000;-0.000} | {g.BestSoFar:+0.000;-0.000} |\n"));
            }
            return md.ToString();
        }

        private static void Row(StringBuilder md, string label, EvaluationScore s)
            => md.Append(Inv($"| {label} | {s.Wins} | {s.Losses} | {s.Draws} | {s.WinRate:P0} | {s.MeanMargin:+0.000;-0.000} |\n"));

        private static string Short(EvaluationScore s)
            => Inv($"{s.Wins}-{s.Losses}-{s.Draws} ({s.WinRate:P0}, {s.MeanMargin:+0.00;-0.00})");

        private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
