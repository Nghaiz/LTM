using System;
using UnityEngine;

namespace Pathfinding
{
	[Serializable]
	[AddComponentMenu("Pathfinding/Modifiers/Alternative Path")]
	[HelpURL("http://arongranberg.com/astar/docs/class_pathfinding_1_1_alternative_path.php")]
	public class AlternativePath : MonoModifier
	{
		/// <summary>
		/// The one lock every modifier takes to change a node's penalty or the pre-search hook.
		/// </summary>
		/// <remarks>
		/// <para>
		/// <b>Owner's practice session of 2026-10-10: 121,410 log lines and bots walking around
		/// walls that were not there.</b> Offline, <c>threadCount: -1</c> (AutomaticLowLoad) runs
		/// half the logical processors as path threads, sixteen on a 32-thread laptop, and
		/// <see cref="ApplyNow"/> runs on whichever thread is about to search. Each modifier locked
		/// only itself, so two of them adding to and taking from the same node raced: an addition
		/// was lost, the next subtraction took more than was there, and the <c>uint</c> wrapped to
		/// about 4.29 billion -- a wall that never went away, with "Very high penalty applied"
		/// printed every time a bot's path crossed it. The dedicated server never showed it: two
		/// vCPUs give it one path thread.
		/// </para>
		/// <para>
		/// The same lock guards the add and remove on <see cref="AstarPath.OnPathPreSearch"/>, which
		/// raced the same way: a lost subscription left its penalties on the graph for good.
		/// <c>WaterPathTags</c> takes it too, because it adds to node penalties while bots may
		/// already be searching.
		/// </para>
		/// </remarks>
		public static readonly object PenaltySync = new object();

		/// <summary>Whether this run has said, once, that a node lost a penalty under a modifier.</summary>
		private static bool resetReported;

		public int penalty = 1000;

		public int randomStep = 10;

		private GraphNode[] prevNodes;

		private int prevSeed;

		private int prevPenalty;

		private bool waitingForApply;

		private System.Random rnd = new System.Random();

		private readonly System.Random seedGenerator = new System.Random();

		private bool destroyed;

		private GraphNode[] toBeApplied;

		public override int Order
		{
			get
			{
				return 10;
			}
		}

		public override void Apply(Path p)
		{
			if (this == null)
			{
				return;
			}
			lock (PenaltySync)
			{
				toBeApplied = p.path.ToArray();
				if (!waitingForApply)
				{
					waitingForApply = true;
					AstarPath.OnPathPreSearch = (OnPathDelegate)Delegate.Combine(AstarPath.OnPathPreSearch, new OnPathDelegate(ApplyNow));
				}
			}
		}

		public new void OnDestroy()
		{
			destroyed = true;
			lock (PenaltySync)
			{
				if (!waitingForApply)
				{
					waitingForApply = true;
					AstarPath.OnPathPreSearch = (OnPathDelegate)Delegate.Combine(AstarPath.OnPathPreSearch, new OnPathDelegate(ClearOnDestroy));
				}
			}
			((MonoModifier)this).OnDestroy();
		}

		private void ClearOnDestroy(Path p)
		{
			lock (PenaltySync)
			{
				AstarPath.OnPathPreSearch = (OnPathDelegate)Delegate.Remove(AstarPath.OnPathPreSearch, new OnPathDelegate(ClearOnDestroy));
				waitingForApply = false;
				InversePrevious();
			}
		}

		/// <summary>
		/// <paramref name="current"/> less <paramref name="amount"/>, stopping at zero: a node whose
		/// penalty was reset under this modifier (a graph update, a rescan) has nothing left to give
		/// back, and wrapping round would make it a wall.
		/// </summary>
		public static uint Withdraw(uint current, int amount, out bool wasReset)
		{
			uint taken = amount > 0 ? (uint)amount : 0u;
			wasReset = current < taken;
			return wasReset ? 0u : current - taken;
		}

		private void InversePrevious()
		{
			int seed = prevSeed;
			rnd = new System.Random(seed);
			if (prevNodes == null)
			{
				return;
			}
			bool flag = false;
			int num = rnd.Next(randomStep);
			for (int i = num; i < prevNodes.Length; i += rnd.Next(1, randomStep))
			{
				prevNodes[i].Penalty = Withdraw(prevNodes[i].Penalty, prevPenalty, out bool wasReset);
				flag |= wasReset;
			}
			// Given back once: a second call before the next ApplyNow (destroyed between the two)
			// must not take the same penalty off again.
			prevNodes = null;
			if (flag && !resetReported)
			{
				resetReported = true;
				Debug.LogWarning("[ai] a graph update reset some path penalties while bots still held them; they were cleared to zero (said once per run).");
			}
		}

		private void ApplyNow(Path somePath)
		{
			lock (PenaltySync)
			{
				waitingForApply = false;
				AstarPath.OnPathPreSearch = (OnPathDelegate)Delegate.Remove(AstarPath.OnPathPreSearch, new OnPathDelegate(ApplyNow));
				InversePrevious();
				if (destroyed)
				{
					return;
				}
				int seed = seedGenerator.Next();
				rnd = new System.Random(seed);
				if (toBeApplied != null)
				{
					int num = rnd.Next(randomStep);
					for (int i = num; i < toBeApplied.Length; i += rnd.Next(1, randomStep))
					{
						toBeApplied[i].Penalty = (uint)(toBeApplied[i].Penalty + penalty);
					}
				}
				prevPenalty = penalty;
				prevSeed = seed;
				prevNodes = toBeApplied;
			}
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetOnLoad()
		{
			resetReported = false;
		}
	}
}
