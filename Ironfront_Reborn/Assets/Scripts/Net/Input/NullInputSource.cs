namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Presses nothing, ever. For a dead actor, an actor whose input is disabled, and for the
    /// dedicated server, which has no keyboard.
    /// </summary>
    /// <remarks>
    /// A null object rather than a null reference: every
    /// <c>ActorController</c> read is on a per-frame path, so a source that can be null turns
    /// one missed assignment into a <c>NullReferenceException</c> every frame for the rest of
    /// the session. Nothing pressed is always a safe answer.
    /// </remarks>
    public sealed class NullInputSource : IInputSource
    {
        /// <summary>Shared; the type is stateless, so one instance is enough.</summary>
        public static readonly NullInputSource Instance = new NullInputSource();

        public float MoveX => 0f;
        public float MoveZ => 0f;
        public float Yaw => 0f;
        public float Pitch => 0f;
        public float Lean => 0f;
        public float LookDeltaX => 0f;
        public float LookDeltaY => 0f;
        public ushort Buttons => 0;
        public float HeliYaw => 0f;
        public float HeliCollective => 0f;
        public float HeliRoll => 0f;
        public float HeliPitch => 0f;
    }
}
