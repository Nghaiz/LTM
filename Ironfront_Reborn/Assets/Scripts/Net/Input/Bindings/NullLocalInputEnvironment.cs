using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The environment used when nothing registered one. Reports the loadout screen closed —
    /// which is what the legacy <c>LoadoutUi.IsOpen()</c> returns when no loadout screen exists
    /// — and zeroed helicopter options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zeroed, not plausible, and it says so once.</b> The binding is installed
    /// unconditionally at <c>RuntimeInitializeLoadType.BeforeSceneLoad</c>, which runs before the
    /// first scene's <c>Awake</c> and so before any <see cref="LocalInputSource"/> can exist.
    /// Reaching this object therefore means the registration did not run, which is a bug and not
    /// a configuration. Guessing the shipped defaults here would hide it behind a helicopter that
    /// flies with the wrong feel — the exact failure nobody can attribute. Zero sensitivity
    /// gives a helicopter that does not respond at all, next to an error in the Console naming
    /// the cause.
    /// </para>
    /// </remarks>
    internal sealed class NullLocalInputEnvironment : ILocalInputEnvironment
    {
        internal static readonly NullLocalInputEnvironment Instance = new NullLocalInputEnvironment();

        private bool _warned;

        public bool LoadoutScreenOpen
        {
            get
            {
                Warn();
                return false;
            }
        }

        public HelicopterControlOptions HelicopterOptions
        {
            get
            {
                Warn();
                return default;
            }
        }

        private void Warn()
        {
            if (_warned) return;
            _warned = true;
            Debug.LogError(
                "[net-input] NetInputBindings.Environment was never set, so local input is " +
                "reading a null environment: the loadout screen always reads closed and every " +
                "helicopter control preference reads zero. The installer is " +
                "IronfrontNetBindings.Install, which runs at BeforeSceneLoad — if this fired, " +
                "it did not.");
        }
    }
}
