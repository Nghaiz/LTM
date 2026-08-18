namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Where the server puts a driver's decoded vehicle axes so the vehicle can read them.
    /// V5 task 5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A seam rather than a direct call, for the same reason every other one here is one.</b>
    /// The controller, the input source and the vehicles all live in <c>Assembly-CSharp</c>, and
    /// a predefined assembly cannot be referenced from an <c>.asmdef</c> — the reference only
    /// goes the other way. So this assembly declares what it needs and
    /// <c>IronfrontNetBindings</c>, which sits outside every asmdef, supplies it.
    /// </para>
    /// <para>
    /// <b>Attaching is the implementation's job, not the caller's.</b> Installing a network
    /// input source means calling <c>FpsActorController.SetInputSource</c> and remembering what
    /// was there before — types this assembly cannot name. The caller asks for a sink for an
    /// actor and gets one already installed, or null when that actor has no controller.
    /// </para>
    /// <para>
    /// <b>Both readings of the four wire axis slots are published at once, and that is not
    /// ambiguity.</b> <c>C_VEHICLE_INPUT</c> carries four generic slots whose meaning is per
    /// <c>VehicleKind</c>: a car fills the first two with throttle and steer, a helicopter fills
    /// all four with its control vector. A car never reads the helicopter axes and a helicopter
    /// never reads steer/throttle, so handing over both lets the vehicle pick and keeps the
    /// server from having to know the kind — or from getting it wrong.
    /// </para>
    /// </remarks>
    public interface IDriverInputSink
    {
        /// <summary>False once the actor or its controller has been destroyed.</summary>
        bool Exists { get; }

        /// <summary>
        /// Publishes the axes from the last accepted <c>C_VEHICLE_INPUT</c>.
        /// </summary>
        /// <param name="steer">Wire slot 2. What a ground vehicle steers with.</param>
        /// <param name="throttle">Wire slot 1. What a ground vehicle accelerates with.</param>
        /// <param name="heliYaw">Wire slot 1, read as a helicopter's tail rotor.</param>
        /// <param name="heliCollective">Wire slot 2, read as lift.</param>
        /// <param name="heliRoll">Wire slot 3, read as bank.</param>
        /// <param name="heliPitch">Wire slot 4, read as nose pitch.</param>
        void SetAxes(
            float steer, float throttle,
            float heliYaw, float heliCollective, float heliRoll, float heliPitch);

        /// <summary>
        /// Centres every axis, leaving the sink installed.
        /// </summary>
        /// <remarks>
        /// What the hold window expiring looks like (V5-D11): a driver whose connection stalls
        /// coasts to a stop rather than holding full throttle into the sea.
        /// </remarks>
        void Centre();

        /// <summary>Puts back whatever input source the controller held before. Seat exit.</summary>
        void Detach();
    }
}
