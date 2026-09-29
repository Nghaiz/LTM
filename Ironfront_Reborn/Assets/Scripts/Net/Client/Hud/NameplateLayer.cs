using System.Collections.Generic;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// Holds the name plates: one per actor, cloned from an authored template, placed on this
    /// Canvas each frame. Playtest 2026-09-28, feature 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A plate follows its actor</b>, looked up by id rather than handed out in order, so the
    /// bar's trail and pulse belong to one body from frame to frame instead of jumping between
    /// heads as they cross.
    /// </para>
    /// <para>
    /// <b>Drawn beneath the rest of the readout</b>: the builder makes this the Canvas's first
    /// child, so the killfeed, the deploy screen and the board all cover it.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NameplateLayer : MonoBehaviour
    {
        [SerializeField] private NameplateView _template;

        private readonly NameplateView[] _byActor = new NameplateView[ProtocolConstants.MAX_ACTORS];
        private readonly bool[] _seen = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly Stack<NameplateView> _free = new Stack<NameplateView>();

        private RectTransform _rect;
        private Canvas _canvas;
        private bool _initialized;
        private bool _complete;

        private void Awake() => Initialize();

        /// <summary>
        /// Resolves the layer. <c>Awake</c> calls it; the capture tool calls it in edit mode.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _rect = (RectTransform)transform;
            _canvas = GetComponentInParent<Canvas>();
            _complete = _template != null && _canvas != null;

            if (!_complete)
            {
                Debug.LogError(
                    "[hud] " + name + " has no plate template; run \"Ironfront/Net/Build "
                    + "in-match readout\" to rebuild the name plates.", this);
                return;
            }

            _template.gameObject.SetActive(false);
        }

        /// <summary>Starts a frame. Plates not set before <see cref="End"/> are taken down.</summary>
        public void Begin() => System.Array.Clear(_seen, 0, _seen.Length);

        /// <summary>Draws one plate this frame, advancing its bar by the frame's time.</summary>
        public void Set(in Nameplate plate, Color teamInk) => Set(in plate, teamInk, Time.unscaledDeltaTime);

        /// <summary>Draws one plate this frame, advancing its bar by <paramref name="deltaSeconds"/>.</summary>
        public void Set(in Nameplate plate, Color teamInk, float deltaSeconds)
        {
            if (!_complete) return;

            ushort id = plate.ActorId;
            if (id == 0 || id >= _byActor.Length) return;

            NameplateView view = _byActor[id];
            if (view == null)
            {
                view = Take();
                if (view == null) return;
                _byActor[id] = view;
            }

            // An overlay Canvas maps screen points with no camera; a camera-space one (the
            // capture tool's) needs its own.
            Camera eye = _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _rect, new Vector2(plate.ScreenX, plate.ScreenY), eye, out Vector2 local);

            view.Show(in plate, local, teamInk, deltaSeconds);
            _seen[id] = true;
        }

        /// <summary>Ends the frame: every plate nobody set goes back to the pool.</summary>
        public void End()
        {
            for (int id = 0; id < _byActor.Length; id++)
            {
                NameplateView view = _byActor[id];
                if (view == null || _seen[id]) continue;

                view.Hide();
                _free.Push(view);
                _byActor[id] = null;
            }
        }

        private NameplateView Take()
        {
            if (_free.Count > 0) return _free.Pop();

            NameplateView clone = Instantiate(_template, _rect);
            clone.name = "Nameplate";
            clone.Initialize();

            if (clone.IsComplete) return clone;

            DestroyImmediate(clone.gameObject);
            return null;
        }
    }
}
