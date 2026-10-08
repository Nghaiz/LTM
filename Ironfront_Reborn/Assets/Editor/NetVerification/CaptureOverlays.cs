using System.IO;
using Ironfront.Net.Unity.Client.Overlay;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// Renders an overlay page (settings, the guide, achievements, the ranking) to a 1920x1080 PNG
    /// in edit mode, for reviewing a layout without play mode or a match.
    /// </summary>
    /// <remarks>
    /// A detached host in a preview scene with its own camera; nothing in the open scene is touched.
    /// The scaler is pinned to the reference resolution, for <see cref="CaptureMenuScreens"/>' reason.
    /// </remarks>
    public static class CaptureOverlays
    {
        private const string OutputDirectory = "Temp/overlay-capture";

        /// <summary>
        /// Renders <paramref name="page"/> after <paramref name="prepare"/> has run against the shown
        /// host (switching a tab, filling sample rows), and returns the PNG's path.
        /// </summary>
        public static string Capture(OverlayPage page, System.Action<OverlayHost> prepare, string fileName)
        {
            Directory.CreateDirectory(OutputDirectory);
            Scene preview = EditorSceneManager.NewPreviewScene();
            var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            OverlayHost host = null;
            Camera camera = null;
            try
            {
                host = OverlayHost.CreateDetached();
                SceneManager.MoveGameObjectToScene(host.gameObject, preview);

                var cameraObject = new GameObject("Capture Camera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, preview);
                camera = cameraObject.GetComponent<Camera>();
                camera.scene = preview;
                camera.targetTexture = target;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.12f, 0.16f, 0.14f);

                var canvas = host.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;
                var scaler = host.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;

                host.ShowForTool(page);
                prepare?.Invoke(host);

                Canvas.ForceUpdateCanvases();
                camera.Render();

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                var pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                pixels.Apply();
                RenderTexture.active = previous;

                string path = Path.Combine(OutputDirectory, fileName);
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                Object.DestroyImmediate(pixels);
                return Path.GetFullPath(path);
            }
            finally
            {
                if (camera != null) camera.targetTexture = null;
                target.Release();
                Object.DestroyImmediate(target);
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        /// <summary>Clicks the button named <paramref name="name"/> anywhere under the host.</summary>
        public static void Click(OverlayHost host, string name)
        {
            foreach (Button button in host.GetComponentsInChildren<Button>(true))
            {
                if (button.name != name) continue;
                button.onClick.Invoke();
                return;
            }
            Debug.LogWarning("[capture-overlays] no button '" + name + "'");
        }
    }
}
