#!/bin/sh
# Launcher for the vexillum-dev MCP server (stdio). Used by .mcp.json.
# Tries, in order: a python3 that already has the `mcp` package, then `uv`
# (searching the usual install locations, since MCP hosts often start with
# a minimal PATH). Diagnostics go to .claude/mcp/serve.log, never to stdout,
# because stdout is the JSON-RPC channel.
cd "$(dirname "$0")/../.." || exit 1
LOG=".claude/mcp/serve.log"
: > "$LOG"
SERVER=".claude/mcp/vexillum_dev.py"

for py in python3 /opt/anaconda3/bin/python3 /opt/homebrew/bin/python3 /usr/local/bin/python3 /usr/bin/python3 "$HOME/.pyenv/shims/python3"; do
  if command -v "$py" >/dev/null 2>&1 && "$py" -c "import mcp" >/dev/null 2>&1; then
    echo "using $py (has mcp)" >> "$LOG"
    exec "$py" "$SERVER" serve 2>> "$LOG"
  fi
done

for uv in uv "$HOME/.local/bin/uv" /opt/homebrew/bin/uv /usr/local/bin/uv; do
  if command -v "$uv" >/dev/null 2>&1; then
    echo "using $uv run --with mcp<2" >> "$LOG"
    exec "$uv" run --quiet --with "mcp<2" python "$SERVER" serve 2>> "$LOG"
  fi
done

echo "no python with the mcp package and no uv found; install one: pip install 'mcp<2' or brew install uv" >> "$LOG"
exit 1
