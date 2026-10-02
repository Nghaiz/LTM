using Ironfront.Net.Unity;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// With auto-sync off -- an online client, <c>ClientPhysicsSync</c> -- a move starts from where
    /// the body's transform was written, not from where physics last saw it.
    /// </summary>
    /// <remarks>
    /// <c>NetMovementAgent.ApplyStanceHeight</c> moves the transform with the controller enabled,
    /// and <c>PredictedViewInterpolator</c> puts the simulated position back on it before every
    /// tick; both are followed by a <c>CharacterController.Move</c> that, without a sync, starts
    /// from the stale position and writes it back over theirs.
    /// </remarks>
    public sealed class MoveFromWrittenTransformTests
    {
        private GameObject _body;
        private bool _autoSyncBefore;

        [SetUp]
        public void SetUp()
        {
            _autoSyncBefore = Physics.autoSyncTransforms;
        }

        [TearDown]
        public void TearDown()
        {
            Physics.autoSyncTransforms = _autoSyncBefore;
            LeavePhysicsSettingsClean();
            if (_body != null) Object.DestroyImmediate(_body);
        }

        // Physics.autoSyncTransforms is a project setting: writing it marks DynamicsManager.asset
        // dirty, and the Editor then re-saves that file on exit in its current format, a diff
        // nobody made. The value is put back above, so there is nothing to save.
        private static void LeavePhysicsSettingsClean()
        {
            foreach (Object settings in AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset"))
                EditorUtility.ClearDirty(settings);
        }

        [Test]
        public void AMoveStartsFromTheWrittenTransformWithAutoSyncOff()
        {
            _body = new GameObject("body");
            _body.AddComponent<CharacterController>();
            NetMovementAgent agent = _body.AddComponent<NetMovementAgent>();

            Physics.autoSyncTransforms = false;
            Physics.SyncTransforms();
            _body.transform.position = new Vector3(0f, 2f, 0f);

            agent.CharacterMove(new Vector3(0.05f, 0f, 0f));

            Assert.AreEqual(2f, _body.transform.position.y, 0.01f,
                "the move started where physics last saw the body and undid the write");
            Assert.AreEqual(0.05f, _body.transform.position.x, 0.01f);
        }
    }
}
