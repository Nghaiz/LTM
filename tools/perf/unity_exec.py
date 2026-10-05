#!/usr/bin/env python3
"""Run a static method of a C# file inside the open Unity Editor and print what it returns.

    python tools/perf/unity_exec.py <file.cs> <ClassName> <MethodName>

Goes through tools/mcp-call.py (Unity MCP's script-execute), so it works from any shell while
the Editor and its MCP server are up -- including a Claude session whose own MCP binding died.
The method must be static, take no arguments and return a string.
"""
import json
import pathlib
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__)
        return 2
    source, cls, method = sys.argv[1:]
    args = {"className": cls, "methodName": method, "csharpCode": pathlib.Path(source).read_text(encoding="utf-8")}
    run = subprocess.run(
        [sys.executable, str(REPO / "tools" / "mcp-call.py"), "call", "script-execute", "-"],
        input=json.dumps(args), capture_output=True, text=True, encoding="utf-8",
    )
    text = run.stdout.strip()
    try:
        reply = json.loads(text)
    except json.JSONDecodeError:
        print(text or run.stderr)
        return run.returncode or 1
    value = reply
    for key in ("result", "value"):
        value = value.get(key, value) if isinstance(value, dict) else value
    print(value if isinstance(value, str) else json.dumps(value, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
