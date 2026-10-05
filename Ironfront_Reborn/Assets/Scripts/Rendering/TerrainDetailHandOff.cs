using UnityEngine;

namespace Ironfront.Rendering
{
    /// <summary>
    /// Stops a terrain drawing its details while <see cref="InstancedDetailRenderer"/> draws them,
    /// leaving everything else it draws exactly as it was, and gives them back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not just its detail distance.</b> Every quality preset overrides the terrain's own
    /// detail distance and density (<c>terrainQualityOverrides</c> 255 in QualitySettings), so a
    /// terrain whose own distance is 0 still draws all its details (0 pixels changed, Editor,
    /// 2026-10-05) -- and the getters answer with the preset's values, not the terrain's. So the
    /// terrain is told to ignore the preset, and every value the preset would have given it is
    /// written into its own settings instead -- pixel error, basemap and tree distances, fade, max
    /// trees, detail density -- except the detail distance, which goes to 0. Measured: with the
    /// preset's values mirrored this way, a render is pixel-identical to the preset's own
    /// (0 of 921,600 pixels differ, Editor, 2026-10-05).
    /// </para>
    /// <para>
    /// <b>The preset keeps the say.</b> The mirror is redone every frame, so a preset picked in
    /// Settings reaches the terrain as before; a value someone else writes into the terrain
    /// meanwhile (<c>DetailObjectQuality</c> on an options save) is taken as the terrain's own and
    /// put back on release. Only the terrain's component changes, never its data or the project's
    /// quality settings, so nothing is written back to an asset in the Editor.
    /// </para>
    /// </remarks>
    internal sealed class TerrainDetailHandOff
    {
        private readonly Terrain _terrain;
        private bool _holding;
        private bool _ignoresPreset;
        private float _pixelError, _basemapDistance, _detailDensity, _detailDistance;
        private float _treeDistance, _billboardStart, _fadeLength;
        private int _maxFullTrees;

        // What was written last, as the terrain reads it back: a value that differs next frame
        // was written by someone else.
        private float _wPixelError, _wBasemapDistance, _wDetailDensity, _wTreeDistance, _wBillboardStart, _wFadeLength;
        private int _wMaxFullTrees;

        internal TerrainDetailHandOff(Terrain terrain) => _terrain = terrain;

        internal bool IsHolding => _holding;

        /// <summary>The distance the terrain draws its details to (or would, while held).</summary>
        internal float Distance => _holding
            ? Pick(TerrainQualityOverrides.DetailDistance, QualitySettings.terrainDetailDistance, _detailDistance)
            : _terrain.detailObjectDistance;

        /// <summary>The density the terrain draws its details at (or would, while held).</summary>
        internal float Density => _holding
            ? Pick(TerrainQualityOverrides.DetailDensity, QualitySettings.terrainDetailDensityScale, _detailDensity)
            : _terrain.detailObjectDensity;

        /// <summary>The terrain's own detail distance, given back on release.</summary>
        internal float OwnDetailDistance => _detailDistance;

        /// <summary>Takes the terrain's details, or keeps them, mirroring the preset into it.</summary>
        internal void Hold()
        {
            if (!_holding)
            {
                _ignoresPreset = _terrain.ignoreQualitySettings;
                // From here the getters answer with the terrain's own values.
                _terrain.ignoreQualitySettings = true;
                _pixelError = _terrain.heightmapPixelError;
                _basemapDistance = _terrain.basemapDistance;
                _detailDensity = _terrain.detailObjectDensity;
                _detailDistance = _terrain.detailObjectDistance;
                _treeDistance = _terrain.treeDistance;
                _billboardStart = _terrain.treeBillboardDistance;
                _fadeLength = _terrain.treeCrossFadeLength;
                _maxFullTrees = _terrain.treeMaximumFullLODCount;
                _holding = true;
            }
            else
            {
                Adopt();
            }
            Mirror();
        }

        /// <summary>Gives the terrain its details and its own settings back.</summary>
        internal void Release()
        {
            if (!_holding) return;
            Adopt();
            _terrain.heightmapPixelError = _pixelError;
            _terrain.basemapDistance = _basemapDistance;
            _terrain.detailObjectDensity = _detailDensity;
            _terrain.detailObjectDistance = _detailDistance;
            _terrain.treeDistance = _treeDistance;
            _terrain.treeBillboardDistance = _billboardStart;
            _terrain.treeCrossFadeLength = _fadeLength;
            _terrain.treeMaximumFullLODCount = _maxFullTrees;
            _terrain.ignoreQualitySettings = _ignoresPreset;
            _holding = false;
        }

        private float Pick(TerrainQualityOverrides setting, float preset, float own) =>
            !_ignoresPreset && (QualitySettings.terrainQualityOverrides & setting) != 0 ? preset : own;

        /// <summary>
        /// Takes any value written into the terrain by someone else since the last frame as its
        /// own, so <see cref="Distance"/> and <see cref="Density"/> answer with it.
        /// </summary>
        internal void Adopt()
        {
            if (!_holding) return;
            // Something turned the preset back on: the terrain's own choice now.
            if (!_terrain.ignoreQualitySettings) _ignoresPreset = false;
            if (_terrain.heightmapPixelError != _wPixelError) _pixelError = _terrain.heightmapPixelError;
            if (_terrain.basemapDistance != _wBasemapDistance) _basemapDistance = _terrain.basemapDistance;
            if (_terrain.detailObjectDensity != _wDetailDensity) _detailDensity = _terrain.detailObjectDensity;
            // A 0 written meanwhile reads as this hand-off's own 0 and is missed; harmless while a
            // preset decides the distance, as every one in the project does.
            if (_terrain.detailObjectDistance != 0f) _detailDistance = _terrain.detailObjectDistance;
            if (_terrain.treeDistance != _wTreeDistance) _treeDistance = _terrain.treeDistance;
            if (_terrain.treeBillboardDistance != _wBillboardStart) _billboardStart = _terrain.treeBillboardDistance;
            if (_terrain.treeCrossFadeLength != _wFadeLength) _fadeLength = _terrain.treeCrossFadeLength;
            if (_terrain.treeMaximumFullLODCount != _wMaxFullTrees) _maxFullTrees = _terrain.treeMaximumFullLODCount;
        }

        /// <summary>
        /// Writes what the preset gives each setting, only where it differs (a terrain setter may
        /// rebuild what it draws), and notes what the terrain then reads back.
        /// </summary>
        private void Mirror()
        {
            if (!_terrain.ignoreQualitySettings) _terrain.ignoreQualitySettings = true;

            float value = Pick(TerrainQualityOverrides.PixelError, QualitySettings.terrainPixelError, _pixelError);
            if (_terrain.heightmapPixelError != value) _terrain.heightmapPixelError = value;
            _wPixelError = _terrain.heightmapPixelError;

            value = Pick(TerrainQualityOverrides.BasemapDistance, QualitySettings.terrainBasemapDistance, _basemapDistance);
            if (_terrain.basemapDistance != value) _terrain.basemapDistance = value;
            _wBasemapDistance = _terrain.basemapDistance;

            value = Density;
            if (_terrain.detailObjectDensity != value) _terrain.detailObjectDensity = value;
            _wDetailDensity = _terrain.detailObjectDensity;

            value = Pick(TerrainQualityOverrides.TreeDistance, QualitySettings.terrainTreeDistance, _treeDistance);
            if (_terrain.treeDistance != value) _terrain.treeDistance = value;
            _wTreeDistance = _terrain.treeDistance;

            value = Pick(TerrainQualityOverrides.BillboardStart, QualitySettings.terrainBillboardStart, _billboardStart);
            if (_terrain.treeBillboardDistance != value) _terrain.treeBillboardDistance = value;
            _wBillboardStart = _terrain.treeBillboardDistance;

            value = Pick(TerrainQualityOverrides.FadeLength, QualitySettings.terrainFadeLength, _fadeLength);
            if (_terrain.treeCrossFadeLength != value) _terrain.treeCrossFadeLength = value;
            _wFadeLength = _terrain.treeCrossFadeLength;

            int maxTrees = (int)Pick(TerrainQualityOverrides.MaxTrees, QualitySettings.terrainMaxTrees, _maxFullTrees);
            if (_terrain.treeMaximumFullLODCount != maxTrees) _terrain.treeMaximumFullLODCount = maxTrees;
            _wMaxFullTrees = _terrain.treeMaximumFullLODCount;

            if (_terrain.detailObjectDistance != 0f) _terrain.detailObjectDistance = 0f;
        }
    }
}
