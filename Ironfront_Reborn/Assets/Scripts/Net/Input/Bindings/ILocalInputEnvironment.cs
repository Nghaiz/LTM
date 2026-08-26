// NO `using UnityEngine;` IN THIS FILE, and that is a constraint rather than an accident.
// Ironfront.Client.Input.Tests compiles Assets/Scripts/Net/Input sources a second time under
// `dotnet test`, and its one rule for a linked file is that it must not touch UnityEngine —
// see that .csproj's header. The interface, the enum and the options struct are the half worth
// testing (the enum is pinned to shipped PlayerPrefs values), so they stay linkable. The null
// object needs Debug.LogError and therefore lives in NullLocalInputEnvironment.cs. Do not merge
// the two files back together.

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Which of the three helicopter control schemes the player has chosen. Values are pinned to
    /// the legacy <c>OptionsUi.Options.HELICOPTER_TYPE_*</c> constants, which are what
    /// <c>PlayerPrefs</c> already holds on every existing install.
    /// </summary>
    /// <remarks>
    /// The pinning is load-bearing rather than cosmetic: the stored preference is an
    /// <c>int</c> written by a shipped build, so a renumbering here silently changes which
    /// scheme an existing player gets. Nothing casts between the two — the legacy side maps
    /// explicitly — but the numbers agree so that a future reader comparing them is not
    /// left wondering which one moved.
    /// </remarks>
    public enum HelicopterControlStyle
    {
        Battlefield = 0,
        Arma        = 1,
        Custom      = 2,
    }

    /// <summary>
    /// The per-user helicopter control preferences <see cref="LocalInputSource"/> reads: two
    /// sensitivities, the scheme, and the four invert flags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A snapshot, taken per read, rather than a live handle on the options object. The values
    /// are read together and used together within one frame's <c>HelicopterControls</c>
    /// evaluation, so a struct that cannot change halfway through is the honest shape.
    /// </para>
    /// <para>
    /// <b>These are the client's business and only the client's.</b> The dedicated server has no
    /// <c>PlayerPrefs</c> and no user whose sensitivity it could mean, which is why
    /// <c>FpsActorController</c> refuses to build a <see cref="LocalInputSource"/> at server role
    /// at all (V5-D9) rather than binding this to something neutral there.
    /// </para>
    /// </remarks>
    public readonly struct HelicopterControlOptions
    {
        public readonly float MouseSensitivity;
        public readonly float HelicopterSensitivity;
        public readonly HelicopterControlStyle Style;
        public readonly bool InvertPitch;
        public readonly bool InvertYaw;
        public readonly bool InvertRoll;
        public readonly bool InvertThrottle;

        public HelicopterControlOptions(
            float mouseSensitivity,
            float helicopterSensitivity,
            HelicopterControlStyle style,
            bool invertPitch,
            bool invertYaw,
            bool invertRoll,
            bool invertThrottle)
        {
            MouseSensitivity      = mouseSensitivity;
            HelicopterSensitivity = helicopterSensitivity;
            Style                 = style;
            InvertPitch           = invertPitch;
            InvertYaw             = invertYaw;
            InvertRoll            = invertRoll;
            InvertThrottle        = invertThrottle;
        }
    }

    /// <summary>
    /// The client-side UI and preference state that shapes local input: whether the loadout
    /// screen is suppressing gameplay buttons, and the helicopter control preferences.
    /// Implemented in <c>Assembly-CSharp</c>, named nowhere in this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why one interface and not two.</b> The enumeration this seam exists to cover is
    /// exactly two legacy types — <c>LoadoutUi</c> and <c>OptionsUi</c> — and both are static
    /// singletons read by the same object, <see cref="LocalInputSource"/>, on the same frame,
    /// for the same purpose: turning raw device state into the input this player meant. They
    /// are one context, so they are one binding. Splitting them would give the seam a second
    /// registration to keep alive for no reader's benefit, which is the failure phase C2's
    /// § 3.2 names.
    /// </para>
    /// <para>
    /// <b>The direction is the same as <c>Net/Server/Bindings/</c>.</b> The sealed side declares
    /// the interface; a legacy type implements it; the sealed side never names the legacy type.
    /// The difference from <c>ICapturePointDirectory</c> is only that this one is read rather
    /// than driven — the shape of the seam is the shape that assembly boundaries permit, and it
    /// does not vary with which way the data happens to flow.
    /// </para>
    /// </remarks>
    public interface ILocalInputEnvironment
    {
        /// <summary>
        /// Whether the loadout screen is showing, and so whether fire, aim and reload are
        /// suppressed this frame. False where there is no loadout screen.
        /// </summary>
        bool LoadoutScreenOpen { get; }

        /// <summary>This player's helicopter control preferences, read fresh.</summary>
        HelicopterControlOptions HelicopterOptions { get; }
    }
}
