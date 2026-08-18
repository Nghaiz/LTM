using System.Runtime.CompilerServices;

// The EditMode suite tests the spawn-point sampling directly. ServerCombatBridge is internal
// and stays internal: it is a collaborator of the tick loop, not public surface.
[assembly: InternalsVisibleTo("Ironfront.Net.Unity.Server.Tests")]
