using System.Runtime.CompilerServices;

// The EditMode suite drives the client's own models directly. NetClientVehicle and
// ClientTurretDirectory are internal and stay internal: both are collaborators of the vehicle
// stage rather than public surface, and phase C4c deliberately did NOT widen either to let the
// lane-B harness read a vehicle pose — RemoteVehicleRegistry.TryGetPose is the narrow public read
// that need actually wanted. Same shape, and same reasoning, as the server assembly's own
// AssemblyInfo.
[assembly: InternalsVisibleTo("Ironfront.Net.Unity.Client.Tests")]
