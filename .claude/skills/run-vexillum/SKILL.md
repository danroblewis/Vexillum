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

## Persistent runs and live debugging

The timed tools stop what they start. To keep things running across several
tool calls use the `proc_*` family:

* `proc_start(target="server", port=24224)` then
  `proc_start(target="client", connect="127.0.0.1:24224", instance=0)`;
  more clients with `instance=1,2,...` (tracked as `client1`, `client2`).
* `proc_status()`, `proc_logs(target, lines, grep)`, `screenshot()`,
  `proc_stop(target="all")`. Always stop what you started.
* `eval(target, code)` evaluates C# inside that process (Roslyn scripting,
  variables persist per process; send `!reset` to clear). Globals: `Game`
  (client, dynamic `Vexillum.Vexillum`), `Server` (dynamic `Server.Server`),
  `Sync(() => expr)` runs on the game/step thread (use it for anything that
  reads or mutates level/entity state), `Get(obj, "name")`, `Set`, `Call`,
  `Static("Type", "member")`, `TypeOf`, `Dump(obj)`, `Log(x)`.
  Examples:
  - `eval client 'Game.View.GetType().Name'`
  - `eval client 'Sync(() => ((GameView)Game.View).Level.frame)'`
  - `eval client 'Sync(() => { var p = (LocalPlayer)Get((GameView)Game.View, "player"); p.Entity.Position += new Vec2(0, 50); return p.Position; })'`
  - `eval server 'Sync(() => Server.level.getEntities().Count)'`
* `probe(target, what)` is a shortcut for the common inspections:
  client `view|frame|player|entities|players|gamemode|threads`, server
  `players|frame|entities|gamemode|level|threads`; `what="list"` prints the
  scripts, which are good starting points for your own `eval`.

The console is only present when the process was started by `proc_start`
(env `VEXILLUM_DEBUG_PORT`); it listens on 127.0.0.1 only. A script that
hangs the game thread is reported as a `Sync` timeout, not killed.

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
