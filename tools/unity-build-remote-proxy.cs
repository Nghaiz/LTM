// Builds Assets/Prefab/Remote Actor Proxy.prefab — the visual-only stand-in RemoteActorRegistry
// pools for every actor this client does not control. Run once via MCP script-execute.
//
// Why a copy-and-strip rather than a fresh hierarchy: the SkinnedMeshRenderer under
// Actor Parent/character binds to bones in its own Armature subtree, and the Animator's avatar
// binds to the same transforms. Rebuilding that by hand is how you get a mesh that renders as a
// crumpled ball. Copying the asset and deleting what a remote proxy must not run keeps every one
// of those bindings, and keeps the local offsets that put the body at the right height.
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class BuildRemoteActorProxy
{
    private const string Source = "Assets/Prefab/Player Fps Actor.prefab";
    private const string Target = "Assets/Prefab/Remote Actor Proxy.prefab";

    public static void Run()
    {
        var log = new StringBuilder();

        if (AssetDatabase.LoadAssetAtPath<GameObject>(Target) != null)
        {
            AssetDatabase.DeleteAsset(Target);
            log.AppendLine("deleted existing " + Target);
        }

        if (!AssetDatabase.CopyAsset(Source, Target))
        {
            log.AppendLine("FAILED: CopyAsset");
            System.IO.File.WriteAllText("proxy-build.txt", log.ToString());
            return;
        }

        AssetDatabase.Refresh();

        GameObject root = PrefabUtility.LoadPrefabContents(Target);
        log.AppendLine("loaded contents: " + root.name);

        // --- children a remote proxy must not carry -------------------------------------------
        // FP Camera Parent  : a Camera + AudioListener per pooled instance. Sixteen AudioListeners
        //                     in one scene is a Unity warning per frame and undefined audio.
        // Bullet Flyby Sound: local-player feedback for rounds passing your own head.
        // character Ragdoll : the death visual, driven by ActiveRaggy which is being removed.
        // Third Person Camera: a second Camera, same reason as the first.
        // Head/Body Hitbox : each is a kinematic Rigidbody + collider on layer 8 carrying a
        //                     Hitbox whose `parent` field points at the Actor on Actor Parent.
        //                     Removing that Actor leaves `parent` dangling, and nothing guards it:
        //                     AiActorController.cs:950 reads `GetComponent<Hitbox>().parent.team`
        //                     inside a bot's per-frame fire decision, and Hitbox.ProjectileHit
        //                     calls `parent.Damage(...)`. Either one throws the moment a bot looks
        //                     at a proxy or a local projectile touches one. Rewiring instead of
        //                     deleting would mean putting an Actor back on the proxy, which is the
        //                     one thing a proxy must not have — and it would be a client computing
        //                     damage, which is the server's job (LagCompensator rewinds its own
        //                     hitbox ring against the real NetServerActor prefabs, not these).
        foreach (string path in new[]
                 {
                     "FP Camera Parent",
                     "Bullet Flyby Sound",
                     "Actor Parent/character Ragdoll",
                     "Actor Parent/Third Person Camera",
                     "Actor Parent/character/Head Hitbox",
                     "Actor Parent/character/Body Hitbox",
                 })
        {
            Transform t = root.transform.Find(path);
            if (t == null) { log.AppendLine("  child MISSING (skipped): " + path); continue; }
            Object.DestroyImmediate(t.gameObject);
            log.AppendLine("  removed child: " + path);
        }

        // --- components ------------------------------------------------------------------------
        // MonoBehaviours first, then the built-ins they may RequireComponent, so a dependency
        // never blocks its own dependant's removal.
        StripComponents(root.transform, log, "root", new[]
        {
            // Netcode: the local player's prediction and the server's authority. A pooled remote
            // proxy running either would simulate an actor the server owns.
            "ClientPredictionStage", "NetPredictionClock", "NetServerActor", "NetMovementAgent",
            // Gameplay controllers and pathfinding.
            "FpsActorController", "FirstPersonController", "Seeker",
            // Audio, then physics. The Rigidbody matters most: RemoteActorRegistry assigns
            // transform.position directly, and a non-kinematic body would answer with gravity —
            // the proxy would fall through the world between snapshots.
            "AudioSource", "CharacterController", "Rigidbody",
        });

        Transform actorParent = root.transform.Find("Actor Parent");
        if (actorParent != null)
        {
            StripComponents(actorParent, log, "Actor Parent", new[]
            {
                // ActiveRaggy drives the ragdoll that was just deleted. Actor is the gameplay
                // object — health, weapons, dying. Nothing on the client reads either for a
                // remote actor: RemoteActorRegistry.Update sets position and rotation, full stop.
                "ActiveRaggy", "Actor", "Rigidbody",
            });
        }

        Transform character = root.transform.Find("Actor Parent/character");
        if (character != null)
        {
            // ActorIk aims the upper body at ActorIk.aimPoint. Nothing sets aimPoint on a proxy,
            // so it would aim at the world origin the moment weight went above zero.
            StripComponents(character, log, "character", new[] { "ActorIk" });
        }
        else
        {
            log.AppendLine("  WARNING: Actor Parent/character not found — the mesh may be gone");
        }

        // --- verify before saving --------------------------------------------------------------
        var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var anims = root.GetComponentsInChildren<Animator>(true);
        var cams = root.GetComponentsInChildren<Camera>(true);
        var listeners = root.GetComponentsInChildren<AudioListener>(true);
        var bodies = root.GetComponentsInChildren<Rigidbody>(true);
        var colliders = root.GetComponentsInChildren<Collider>(true);

        // Every MonoBehaviour left anywhere in the tree, by name. A proxy should have none: the
        // registry writes position and rotation and nothing else runs. Listing them rather than
        // just counting means a component added to the source prefab later shows up here instead
        // of silently riding along into every pooled remote actor.
        var scripts = new List<string>();
        int missing = 0;
        foreach (MonoBehaviour m in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (m == null) { missing++; continue; }
            scripts.Add(m.GetType().Name);
        }

        log.AppendLine("VERIFY skinnedMesh=" + smrs.Length + " animator=" + anims.Length
                       + " camera=" + cams.Length + " audioListener=" + listeners.Length
                       + " rigidbody=" + bodies.Length + " collider=" + colliders.Length
                       + " monoBehaviours=" + scripts.Count + " missingScripts=" + missing);
        log.AppendLine("VERIFY remainingScripts=[" + string.Join(", ", scripts) + "]");

        log.AppendLine("REMAINING root components:");
        foreach (Component c in root.GetComponents<Component>())
            log.AppendLine("  " + (c == null ? "<<MISSING>>" : c.GetType().Name));

        PrefabUtility.SaveAsPrefabAsset(root, Target, out bool saved);
        PrefabUtility.UnloadPrefabContents(root);
        log.AppendLine("saved=" + saved + " -> " + Target);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        System.IO.File.WriteAllText("proxy-build.txt", log.ToString());
        Debug.Log("[proxy] build finished, see proxy-build.txt");
    }

    /// <summary>
    /// Destroys the named component types on one transform, in the order given.
    /// </summary>
    /// <remarks>
    /// Matched by type name rather than by <c>GetComponent&lt;T&gt;()</c> so that one list literal
    /// drives the whole strip and a type that disappears from the source prefab logs "not present
    /// (skipped)" instead of failing to compile. Naming the types directly would also work — the
    /// MCP Roslyn host does reference <c>Assembly-CSharp</c> — but then every entry needs its own
    /// statement and its own null check.
    /// </remarks>
    private static void StripComponents(Transform t, StringBuilder log, string label, string[] typeNames)
    {
        foreach (string typeName in typeNames)
        {
            bool found = false;
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != typeName) continue;
                Object.DestroyImmediate(c);
                log.AppendLine("  removed " + label + "." + typeName);
                found = true;
                break;
            }
            if (!found) log.AppendLine("  " + label + "." + typeName + " not present (skipped)");
        }
    }
}
