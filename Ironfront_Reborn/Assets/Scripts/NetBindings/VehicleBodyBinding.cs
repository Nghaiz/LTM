using Ironfront.Net.Unity.Client;
using UnityEngine;

/// <summary>
/// The <c>Assembly-CSharp</c> half of <see cref="IGameplayVehicleBody"/>. Phase C4b.
/// </summary>
/// <remarks>
/// A partial on the component rather than an adapter class, for the reason
/// <c>ActorPresenceBinding</c> gives at length: the netcode holds one of these per replicated
/// vehicle for the vehicle's whole life, and an adapter would be a second object to keep alive
/// and a second thing that can outlive its subject. <c>SetNetworkDriven</c> and <c>Die</c> are
/// pre-existing public methods and satisfy the interface unwritten.
/// </remarks>
public partial class Vehicle
{
    /// <inheritdoc/>
    /// <remarks>See <c>IGameplayActorPresence.Exists</c> — this is Unity's overloaded equality,
    /// which an interface reference held by the netcode cannot perform for itself.</remarks>
    public bool Exists => this != null;

    /// <inheritdoc/>
    public GameObject GameObject => gameObject;

    /// <inheritdoc/>
    public Transform Transform => transform;

    /// <inheritdoc/>
    /// <remarks>
    /// <c>Vehicle.rigidbody</c> is the game's own cached field, not the deprecated
    /// <c>Component.rigidbody</c>. Forwarding it rather than calling <c>GetComponent</c> keeps
    /// the client's correction path reading exactly the body the offline game drives.
    /// </remarks>
    Rigidbody IGameplayVehicleBody.Rigidbody => rigidbody;
}
