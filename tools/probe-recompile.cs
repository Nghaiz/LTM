// The build did not fail on a compile error. It failed on a stale flag: during the recompile that
// followed step A's target switch, Unity could not copy Library/ScriptAssemblies/
// UnityEditor.TestRunner.dll because another process held it, and it treats a failed script-assembly
// copy as a compilation failure. EditorUtility.scriptCompilationFailed stayed true, and
// BuildPipeline.BuildPlayer refuses with the generic "scripts have compile errors in the editor" --
// a message that names neither the file nor the fact that nothing failed to compile.
//
// The lock has since been released (verified from a shell: the file opens exclusively for
// ReadWrite). Forcing one more compilation retries the copy and clears the flag. This triggers a
// domain reload, so this snippet will not survive to write its own tail; check the flag with a
// separate call.
Out("before: isCompiling=" + EditorApplication.isCompiling
    + " scriptCompilationFailed=" + EditorUtility.scriptCompilationFailed);

UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
Out("RequestScriptCompilation() issued — expect a recompile and a domain reload");
