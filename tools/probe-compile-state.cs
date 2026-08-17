// Dump all three console errors verbatim, and check the Editor assemblies too — the flag
// EditorUtility.scriptCompilationFailed covers editor-only assemblies, which
// GetAssemblies(PlayerWithoutTestAssemblies) does not list at all. All 40 player assemblies have
// output on disk, so whatever failed is not one of those.
Type le = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
Type entryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry");
var start = le.GetMethod("StartGettingEntries",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
var end = le.GetMethod("EndGettingEntries",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
var getEntry = le.GetMethod("GetEntryInternal",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

int n = (int)start.Invoke(null, null);
object entry = Activator.CreateInstance(entryType);
var fMsg = entryType.GetField("message",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
Out("=== all " + n + " console entries, verbatim ===");
for (int i = 0; i < n; i++)
{
    getEntry.Invoke(null, new object[] { i, entry });
    Out("--- entry " + i + " ---");
    Out((string)fMsg.GetValue(entry));
}
end.Invoke(null, null);

Out("");
Out("=== editor assemblies ===");
var ed = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(
    UnityEditor.Compilation.AssembliesType.Editor);
Out("count=" + ed.Length);
foreach (var asm in ed)
{
    string p = Path.GetFullPath(asm.outputPath);
    if (!File.Exists(p)) Out("  MISSING OUTPUT: " + asm.name + " -> " + asm.outputPath);
}

Out("");
Out("=== ScriptAssemblies on disk vs expected ===");
string sa = Path.GetFullPath("Library/ScriptAssemblies");
Out("dir=" + sa + " exists=" + Directory.Exists(sa));
if (Directory.Exists(sa))
{
    var dlls = Directory.GetFiles(sa, "*.dll");
    Out("dlls=" + dlls.Length);
    foreach (string d in dlls)
    {
        string nm = Path.GetFileNameWithoutExtension(d);
        if (nm.StartsWith("Assembly-CSharp", StringComparison.Ordinal)
            || nm.IndexOf("Ironfront", StringComparison.OrdinalIgnoreCase) >= 0
            || nm.IndexOf("Mcp", StringComparison.OrdinalIgnoreCase) >= 0)
            Out("  " + nm + " (" + new FileInfo(d).Length + " b)");
    }
}
