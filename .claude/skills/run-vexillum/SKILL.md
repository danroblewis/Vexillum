---
name: run-vexillum
description: Launch the Vexillum server and client, run the loopback smoke test, and capture logs and screenshots. Use when asked to run, start, test, or screenshot the game or server, or to confirm a change works in the real app.
---

# Running Vexillum

## Runtime directory

Both executables must run with a working directory shaped like `Test/`
(see CLAUDE.md "Repository map"). The MCP tools default to `Test/`. Never
run the shipped `Test/*.exe` on macOS expecting them to work: they are
Windows XNA builds and exist only as a reference. `mono Test/VexillumServer.exe`
fails on the Steamworks.NET reference; do not spend time on it.

## Which binary runs

`runtime_status` (MCP) reports what is built. Order of preference the tools
use: `Server/bin/**/VexillumServer.dll` and `ZombieSurvival/bin/**/VexillumGame.dll`
via `dotnet`, else nothing. Build first with the `build-loop` skill.

## Tools

* `run_server(seconds=8)` — starts the server in the runtime dir, waits,
  stops it, returns stdout/stderr and the tail of `Server/debug_server.log`.
  Success marker: `Ready for connections`.
* `run_client(seconds=12, screenshot=true, connect="")` — starts the client,
  optionally with `--connect host:port`, takes a `screencapture` of the main
  display a second before stopping, returns log tail and the PNG path. Read
  the PNG with the Read tool to see what the window showed.
* `smoke_test(seconds=25)` — server + client on loopback. Passes when the
  server logs `logged in as` and the client log shows the level finish
  (packet 9) without `Disconnected`. Returns both logs.

## Interpreting logs

`Util.Debug` prefixes lines with `[yyyy-MM-dd HH:mm:ss]`. Typical client
sequence: `Connecting to`, then packets, `Set terrain state (N bytes)`,
then gameplay. `Disconnected: <msg>` with `Invalid command: N` means the
reader desynchronised: a protocol regression (run `invariant_check`).
`Wrong protocol version` means `PROTOCOL_VERSION` differs.

Server: `Loading level...`, `Ready for connections`, `Connection from`,
`<ip> logged in as <name>`, chat lines, `<name> disconnected`.

## Manual play testing

Run the client without `--connect`; the main menu has "Start Playing" (server
list from a dead web service, so use "Direct IP Join..." with `127.0.0.1`
and port 24224). Controls: A/D move, W jump, S down ladders, F grapple,
mouse aim/fire, 1-3 weapons, R reload, `.` chat, Tab scoreboard, Esc pause.
`/green` and `/blue` in chat switch teams; `/spec` spectates.

## Screenshots

`screencapture -x <file>` captures the whole display, so the game window
must be frontmost; the tool `open -a`s the process. If the capture is black,
the GL context was not ready yet: increase `seconds`.
