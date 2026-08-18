#!/usr/bin/env python3
"""Minimal MCP Streamable-HTTP client for the Unity gamedev-mcp-server.

The Unity Editor's MCP server (Library/mcp-server/win-x64/gamedev-mcp-server.exe) listens on
127.0.0.1:<port> with client-transport=streamableHttp and auth=none, but it is not registered as
an MCP server in this Claude Code session. This script speaks the transport directly so Editor
work can be driven from a shell.

Usage:
    python tools/mcp-call.py list
    python tools/mcp-call.py call <toolName> <args.json>
    python tools/mcp-call.py call <toolName> -            # args JSON on stdin

Session handling: MCP wants initialize -> notifications/initialized -> tools/*. The server issues
a Mcp-Session-Id we must echo on every later request, so each invocation re-initialises rather
than caching a session that may have expired between calls.
"""
import json
import os
import pathlib
import re
import sys
import urllib.request

_REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent


def _resolve_url():
    """The bridge URL, resolved from data rather than a hardcoded literal.

    The port moves whenever the Unity plugin re-allocates it, and a stale constant here fails
    as a connection refusal that looks like "the Editor is down" rather than "this file drifted".
    Order: T1K_UNITY_MCP_URL -> the repo's .mcp.json -> the plugin default.
    """
    override = os.environ.get("T1K_UNITY_MCP_URL")
    if override:
        return override
    cfg = _REPO_ROOT / ".mcp.json"
    if cfg.is_file():
        try:
            servers = json.loads(cfg.read_text(encoding="utf-8")).get("mcpServers", {})
        except (OSError, ValueError):
            servers = {}
        for entry in servers.values():
            url = entry.get("url")
            if url and re.search(r"/mcp/?$", url):
                return url
    return "http://127.0.0.1:21471/mcp"


URL = _resolve_url()


def _post(payload, session=None):
    """POST one JSON-RPC message. Returns (parsed_result_or_None, session_id)."""
    body = json.dumps(payload).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        # Streamable HTTP lets the server answer with either a JSON body or an SSE stream.
        # Advertising both is required; servers reject a request that accepts only one.
        "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": "2025-06-18",
    }
    if session:
        headers["Mcp-Session-Id"] = session
    req = urllib.request.Request(URL, data=body, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=600) as resp:
        sid = resp.headers.get("Mcp-Session-Id") or session
        raw = resp.read().decode("utf-8", "replace")
    if not raw.strip():
        return None, sid
    return _parse(raw), sid


def _parse(raw):
    """Unwrap an SSE frame if that is what came back, otherwise parse plain JSON."""
    if raw.lstrip().startswith("{"):
        return json.loads(raw)
    for line in raw.splitlines():
        if line.startswith("data:"):
            return json.loads(line[5:].strip())
    raise SystemExit(f"unparseable response: {raw[:400]}")


def _handshake():
    msg, sid = _post({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "claude-code-shell-bridge", "version": "1.0"},
        },
    })
    if msg and "error" in msg:
        raise SystemExit(f"initialize failed: {msg['error']}")
    _post({"jsonrpc": "2.0", "method": "notifications/initialized"}, sid)
    return sid


def _rpc(method, params, sid):
    msg, _ = _post({"jsonrpc": "2.0", "id": 2, "method": method, "params": params}, sid)
    if msg is None:
        raise SystemExit(f"{method}: empty response")
    if "error" in msg:
        raise SystemExit(f"{method} failed: {json.dumps(msg['error'])[:800]}")
    return msg.get("result", {})


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    sid = _handshake()
    verb = sys.argv[1]

    if verb == "list":
        result = _rpc("tools/list", {}, sid)
        for tool in result.get("tools", []):
            print(f"{tool['name']}\t{(tool.get('description') or '').splitlines()[0][:150]}")
        return

    if verb == "schema":
        result = _rpc("tools/list", {}, sid)
        for tool in result.get("tools", []):
            if tool["name"] == sys.argv[2]:
                print(json.dumps(tool, indent=2)[:6000])
        return

    if verb == "call":
        name = sys.argv[2]
        src = sys.argv[3] if len(sys.argv) > 3 else None
        if src is None:
            args = {}
        elif src == "-":
            args = json.loads(sys.stdin.read())
        elif src.lstrip().startswith("{"):
            # Inline JSON, for the small calls where a temp file is pure ceremony.
            args = json.loads(src)
        else:
            with open(src, encoding="utf-8") as handle:
                args = json.load(handle)
        result = _rpc("tools/call", {"name": name, "arguments": args}, sid)
        # Unity returns text content blocks; print them raw so callers can grep the payload.
        for block in result.get("content", []):
            if block.get("type") == "text":
                print(block.get("text", ""))
            else:
                print(json.dumps(block))
        if result.get("isError"):
            sys.exit(2)
        return

    raise SystemExit(__doc__)


if __name__ == "__main__":
    main()
