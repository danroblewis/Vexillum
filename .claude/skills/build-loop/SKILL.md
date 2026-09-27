---
name: build-loop
description: Compile-and-fix cycle for the Vexillum port. Use when asked to build, make it compile, fix build errors, or get a project green. Drives dotnet build through the vexillum-dev MCP tool and fixes errors without changing gameplay.
---

# Build loop

## Preconditions

1. Read `CLAUDE.md` section C (porting rules) if not already in context.
2. Call the MCP tool `runtime_status` once to learn which solution/projects
   exist and whether the new SDK-style solution is in place. If only the 2010
   `Vexillum.sln` exists, do step 1 of `docs/PORTING.md` first (there is no
   point running `dotnet build` on the legacy projects).

## Loop

1. `build` (MCP) with the target you are working on. Prefer building the
   smallest project that contains the file you changed, then the solution.
2. Read the grouped error summary. Fix errors in this order:
   * missing references / project references (csproj problems)
   * namespace `using`s that no longer exist (`GamerServices`, `Storage`,
     `Nuclex.*`, `Steamworks`, `System.Drawing`, `System.Windows.Forms`)
   * API differences (see the `xna-migration` skill table)
   * everything else
   Group by error code; one root cause often explains dozens of errors.
3. Re-run `build`. Stop when it reports success; then run `port_audit` and
   `invariant_check` and report both.

## Rules while fixing

* A fix must not change behaviour. If the only way to compile is to change
  what the code does, stop, describe the choice, and pick the option that
  keeps behaviour (usually: add a small shim type or wrapper).
* Never "fix" an error by deleting a call site. Exception: the files
  `docs/PORTING.md` step 6 lists as dead (`DemoDialog`, `WebControl*`,
  `XNASurface`, `CustomInputControl*`) and `ZombieSurvival/ZombieSurvival.cs`.
* Do not exclude files from compilation to hide errors (the 2025 attempt did
  this with two renderers; undo that when you get there).
* Do not touch `Lzma/` or `Game/Game/util/misc/` sources.
* Suppressing warnings is fine; suppressing errors with `#pragma` or
  `unsafe`/`dynamic` tricks is not.
* When an error is in vendored Nuclex sources (once ported), fix it there
  with the smallest edit and note it in a `NUCLEX-PORT-NOTES.md` next to it.

## Reporting

End with: build result, error count before/after, hazard counts from
`port_audit`, and the PORTING.md row you updated.
