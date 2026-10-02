using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Remote bodies follow Unity's animation guidance: each is its own transform hierarchy, and
    /// none animates while nobody can see it.
    /// </summary>
    /// <remarks>
    /// Unity writes animation back one hierarchy at a time, so a hundred bodies under the
    /// registry were written back in series on one thread (Unity, <i>Optimize your game
    /// performance for consoles and PCs</i>, "Separate animating hierarchies" and "Update only
    /// when visible").
    /// </remarks>
    public sealed class RemoteBodyHierarchyTests
    {
        private const string ProxyPath = "Assets/Prefab/Remote Actor Proxy.prefab";

        private Scene _scene;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(_scene);
        }

        [Test]
        public void EveryRemoteBodyIsItsOwnHierarchyInTheRegistrysScene()
        {
            RemoteActorRegistry registry = NewRegistry(prewarm: 3);

            RemoteActorView[] bodies = Bodies();
            Assert.AreEqual(3, registry.PooledCount);
            Assert.AreEqual(3, bodies.Length, "the pool did not make its bodies in the registry's scene");
            foreach (RemoteActorView body in bodies)
            {
                Assert.IsNull(body.transform.parent,
                    "a remote body is parented, so every body shares one hierarchy and its animation is written back in series");
            }
        }

        [Test]
        public void TheRemoteBodiesGoWithTheirRegistry()
        {
            RemoteActorRegistry registry = NewRegistry(prewarm: 3);
            Assert.AreEqual(3, Bodies().Length, "Setup: the pool made no bodies");

            // Edit mode runs no lifecycle messages for this component: OnDestroy by hand, as a
            // destroyed registry's would run.
            Invoke(registry, "OnDestroy");
            Object.DestroyImmediate(registry.gameObject);

            Assert.IsEmpty(Bodies(), "the bodies outlived the registry that made them");
        }

        [Test]
        public void ARemoteBodyDoesNotAnimateWhileNobodySeesIt()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPath);
            Animator animator = prefab.GetComponentInChildren<Animator>();
            Assert.AreEqual(AnimatorCullingMode.CullCompletely, animator.cullingMode,
                "an off-screen remote body still runs its state machine every frame");
        }

        private RemoteActorRegistry NewRegistry(int prewarm)
        {
            var host = new GameObject("Registry");
            host.SetActive(false);
            SceneManager.MoveGameObjectToScene(host, _scene);
            RemoteActorRegistry registry = host.AddComponent<RemoteActorRegistry>();
            Set(registry, "_remoteActorPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPath));
            Set(registry, "_prewarm", prewarm);
            host.SetActive(true);
            // Edit mode sends no Awake to this component: the pool is made by hand.
            Invoke(registry, "Awake");
            return registry;
        }

        private static void Invoke(object target, string method)
        {
            MethodInfo info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(info, $"Setup: {target.GetType().Name} has no method {method}");
            info.Invoke(target, null);
        }

        private RemoteActorView[] Bodies()
        {
            var found = new System.Collections.Generic.List<RemoteActorView>();
            foreach (GameObject root in _scene.GetRootGameObjects())
                found.AddRange(root.GetComponentsInChildren<RemoteActorView>(true));
            return found.ToArray();
        }

        private static void Set(object target, string field, object value)
        {
            FieldInfo info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(info, $"Setup: {target.GetType().Name} has no field {field}");
            info.SetValue(target, value);
        }
    }
}
