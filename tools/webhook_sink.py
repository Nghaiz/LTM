#!/usr/bin/env python3
"""A webhook receiver that records what it was sent.

WHY THIS EXISTS. tools/alert.sh has always been able to DECIDE that something is wrong -- the
four conditions are straightforward reads of the master's metrics snapshot. What was never
tested is the other half: that the decision reaches a person. `notify()` builds a JSON body and
curls it at IRONFRONT_ALERT_WEBHOOK, and with no webhook configured it prints to stdout and
stops, which is indistinguishable from working. Every "alert drill" that killed a process and
watched a log line proved the timer fired and nothing about delivery.

This is the receiving end. Point IRONFRONT_ALERT_WEBHOOK at it and every POST is appended to a
file, one JSON object per line, with the body verbatim. An assertion can then be made about what
arrived rather than about what was logged locally.

It is deliberately dumb: no auth, no TLS, binds loopback by default. It is a test fixture, not a
service, and it must never be reachable from anywhere the real alert stream is.

Usage:
    python3 tools/webhook_sink.py --port 8099 --out artifacts/alert-drill/received.jsonl
    python3 tools/webhook_sink.py --port 8099 --out /tmp/received.jsonl --host 127.0.0.1

Every request is answered 204 regardless, because a sink that 500s would make alert.sh print
"webhook post failed" and the drill would then be measuring the sink instead of the alert.
"""

import argparse
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, HTTPServer


class Sink(BaseHTTPRequestHandler):
    out_path = None

    def do_POST(self):  # noqa: N802 - the name is BaseHTTPRequestHandler's
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length).decode("utf-8", errors="replace") if length else ""

        record = {
            "receivedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "path": self.path,
            "contentType": self.headers.get("Content-Type", ""),
            "body": raw,
        }

        with open(Sink.out_path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(record) + "\n")
            handle.flush()

        self.send_response(204)
        self.end_headers()

    def do_GET(self):  # noqa: N802
        # So a human can confirm the sink is up without writing a fake alert into the record.
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.end_headers()
        self.wfile.write(b"ironfront webhook sink\n")

    def log_message(self, fmt, *args):
        # Quiet: the record file is the output, and access-log noise on stderr makes a drill
        # script's own output unreadable.
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8099)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--out", required=True, help="JSONL file; appended, one record per POST")
    args = parser.parse_args()

    # Created up front so a drill can assert "zero lines" without special-casing an absent file,
    # which is the difference between "nothing arrived" and "the sink never started".
    open(args.out, "a", encoding="utf-8").close()

    Sink.out_path = args.out
    server = HTTPServer((args.host, args.port), Sink)
    print(f"[sink] listening on {args.host}:{args.port}, recording to {args.out}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
