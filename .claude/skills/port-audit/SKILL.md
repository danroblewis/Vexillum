---
name: port-audit
description: Scan the Vexillum sources for Windows-only and XNA-only API usage, verify format/protocol invariants, and update the status in docs/PORTING.md. Use after a porting commit, before a PR, or when asked how much porting work remains.
---

# Port audit

1. Call MCP `port_audit` (no arguments for the whole tree, or pass
   `paths=["Game/Game/ui"]` to narrow). It returns, per hazard pattern, the
   files and hit counts, plus a total.
2. Call MCP `invariant_check`. It parses the sources and compares the entity
   type table, enum orders, protocol version, port, map magic and frame
   constants against the values in `docs/PROTOCOL.md`. Any mismatch is a
   blocker: either the doc or the code is wrong, and code changes to those
   are only allowed as a deliberate version bump.
3. If the new solution exists, call `build` so the report includes whether
   the tree compiles.
4. Update `docs/PORTING.md`:
   * status column of the step(s) touched (`todo` → `in progress` → `done (date)`),
   * the hazard inventory section when a hazard class changed materially,
   * the known-bugs list if you found a new original bug (do not fix it).
5. Report in this shape:

```
Build: <green|red|no solution yet>
Hazards: System.Drawing 12 files (was 16), Nuclex 27, Steamworks 9, WinForms 7, DllImport 1, Awesomium 3
Invariants: OK | MISMATCH <what>
PORTING.md: updated rows <n>, <m>
Next step: <step number and one line>
```

Patterns the audit looks for and why each matters are documented at the top
of `.claude/mcp/vexillum_dev.py` (`HAZARDS`). Add a pattern there if you
discover a new class of Windows-only usage; keep the list and
`docs/PORTING.md` in sync.
