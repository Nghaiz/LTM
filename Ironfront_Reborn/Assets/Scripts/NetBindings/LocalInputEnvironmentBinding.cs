namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// The <c>Assembly-CSharp</c> half of <see cref="ILocalInputEnvironment"/>: reads
    /// <c>LoadoutUi</c> and <c>OptionsUi</c>, which the <c>Ironfront.Net.Unity.Input</c>
    /// assembly may not name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than beside the two UI classes because this is a seam implementation, and the
    /// seam implementations live together: <c>NetBindings/</c> is the one folder that sees both
    /// halves, which is the whole reason it is not inside any asmdef.
    /// </para>
    /// <para>
    /// <b>Every read is a live read.</b> Neither property caches. <c>OptionsUi.GetOptions()</c>
    /// returns the mutable options object the settings screen writes into, so a sensitivity
    /// changed mid-session takes effect on the next frame exactly as it did before this seam
    /// existed. A snapshot taken at install time would have quietly frozen the settings screen.
    /// </para>
    /// </remarks>
    internal sealed class LocalInputEnvironmentBinding : ILocalInputEnvironment
    {
        public bool LoadoutScreenOpen => LoadoutUi.IsOpen();

        public HelicopterControlOptions HelicopterOptions
        {
            get
            {
                OptionsUi.Options options = OptionsUi.GetOptions();
                return new HelicopterControlOptions(
                    options.mouseSensitivity,
                    options.helicopterSensitivity,
                    ToStyle(options.helicopterType),
                    options.heliInvertPitch,
                    options.heliInvertYaw,
                    options.heliInvertRoll,
                    options.heliInvertThrottle);
            }
        }

        /// <remarks>
        /// The default arm is <see cref="HelicopterControlStyle.Arma"/> and not a throw, because
        /// that is what the code this replaced did: <c>LocalInputSource</c> tested for CUSTOM,
        /// then for BATTLEFIELD, and fell through to the ARMA mapping for anything else —
        /// including a <c>PlayerPrefs</c> value written by a build that knew a fourth scheme.
        /// Preserving the fall-through is what makes this a move rather than a behaviour change.
        /// </remarks>
        private static HelicopterControlStyle ToStyle(int helicopterType)
        {
            switch (helicopterType)
            {
                case OptionsUi.Options.HELICOPTER_TYPE_CUSTOM:      return HelicopterControlStyle.Custom;
                case OptionsUi.Options.HELICOPTER_TYPE_BATTLEFIELD: return HelicopterControlStyle.Battlefield;
                default:                                            return HelicopterControlStyle.Arma;
            }
        }
    }
}
