using System;
using UnityEngine;
using JobsUtility = Unity.Jobs.LowLevel.Unsafe.JobsUtility;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Shrinks Unity's job-worker pool to a limit, unless <c>-job-worker-count</c> was passed.
    /// </summary>
    /// <remarks>
    /// Shared by the headless server (<c>NetServerBootstrap.HeadlessJobWorkers</c>) and the
    /// rendering client (<see cref="CpuBudgetRules.ClientJobWorkers"/>); each states its own
    /// measurement. The command line always wins, so a player or an operator can choose another
    /// number without a build.
    /// </remarks>
    public static class JobWorkerCap
    {
        public static void Apply(int limit)
        {
            int before = JobsUtility.JobWorkerCount;
            int maximum = JobsUtility.JobWorkerMaximumCount;

            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-job-worker-count") >= 0)
            {
                Debug.Log($"[net] job workers {before} of {maximum}, from -job-worker-count.");
                return;
            }

            int capped = CpuBudgetRules.CappedJobWorkers(before, limit);
            if (capped != before) JobsUtility.JobWorkerCount = capped;

            Debug.Log(
                $"[net] job workers {JobsUtility.JobWorkerCount} "
                + $"of {maximum} (was {before}); pass -job-worker-count to choose another number.");
        }
    }
}
