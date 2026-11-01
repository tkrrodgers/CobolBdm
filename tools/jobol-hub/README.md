# jobol-hub (Phase 2): **disabled pending approval**

This is a local channel between the pipeline session (Claude Code in VS Code, role `pipeline`) and the architect session (Claude desktop, role `architect`). Either side can run the deterministic JOBOL audit, read stored reports, and exchange signed review requests and feedback. The design, security model and why no VPN or socket is used are in [JavaJOBOLAuditor.md §8](../../JavaJOBOLAuditor.md#8-phase-2-the-hub-between-the-two-claude-sessions-built-disabled).

- `jobol_hub.py` is the MCP server (stdio, Python standard library only) and the enable/disable switch.
- `hub-config.json` holds the policy. **`enabled: false`**: while false the server exposes only `hub_status`, and nothing registers it.
- `test_jobol_hub.py` holds the tests. They run in throwaway temp stores:
  ```bash
  python -m unittest tools/jobol-hub/test_jobol_hub.py
  ```

## Status

```bash
python tools/jobol-hub/jobol_hub.py status
```

## Enable (only after approval)

```bash
python tools/jobol-hub/jobol_hub.py enable --approve --by "<your name>"
```

This command:
1. creates `%LOCALAPPDATA%\JobolHub`, restricted to your account, with a new signing key
2. records the approval in `hub-config.json`
3. registers `jobol-hub` in the repo's `.mcp.json`

Each Claude Code session then asks you once to approve the server.

To turn it off:

```bash
python tools/jobol-hub/jobol_hub.py disable
```

Add `--purge` to also delete the messages, reports and key.
