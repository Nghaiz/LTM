using System.Collections.Generic;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>What one rifle scope can do: its magnifications, its zero, a rangefinder, and how much it sways.</summary>
    public sealed class ScopeProfile
    {
        public ScopeProfile(
            float[] magnifications, int zeroMinMetres, int zeroMaxMetres, int zeroStepMetres,
            bool rangefinder, float swayDegrees, float aimMarkBelowCentre = 0f)
        {
            Magnifications = magnifications;
            ZeroMinMetres = zeroMinMetres;
            ZeroMaxMetres = zeroMaxMetres;
            ZeroStepMetres = zeroStepMetres;
            HasRangefinder = rangefinder;
            SwayDegrees = swayDegrees;
            AimMarkBelowCentre = aimMarkBelowCentre;
        }

        /// <summary>The scope's settings, lowest first; the wheel steps through them.</summary>
        public IReadOnlyList<float> Magnifications { get; }

        /// <summary>The zero it starts on, metres: the round crosses the line of sight there.</summary>
        public int ZeroMinMetres { get; }

        public int ZeroMaxMetres { get; }

        public int ZeroStepMetres { get; }

        /// <summary>Whether the scope reads the distance to what the crosshair rests on.</summary>
        public bool HasRangefinder { get; }

        /// <summary>How far the aim wanders while breathing, degrees (it scales with nothing: the rifle moves, not the picture).</summary>
        public float SwayDegrees { get; }

        /// <summary>
        /// How far below the overlay's centre its reticle draws the aim mark, as a share of the
        /// screen's height: the overlay is raised by that much so the mark sits where the round
        /// goes. The RECON LRR's chevron apex was measured 21 px under the centre at 1080 p.
        /// </summary>
        public float AimMarkBelowCentre { get; }

        public bool VariableZoom => Magnifications.Count > 1;

        public bool AdjustableZero => ZeroMaxMetres > ZeroMinMetres;

        /// <summary>The magnification at <paramref name="index"/>, clamped into the list.</summary>
        public float MagnificationAt(int index) => Magnifications[Mathf.Clamp(index, 0, Magnifications.Count - 1)];

        /// <summary>A zero moved by <paramref name="steps"/> clicks and kept in range.</summary>
        public int StepZero(int zeroMetres, int steps)
            => Mathf.Clamp(zeroMetres + steps * ZeroStepMetres, ZeroMinMetres, ZeroMaxMetres);
    }

    /// <summary>
    /// The rifle scopes (owner's run of 2026-10-10, phase P38): "the three scoped guns feel the
    /// same -- one should zoom very far, for the long shots". Before this they were three fixed
    /// zooms within a step of each other (about 7x, 5.4x and 4x) with nothing to aim a long shot
    /// with but guesswork.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>SL-DEFENDER</b>, the bolt-action sniper (an AWM, .338-class): a variable 6-25x scope as
    /// on its real counterpart, a zero from 100 m to 1,000 m in 100 m clicks, and a rangefinder,
    /// so a 900 m shot is a reading and a click rather than a guess. <b>RECON LRR</b>, the
    /// semi-automatic long-range rifle (a Kel-Tec RFB, .308-class): a fixed 6x with its own BDC
    /// chevrons and a zero to 800 m. <b>SIGNAL DMR</b> keeps its 4x prism sight on the rifle, a
    /// model the camera looks through, and so has no profile here.
    /// </para>
    /// <para>
    /// <b>Sway, and holding the breath.</b> A scoped rifle drifts while its shooter breathes; the
    /// Sprint key held while scoped steadies it for a few seconds. The drift moves the real aim --
    /// the camera's parent, which is the aim the server is sent and the round flies along -- so
    /// what the crosshair rests on is still what the round hits.
    /// </para>
    /// <para>
    /// Magnification is relative to the player's own field of view (the options' FOV), as a real
    /// scope's is to the eye.
    /// </para>
    /// </remarks>
    public static class ScopeProfiles
    {
        public static readonly ScopeProfile SlDefender = new ScopeProfile(
            new[] { 6f, 12f, 25f }, zeroMinMetres: 100, zeroMaxMetres: 1000, zeroStepMetres: 100,
            rangefinder: true, swayDegrees: 0.09f);

        public static readonly ScopeProfile ReconLrr = new ScopeProfile(
            new[] { 6f }, zeroMinMetres: 100, zeroMaxMetres: 800, zeroStepMetres: 100,
            rangefinder: false, swayDegrees: 0.05f, aimMarkBelowCentre: 0.019f);

        /// <summary>The scope of the rifle with this network id, if it has one.</summary>
        public static bool TryGet(byte weaponId, out ScopeProfile profile)
        {
            switch (weaponId)
            {
                case WeaponIds.SL_DEFENDER: profile = SlDefender; return true;
                case WeaponIds.RECON_LRR: profile = ReconLrr; return true;
                default: profile = null; return false;
            }
        }

        /// <summary>
        /// The vertical field of view, degrees, that magnifies <paramref name="magnification"/> times
        /// over <paramref name="normalVerticalFov"/>.
        /// </summary>
        public static float VerticalFovFor(float magnification, float normalVerticalFov)
        {
            float half = Mathf.Tan(normalVerticalFov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, magnification);
            return 2f * Mathf.Atan(half) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// How much slower the mouse turns at <paramref name="magnification"/> than at the scope's
        /// lowest setting, so a 25x turn feels like a 6x one: the same hand movement moves the
        /// crosshair across the same share of the picture.
        /// </summary>
        public static float SensitivityScale(ScopeProfile profile, float magnification)
            => profile == null ? 1f : profile.MagnificationAt(0) / Mathf.Max(1f, magnification);
    }
}
