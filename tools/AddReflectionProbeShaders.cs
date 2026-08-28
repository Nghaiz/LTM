// Appends Unity's reflection-probe convolution shaders to GraphicsSettings'
// m_AlwaysIncludedShaders. Ledger X-51. Idempotent — safe to re-run.
//
// WHY A SCRIPT AND NOT A HAND-EDIT. Hidden/CubeCopy, Hidden/CubeBlur and Hidden/CubeBlend are
// built-in shaders, so their entries in GraphicsSettings.asset are fileIDs into Unity's own
// resource bundles. Hand-writing those numbers is how a settings file ends up holding a
// confident reference to the WRONG shader: it serialises, it loads, nothing errors, and the
// only symptom is the one we already have. Shader.Find resolves them by name and lets Unity
// write its own fileID.
//
// NAMED IN PascalCase unlike its kebab-case siblings in this directory (probe-defines.cs,
// unity-lod-recon.cs). The repo convention here is kebab; the authoring gate requires the C#
// convention. Following the gate rather than the neighbours, and saying so.
//
// Copy to Assets/Editor/ and run:
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod AddReflectionProbeShaders.Run
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class AddReflectionProbeShaders
{
    private static readonly string[] Required =
    {
        "Hidden/CubeCopy",
        "Hidden/CubeBlur",
        "Hidden/CubeBlend",
    };

    public static void Run()
    {
        var log = new StringBuilder();

        // GraphicsSettings.asset lives under ProjectSettings/, not Assets/, so the ordinary
        // LoadAssetAtPath<T> does not reach it. LoadAllAssetsAtPath does.
        Object[] assets =
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");

        GraphicsSettings settings = null;
        if (assets != null)
        {
            foreach (Object candidate in assets)
            {
                settings = candidate as GraphicsSettings;
                if (settings != null) break;
            }
        }

        if (settings == null)
        {
            log.AppendLine("FAILED: could not load GraphicsSettings.asset");
            Finish(log, 1);
            return;
        }

        var so = new SerializedObject(settings);
        SerializedProperty list = so.FindProperty("m_AlwaysIncludedShaders");

        if (list == null)
        {
            log.AppendLine("FAILED: m_AlwaysIncludedShaders not found on GraphicsSettings");
            Finish(log, 1);
            return;
        }

        var present = new HashSet<string>();
        for (int i = 0; i < list.arraySize; i++)
        {
            var shader = list.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
            if (shader != null) present.Add(shader.name);
        }

        log.AppendLine("before: " + list.arraySize + " always-included shader(s)");

        int added = 0;
        int unresolved = 0;
        foreach (string name in Required)
        {
            if (present.Contains(name))
            {
                log.AppendLine("already present: " + name);
                continue;
            }

            Shader shader = Shader.Find(name);
            if (shader == null)
            {
                // Reported rather than skipped silently: a name that does not resolve means the
                // shader was renamed by an engine version, and the premise needs re-reading.
                log.AppendLine("NOT FOUND by Shader.Find: " + name);
                unresolved++;
                continue;
            }

            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            added++;
            log.AppendLine("added: " + name);
        }

        if (added > 0)
        {
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        log.AppendLine("after: " + list.arraySize + " always-included shader(s), added "
                       + added + ", unresolved " + unresolved);
        Finish(log, unresolved > 0 ? 1 : 0);
    }

    private static void Finish(StringBuilder log, int code)
    {
        System.IO.File.WriteAllText("add-reflection-probe-shaders.txt", log.ToString());
        Debug.Log(log.ToString());
        EditorApplication.Exit(code);
    }
}
