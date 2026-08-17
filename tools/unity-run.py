#!/usr/bin/env python3
"""Run a C# snippet inside the open Unity Editor through the MCP script-execute tool.

The snippet is a method body. Call Out("...") in it to report a line; every line lands in
Ironfront_Reborn/harness-out.txt and is printed here. Results travel through a file rather than
the tool's return value because script-execute hands back only a status string, and a report of
any size would be truncated inside it.

Usage:
    python tools/unity-run.py body.cs
    python tools/unity-run.py -            # body on stdin
"""
import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "Ironfront_Reborn" / "harness-out.txt"
MCP = ROOT / "tools" / "mcp-call.py"

TEMPLATE = """using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEditor;
using Ironfront.Editor.Verification;

public class HarnessRun
{
    static readonly StringBuilder _out = new StringBuilder();

    static void Out(string line) { _out.AppendLine(line); }

    public static void Main()
    {
        try
        {
__BODY__
        }
        catch (Exception ex)
        {
            Out("EXCEPTION " + ex.ToString());
        }

        File.WriteAllText(@"__OUT__", _out.ToString());
    }
}
"""


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)

    source = sys.argv[1]
    body = sys.stdin.read() if source == "-" else open(source, encoding="utf-8").read()

    code = TEMPLATE.replace("__BODY__", body).replace("__OUT__", str(OUT))
    payload = json.dumps({
        "csharpCode": code,
        "className": "HarnessRun",
        "methodName": "Main",
    })

    if OUT.exists():
        OUT.unlink()

    proc = subprocess.run(
        [sys.executable, str(MCP), "call", "script-execute", "-"],
        input=payload, capture_output=True, text=True, encoding="utf-8", errors="replace")

    status = (proc.stdout or "").strip()

    # The result file, not the tool's reply, is the source of truth. Roslyn compilation of the
    # snippet regularly outruns the plugin's 10 s request timeout, so the call reports a retry
    # failure while the code it dispatched has already run to completion.
    if OUT.exists():
        print(OUT.read_text(encoding="utf-8", errors="replace"))
        return 0

    print("no output file written; tool said:")
    print(status[:2000])
    print((proc.stderr or "")[:1000])
    return 3


if __name__ == "__main__":
    sys.exit(main())
