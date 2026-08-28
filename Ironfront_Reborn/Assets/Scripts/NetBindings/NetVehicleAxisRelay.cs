using UnityEngine;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// Carries the vehicle axes the server accepted for one body, for a driver whose controller
    /// has no <c>IInputSource</c> to install onto. Ledger <b>X-46</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case this exists for is the ordinary one, not the exotic one.</b>
    /// <c>IronfrontNetBindings.CreatePlayerBody</c> instantiates <c>ActorManager.actorPrefab</c> —
    /// the bot character — so every networked player's server-side body carries an
    /// <c>AiActorController</c> and never an <c>FpsActorController</c>.
    /// <see cref="NetDriverInputSink.Attach"/> therefore returned null for every real driver, the
    /// bridge counted an unreachable controller, and the authority went on accepting
    /// <c>C_VEHICLE_INPUT</c> with nothing on the other end of it. Measured: 1,285 accepted
    /// vehicle inputs against a hull that never moved
    /// (<c>artifacts/lane-a/r5/r5-combat-05</c>).
    /// </para>
    /// <para>
    /// <b>Not an <c>ActorController</c>, and not an <c>IInputSource</c> either (O-D1).</b> A
    /// second <c>ActorController</c> on the body would make <c>GetComponent&lt;ActorController&gt;()</c>
    /// order-dependent and would trip <c>Actor.aiControlled</c>, which is frozen in <c>Awake</c>
    /// from an exact type comparison and then read by UI, LOD and weapon culling — the field
    /// V5-D7 exists to keep still. An <c>IInputSource</c> would not help either: the thing that
    /// reads a source is <c>FpsActorController</c>, which is what this body does not have. So the
    /// relay is a plain value the vehicle-input overrides read, and nothing about the controller
    /// hierarchy moves.
    /// </para>
    /// <para>
    /// <b>Read only by a SUSPENDED controller (O-D2).</b> <c>NetServerActor.Claim</c> suspends the
    /// bot brain through <c>IAiDriver.Suspend</c>, which sets <c>enabled = false</c>, so
    /// <c>!enabled</c> names exactly "this controller is not steering this body" — the same
    /// condition X-45 and X-47 established. A bot's controller is enabled and never reaches this,
    /// so an AI convoy keeps driving itself.
    /// </para>
    /// <para>
    /// <b>Both readings of the four axis slots are held at once</b>, for
    /// <c>ServerVehicleInputBridge</c>'s reason: a car never reads the helicopter members and a
    /// helicopter never reads the car ones, so the vehicle picks and nothing here has to know the
    /// kind or get it wrong.
    /// </para>
    /// </remarks>
    internal sealed class NetVehicleAxisRelay : MonoBehaviour
    {
        /// <summary>Steer on x, throttle on y. What <c>CarInput</c> and <c>BoatInput</c> return.</summary>
        internal Vector2 CarAxes { get; private set; }

        /// <summary>Yaw, collective, roll, pitch. What <c>HelicopterInput</c> returns.</summary>
        internal Vector4 HelicopterAxes { get; private set; }

        /// <summary>
        /// True while a driver is holding this body's seat, so a stale relay left on a body that
        /// has since got out cannot keep steering.
        /// </summary>
        /// <remarks>
        /// The component is not destroyed on seat exit — the same body enters and leaves seats all
        /// match, and destroying it per exit would allocate one per entry on a path the bridge
        /// already keeps a dictionary to avoid churning. So the flag, not the component's
        /// lifetime, is what says whether the axes mean anything.
        /// </remarks>
        internal bool Driving { get; private set; }

        /// <summary>
        /// The relay on <paramref name="gameObject"/>, added if it has none.
        /// </summary>
        internal static NetVehicleAxisRelay Install(GameObject gameObject)
        {
            if (gameObject == null) return null;

            NetVehicleAxisRelay relay = gameObject.GetComponent<NetVehicleAxisRelay>();
            return relay != null ? relay : gameObject.AddComponent<NetVehicleAxisRelay>();
        }

        /// <summary>
        /// The car axes a suspended controller on <paramref name="behaviour"/> should return, or
        /// <see cref="Vector2.zero"/> when nothing is driving it over the network.
        /// </summary>
        /// <remarks>
        /// A static lookup rather than a field on the controller, so <c>AiActorController</c>
        /// gains one line per override and no state. <c>GetComponent</c> on a body that has no
        /// relay is the bot case, and it returns the neutral stick X-47 already established.
        /// </remarks>
        internal static Vector2 CarAxesFor(MonoBehaviour behaviour)
        {
            NetVehicleAxisRelay relay = Find(behaviour);
            return relay != null ? relay.CarAxes : Vector2.zero;
        }

        /// <summary>The helicopter stick a suspended controller should return.</summary>
        internal static Vector4 HelicopterAxesFor(MonoBehaviour behaviour)
        {
            NetVehicleAxisRelay relay = Find(behaviour);
            return relay != null ? relay.HelicopterAxes : Vector4.zero;
        }

        /// <summary>Publishes one tick's accepted axes.</summary>
        internal void SetAxes(Vector2 car, Vector4 helicopter)
        {
            CarAxes = car;
            HelicopterAxes = helicopter;
            Driving = true;
        }

        /// <summary>
        /// Centres the stick and stops claiming a driver. Seat exit, death, disconnect, and every
        /// tick the authority's hold window has expired.
        /// </summary>
        internal void Centre()
        {
            CarAxes = Vector2.zero;
            HelicopterAxes = Vector4.zero;
            Driving = false;
        }

        private static NetVehicleAxisRelay Find(MonoBehaviour behaviour)
        {
            if (behaviour == null) return null;

            NetVehicleAxisRelay relay = behaviour.GetComponent<NetVehicleAxisRelay>();
            return relay != null && relay.Driving ? relay : null;
        }
    }
}
