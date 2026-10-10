using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Bots' alternative-path penalties never wrap round, however many path threads share the graph
    /// (owner's practice session of 2026-10-10: 121,410 "Very high penalty applied" lines).
    /// </summary>
    /// <remarks>
    /// <c>Pathfinding.AlternativePath</c> and the graph nodes live in Assembly-CSharp, which no asmdef
    /// can reference, so they are reached by name. Nodes are made without their constructor, which
    /// would bind them to a live <c>AstarPath</c>; the penalty field is all these tests touch.
    /// </remarks>
    public sealed class AlternativePathPenaltyTests
    {
        private const int Threads = 8;
        private const int NodeCount = 64;
        private const int RoundsPerThread = 4000;

        private static readonly Type ModifierType = Find("Pathfinding.AlternativePath");
        private static readonly Type NodeType = Find("Pathfinding.PointNode");
        private static readonly Type GraphNodeType = Find("Pathfinding.GraphNode");

        private static readonly MethodInfo ApplyNow = ModifierType.GetMethod("ApplyNow", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo InversePrevious = ModifierType.GetMethod("InversePrevious", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo Withdraw = ModifierType.GetMethod("Withdraw", BindingFlags.Static | BindingFlags.Public);
        private static readonly FieldInfo ToBeApplied = ModifierType.GetField("toBeApplied", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo Penalty = GraphNodeType.GetProperty("Penalty");

        [Test]
        public void AWithdrawalStopsAtZeroInsteadOfWrapping()
        {
            object[] args = { 500u, 1000, false };
            uint left = (uint)Withdraw.Invoke(null, args);
            Assert.AreEqual(0u, left, "a node that lost its penalty must not wrap to ~4.29 billion");
            Assert.IsTrue((bool)args[2], "the reset is reported");

            args = new object[] { 1500u, 1000, false };
            Assert.AreEqual(500u, (uint)Withdraw.Invoke(null, args));
            Assert.IsFalse((bool)args[2]);
        }

        [Test]
        public void ConcurrentModifiersGiveBackExactlyWhatTheyTook()
        {
            Array nodes = Array.CreateInstance(GraphNodeType, NodeCount);
            for (int i = 0; i < NodeCount; i++) nodes.SetValue(FormatterServices.GetUninitializedObject(NodeType), i);

            // ApplyNow unhooks itself from this static delegate; whatever the Editor had there stays.
            FieldInfo hook = Find("AstarPath").GetField("OnPathPreSearch", BindingFlags.Static | BindingFlags.Public);
            object savedHook = hook.GetValue(null);

            var holders = new GameObject[Threads];
            var modifiers = new object[Threads];
            for (int t = 0; t < Threads; t++)
            {
                holders[t] = new GameObject("AlternativePath test " + t);
                modifiers[t] = holders[t].AddComponent(ModifierType);
                ToBeApplied.SetValue(modifiers[t], nodes);
            }

            try
            {
                Exception failure = null;
                var workers = new Thread[Threads];
                for (int t = 0; t < Threads; t++)
                {
                    object modifier = modifiers[t];
                    workers[t] = new Thread(() =>
                    {
                        try
                        {
                            for (int round = 0; round < RoundsPerThread; round++) ApplyNow.Invoke(modifier, new object[] { null });
                        }
                        catch (Exception e)
                        {
                            failure = e;
                        }
                    });
                }
                foreach (Thread worker in workers) worker.Start();
                foreach (Thread worker in workers) worker.Join();
                Assert.IsNull(failure, failure?.ToString());

                foreach (object modifier in modifiers) InversePrevious.Invoke(modifier, null);

                uint[] left = Enumerable.Range(0, NodeCount).Select(i => (uint)Penalty.GetValue(nodes.GetValue(i))).ToArray();
                Assert.That(left, Is.All.EqualTo(0u),
                    "every penalty a modifier added is taken back exactly once: a lost update leaves a node "
                    + "above zero, and a lost update plus the old unchecked subtraction wrapped it to ~4.29 billion");
            }
            finally
            {
                foreach (GameObject holder in holders) UnityEngine.Object.DestroyImmediate(holder);
                hook.SetValue(null, savedHook);
            }
        }

        private static Type Find(string name)
            => AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(name, false))
                .First(type => type != null);
    }
}
