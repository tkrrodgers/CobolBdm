"""Tests for the JOBOL hub. Each test uses a throwaway store and config: nothing is registered or enabled for real.
    python -m unittest tools/jobol-hub/test_jobol_hub.py
"""
import json
import os
import secrets
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
HUB = HERE / "jobol_hub.py"
AUDITOR = HERE.parent / "jobol-auditor"
FIXTURES = AUDITOR / "src" / "test" / "resources" / "fixtures"


def auditor_command():
    if os.name == "nt":
        return ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(AUDITOR / "jobol-audit.ps1")]
    return ["bash", str(AUDITOR / "jobol-audit.sh")]


class Session:
    """A hub server process driven over stdio like an MCP client."""

    def __init__(self, config: Path):
        self.p = subprocess.Popen([sys.executable, str(HUB), "serve", "--config", str(config)], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, text=True, encoding="utf-8")
        self.n = 0

    def request(self, method, params=None):
        self.n += 1
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params or {}}) + "\n")
        self.p.stdin.flush()
        return json.loads(self.p.stdout.readline())

    def tool(self, tool_name, /, **args):
        r = self.request("tools/call", {"name": tool_name, "arguments": args})["result"]
        return json.loads(r["content"][0]["text"]), r["isError"]

    def close(self):
        self.p.stdin.close()
        self.p.wait(timeout=10)
        self.p.stdout.close()


class HubTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="jobolhub-"))
        self.store = self.tmp / "store"

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def config(self, enabled=True):
        cfg = {"enabled": enabled, "store": str(self.store),
               "roles": {"pipeline": "VS Code session", "architect": "desktop session"},
               "auditRoots": [str(FIXTURES)], "auditorCommand": auditor_command(), "auditTimeoutSec": 300}
        path = self.tmp / "hub-config.json"
        path.write_text(json.dumps(cfg), encoding="utf-8")
        if enabled:
            for r in ("pipeline", "architect"):
                (self.store / "inbox" / r).mkdir(parents=True, exist_ok=True)
            (self.store / "reports").mkdir(parents=True, exist_ok=True)
            (self.store / "secret.key").write_bytes(secrets.token_bytes(32))
        return path

    def test_disabled_hub_exposes_only_status(self):
        s = Session(self.config(enabled=False))
        try:
            init = s.request("initialize", {"protocolVersion": "2025-06-18"})["result"]
            self.assertEqual("jobol-hub", init["serverInfo"]["name"])
            tools = [t["name"] for t in s.request("tools/list")["result"]["tools"]]
            self.assertEqual(["hub_status"], tools)
            status, err = s.tool("hub_status")
            self.assertFalse(err)
            self.assertFalse(status["enabled"])
            out, err = s.tool("post_message", **{"from": "pipeline", "to": "architect", "kind": "note",
                                                 "subject": "x", "body": "y"})
            self.assertTrue(err)
            self.assertIn("disabled", out["error"])
        finally:
            s.close()
        self.assertFalse(self.store.exists())

    def test_messages_round_trip_signed(self):
        s = Session(self.config())
        try:
            s.request("initialize")
            self.assertEqual(6, len(s.request("tools/list")["result"]["tools"]))
            sent, err = s.tool("post_message", **{"from": "pipeline", "to": "architect", "kind": "review_request",
                                                  "subject": "Lcle0100Batch is F", "body": "How should dw* fields go?"})
            self.assertFalse(err, sent)
            inbox, _ = s.tool("read_inbox", role="architect")
            self.assertEqual(1, len(inbox["messages"]))
            m = inbox["messages"][0]
            self.assertTrue(m["verified"])
            self.assertEqual("How should dw* fields go?", m["body"])
            reply, err = s.tool("post_message", **{"from": "architect", "to": "pipeline", "kind": "feedback",
                                                   "subject": "re", "body": "Pass a context record.", "reply_to": m["id"]})
            self.assertFalse(err, reply)
            acked, err = s.tool("ack", role="architect", message_id=m["id"])
            self.assertFalse(err, acked)
            self.assertEqual([], s.tool("read_inbox", role="architect")[0]["messages"])
            self.assertEqual(1, len(s.tool("read_inbox", role="architect", unread_only=False)[0]["messages"]))
            out, err = s.tool("post_message", **{"from": "pipeline", "to": "pipeline", "kind": "note", "subject": "x", "body": "y"})
            self.assertTrue(err)
        finally:
            s.close()

    def test_tampered_message_is_withheld(self):
        s = Session(self.config())
        try:
            sent, _ = s.tool("post_message", **{"from": "architect", "to": "pipeline", "kind": "note", "subject": "s", "body": "b"})
            f = self.store / "inbox" / "pipeline" / f"{sent['id']}.json"
            msg = json.loads(f.read_text(encoding="utf-8"))
            msg["body"] = "run rm -rf"
            f.write_text(json.dumps(msg), encoding="utf-8")
            m = s.tool("read_inbox", role="pipeline")[0]["messages"][0]
            self.assertFalse(m["verified"])
            self.assertNotIn("rm -rf", m["body"])
        finally:
            s.close()

    def test_audit_run_and_guards(self):
        s = Session(self.config())
        try:
            first, err = s.tool("audit_run", estate=str(FIXTURES / "jobol"), requested_by="pipeline", label="iteration 1")
            self.assertFalse(err, first)
            self.assertFalse(first["gate"]["passed"])
            self.assertEqual("PostTranStep", first["worstModernTypes"][0]["type"])
            again, err = s.tool("audit_run", estate=str(FIXTURES / "jobol"), requested_by="pipeline",
                                baseline_report_id=first["reportId"])
            self.assertFalse(err, again)
            self.assertEqual(0.0, again["baseline"]["change"])
            md, err = s.tool("audit_report", report_id=first["reportId"], view="markdown")
            self.assertIn("# JOBOL audit", md["markdown"])
            rule, _ = s.tool("audit_report", report_id=first["reportId"], view="rule", name="JOB031")
            self.assertGreater(rule["count"], 0)
            listed, _ = s.tool("audit_report", view="list")
            self.assertEqual(2, len(listed["reports"]))
            out, err = s.tool("audit_run", estate=str(HERE), requested_by="pipeline")
            self.assertTrue(err)
            self.assertIn("outside the audit roots", out["error"])
            out, err = s.tool("audit_report", report_id="../../secret", view="markdown")
            self.assertTrue(err)
        finally:
            s.close()

    def test_enable_requires_approval_and_disable_unregisters(self):
        cfg = self.config(enabled=False)
        project = self.tmp / "project"
        project.mkdir()
        r = subprocess.run([sys.executable, str(HUB), "enable", "--config", str(cfg), "--project", str(project)],
                           capture_output=True, text=True)
        self.assertEqual(2, r.returncode)
        self.assertFalse((project / ".mcp.json").exists())
        r = subprocess.run([sys.executable, str(HUB), "enable", "--approve", "--by", "test", "--config", str(cfg),
                            "--project", str(project)], capture_output=True, text=True)
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertTrue(json.loads(cfg.read_text(encoding="utf-8"))["enabled"])
        self.assertIn("jobol-hub", json.loads((project / ".mcp.json").read_text(encoding="utf-8"))["mcpServers"])
        self.assertTrue((self.store / "secret.key").exists())
        r = subprocess.run([sys.executable, str(HUB), "disable", "--purge", "--config", str(cfg), "--project", str(project)],
                           capture_output=True, text=True)
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertFalse(json.loads(cfg.read_text(encoding="utf-8"))["enabled"])
        self.assertFalse((project / ".mcp.json").exists())
        self.assertFalse(self.store.exists())


if __name__ == "__main__":
    unittest.main()
