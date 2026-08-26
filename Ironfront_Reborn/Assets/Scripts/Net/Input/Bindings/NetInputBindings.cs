namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Where <c>Assembly-CSharp</c> hands this assembly the client state it may not name.
    /// The <c>Net/Input</c> counterpart of <c>NetSceneBindings</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A static registry rather than constructor injection because the consumer is
    /// <see cref="LocalInputSource"/>, which <c>FpsActorController.Awake</c> builds with a camera
    /// transform and an aiming delegate and nothing else. Threading an environment through that
    /// call would put a second thing the controller has to know about into a seam whose entire
    /// point is that the controller knows nothing.
    /// </para>
    /// <para>
    /// <c>Clear</c> exists for the same reason <c>NetSceneBindings.Clear</c> does: with domain
    /// reload disabled a static set in one Play session survives into the next, and a stale
    /// binding pointing at a destroyed scene object is worse than no binding at all.
    /// </para>
    /// </remarks>
    public static class NetInputBindings
    {
        private static ILocalInputEnvironment _environment;

        /// <summary>
        /// The client UI and preference state local input reads. Never null: an unset binding
        /// yields <c>NullLocalInputEnvironment</c>, which reports the fault once and then reads
        /// as a closed loadout screen and zeroed helicopter options.
        /// </summary>
        public static ILocalInputEnvironment Environment
        {
            get => _environment ?? NullLocalInputEnvironment.Instance;
            set => _environment = value;
        }

        public static void Clear()
        {
            _environment = null;
        }
    }
}
