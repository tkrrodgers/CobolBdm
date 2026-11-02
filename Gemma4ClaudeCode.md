
Gemma 4 (`google/gemma-4-31B-it` via DeepInfra) is now a `gemma-4` sub-agent for both me (Copilot) and Claude Code on this laptop. Live calls through the Hugging Face model and through the Claude Code `gemma-4` sub-agent both returned Gemma replies. I haven't yet seen the new Copilot agent file load, because this session's agent list was set when it started; it should appear in a new chat.

**Copilot (VS Code chat and this agent):** `gemma-4.agent.md`
- It shows up in VS Code's agent picker, and Copilot can hand tasks to it as a sub-agent.
- The file lists the model under both naming formats, because VS Code and the Copilot agent runtime name it differently.
- It can read, search, edit, use the terminal and the web.
- A test sub-agent on the Gemma model returned `GEMMA_OK`.

**Claude Code:** `gemma-4.md`
- Claude Code can't run non-Anthropic models directly. So this sub-agent runs on Haiku and passes the task to Gemma through a small bridge, `gemma_mcp.py`. It returns Gemma's answer unchanged.
- The bridge only loads inside that sub-agent, so it doesn't clutter Claude's main session.
- VS Code also reads `~/.claude/agents`. Two settings in this file keep the Claude-only version out of Copilot's agent list, while Claude Code still uses it.
- In a test, Claude Code handed the task to `gemma-4` and Gemma replied `GEMMA_OK Google DeepMind`.

**Hugging Face token**
- The original key was missing the Inference Providers permission, and then the account had no credits. Both are fixed now.
- Copilot uses the key stored in the Hugging Face extension.
- Claude Code reads its own copy from `~/.cache/huggingface/token`. To change it later, run `Save-HfToken.ps1`; it checks the token before saving.

Things to know:
- Every Gemma call uses your Hugging Face credits. Claude Code's relay also uses a small amount of Haiku.
- I added `mcp__gemma__ask_gemma` to the allowed tools in `settings.json`. This stops background sub-agents getting stuck on a permission prompt. It also means Claude Code won't ask before sending code to Hugging Face/DeepInfra; remove that entry if you'd rather approve each call.
- To use it, ask for "Gemma 4" or "a second opinion from Gemma", or name `gemma-4` directly.