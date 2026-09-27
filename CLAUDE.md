# Vexillum

A 2D team capture-the-flag game with destructible pixel terrain, written in
C# on XNA 4.0 in 2012-2013 by the owner's friend (accepted to Steam Greenlight
in 2015). This repository is the original source plus the last shipped
Windows binaries in `Test/`. The goal of this project is to make the game
compile and run again on today's toolchain (macOS first, then Windows/Linux)
using MonoGame, and then keep developing it.

## Preservation comes first

This code is a historical artifact: the owner's friend wrote it over ten
years ago, and keeping *their* code intact matters more than making it
modern. The `.cs` files under `Game/`, `Server/`, `ZombieSurvival/`,
`MapTool/`, `CreateMap.cs/`, `ExtractMap/`, `ServerStart/`, `Platform/`
and `ZombieSurvivalContent/` are the artifact. Gameplay, protocol, file
formats, naming, layout, comments and even the author's quirks stay.

What is **not** historical and may be replaced freely: the binaries in
`dlls/`, `lib/`, `Test/*.exe`, the vendored third-party libraries
(`Lzma/`, `Game/Game/util/misc/`), the XNA content project, the 2010
`.csproj`/`.sln` files and the 2025 `build.sh`/`BUILD_NOTES.md` attempt.
The libraries the game depends on (XNA, Nuclex, SlimDX, Steamworks.NET,
System.Drawing, WinForms) are dependencies, not the work; they can be
swapped for real implementations, source ports, or shims.

The order of preference when the original source does not compile:

1. **Leave the file untouched** and make the missing dependency exist:
   a shim assembly that provides the same namespaces, types and members
   (`System.Drawing.Bitmap`, `Nuclex.Input.MouseButtons`,
   `Steamworks.CSteamID`, `Microsoft.Xna.Framework.GamerServices` as an
   empty namespace, `System.Windows.Forms.MessageBox`, ...), or a source
   port of the original library (Nuclex is CPL-licensed and available).
2. **Minimal edit** when a shim is impossible, marked on the line with
   `// PORT: <reason>` so the diff against the 2013 source stays readable.
3. **Rewrite** only for files that never worked (`PlatformLinux/`,
   `ZombieSurvival/ZombieSurvival.cs`) and only with the owner's agreement.

Before any porting edit, run `git diff 370aa81 -- <file>`; the initial
commit is the author's original and the 2025 commit `c948e3c` already
made small source edits (commented usings, `override` → `new`, a stub
Steamworks file) that should be reverted in favour of shims.

## Read first

* `docs/ARCHITECTURE.md` — projects, threads, coordinate systems, terrain
  bitfield, map format, entities, rendering, input.
* `docs/PROTOCOL.md` — the wire protocol, byte for byte.
* `docs/PORTING.md` — the step plan, decisions still open, hazard inventory,
  known original bugs, and the status table you must keep updated.
* `README.md` — the author's note and two gameplay videos. Do not rewrite
  their text; append a build section when the port works.

## Repository map

```
Game/Game/            shared library (client+server logic, UI, net)   -> Game.dll
Server/               dedicated server exe                            -> VexillumServer
ZombieSurvival/       client exe entry point (assembly VexillumGame)  -> VexillumGame
Lzma/                 vendored LZMA SDK (do not edit beyond csproj)
MapTool/ CreateMap.cs/ ExtractMap/   map packing tools
Platform/             Vec2 wrapper (to be folded into Game); PlatformLinux/ is dead
ZombieSurvivalContent/  XNA content sources (fonts, wavs, Blur.fx, GUI skin)
Test/                 RUNTIME DIRECTORY: shipped exes + Content/ Maps/ Server/ configs
dlls/ lib/            binaries from the failed 2025 Mono attempt (to be deleted)
docs/                 reference docs listed above
.claude/              skills, agents, MCP server for this repo
```

Working directory at runtime must look like `Test/`: `Content/` (loose PNG/JPG
plus `.xnb`), `Maps/*.map`, `Server/settings.txt`, `settings.xml`,
`controls.xml`. Game and server write `debug_client.log` /
`Server/debug_server.log` and a `lock` file there.

## Platform policy

Cross-platform, Linux-style project: everything is driven by the `dotnet`
CLI and POSIX shell, paths use forward slashes and are case-sensitive,
builds and runs are native on Apple Silicon (arm64), Linux and Windows
from the same sources. **No Wine, no Proton, no Rosetta, no Windows-only
build step, ever.** If a tool needs one of those (MonoGame's `mgfxc`
shader compiler does), the tool is not used; find another way (see
`content-pipeline` skill for the shader). Mono/`xbuild` are not the target
either: the runtime is .NET 8+ (`net9.0` here) and the framework is
MonoGame `DesktopGL`, whose native SDL2/OpenAL libraries ship as arm64.

## Toolchain on this machine (macOS 26, M4, arm64)

* `dotnet` 9.0.305 SDK (native arm64), runtime 9.0.9 only. No .NET Framework
  targeting pack, so the 2010 `.csproj` files cannot build with `dotnet build`
  (MSB3644); that is expected and is why step 1 replaces them.
* MonoGame 3.8.4 DesktopGL is in the NuGet cache; NuGet and GitHub are reachable.
* `mono` 6.14 is installed but unused. Do not extend `build.sh`.
* `wine` 10 is installed but must not be used (platform policy).
* `ilspycmd` 9.1 is at `~/.dotnet/tools/ilspycmd`; run it with
  `DOTNET_ROLL_FORWARD=Major` (the MCP `decompile` tool does this). Useful
  for reading the old DLLs' public API when writing shims.
* `uv` and Python 3.13 run the MCP server.

## Commands

Until step 1 of `docs/PORTING.md` lands there is no working build. After it:

```
dotnet build Vexillum.sln -c Debug                 # or the MCP `build` tool
dotnet run --project Server -- --root Test         # server, headless
dotnet run --project ZombieSurvival -- --root Test --connect 127.0.0.1:24224
```

Prefer the MCP tools (`build`, `smoke_test`, `run_server`, `run_client`) over
raw shell for these; they parse errors and capture logs.

## Invariants

### A. Formats and protocol (never change without a deliberate version bump)

1. Wire protocol is `docs/PROTOCOL.md`: little-endian, packet ids, field
   order and encodings, `PROTOCOL_VERSION = 3`, `DEFAULT_PORT = 24224`. A
   ported client must interoperate with `Test/VexillumServer.exe`.
2. `StreamHelper.entityTypes` order, and the declaration order of
   `PlayerClass`, `KeyAction`, `Sounds`, are wire format. Never reorder,
   insert in the middle, or rename those types or their namespaces
   (`Vexillum.Entities.*`, `Vexillum.Entities.Weapons.*`). Entities and
   weapons keep public parameterless constructors (created via `Activator`).
3. Map file format: magic `0x004F876B`, LZMA-alone payload, file list and
   `data.txt` grammar as documented. `Test/Maps/bases.map` and `complex.map`
   must keep loading, and a client must still be able to receive and save a
   map from the server (packets 220-222).
4. Collision image colour codes and the `TerrainArray` bit layout are format:
   the server sends the terrain bitfield (`ToBytes`, column-major, bit 0 only)
   and both sides must derive identical arrays from the same map.
5. `settings.xml`, `controls.xml` (XmlSerializer of `SerializedSettings` /
   `Controls`), `Server/settings.txt` keys, `Server/ops.txt`, `banned.txt`
   keep their schema so existing user files keep working.
6. `.xnb` fonts and sounds in `Test/Content` are the content until a pipeline
   exists; content names (`DefaultFont`, `TitleFont`, `FancyFont`,
   `TinyFont`, `explosion`, `Rocket`, `walk1..5`, `click`, `click2`, `smg`,
   `sword1..3`, `Blur`, `ui/DarknessUI.xml`) are referenced by string in code.

### B. Gameplay behaviour (preserve while porting)

7. Fixed step: `FRAME_RATE = 60`, `TIME_PER_FRAME = 16` ms, `IsFixedTimeStep`.
   Physics constants (`Gravity 0.3`, `friction 0.5`, jump `6`, step height
   `7`, explosion trace logic, `EPSILON 0.1`) are gameplay, not tunables.
8. Window is 840x630, `Scale = 1`; HUD layout is hard-coded to it. Making it
   resizable is a feature for later, not part of the port.
9. Frame-scheduled tasks (`FrameTaskQueue`) and the one-mutator-thread rule
   (see ARCHITECTURE "Threading rules") stay. Do not replace the task queues
   with `async`/`Task` plumbing or add locks around level state; keep the
   closure-enqueue pattern.
10. Rendering order and sprite batch modes (`Immediate`, `NonPremultiplied`,
    `PointClamp`, `RenderTargetUsage.PreserveContents`, render-to-target then
    blur) stay. If MonoGame needs a different way to reach the same pixels,
    change the mechanism, not the result.
11. Known original bugs are listed in `docs/PORTING.md`. Fixing one is a
    separate, named commit after the port compiles, never a drive-by inside a
    porting change.

### C. Porting rules

12. Target `net8.0`+ SDK-style projects and MonoGame 3.8.x DesktopGL from
    NuGet. No `packages.config`, no `xbuild`, no `lib/MonoGame`, no
    `dlls/`. One solution builds everything with `dotnet build` on macOS
    arm64, Linux and Windows with no platform-specific step.
13. Original source files are preserved (see "Preservation comes first").
    Missing dependencies are supplied by a `Shims/` project (or several)
    that reproduces the namespaces, types and members the game actually
    uses, implemented on MonoGame and cross-platform .NET. Shims are
    judged by "does the original file compile and behave the same",
    not by completeness of the emulated API.
14. Windows-only assemblies (`System.Drawing`, `System.Windows.Forms`,
    `SlimDX`, `Awesomium`, `user32`) never become real references. Where the
    original code names them, a shim of the same name provides a
    cross-platform implementation; `#if WINDOWS` is not used. `DllImport`
    exists only in `KeyboardMessageFilter.cs`, which the shim makes inert.
15. Do not use the prebuilt `dlls/Nuclex.*.dll`, `dlls/Platform.dll`,
    `dlls/SlimDX.DirectInput.dll` in any project; they are bound to the real
    XNA assemblies and cannot load against MonoGame. Preferred: port Nuclex
    `UserInterface`, `Input` and `Support` from source into `Shims/Nuclex/`
    (CPL license, keep its notice) minus the DirectInput/SlimDX and
    test/NMock parts, so every dialog and the skin XML work unchanged.
16. `System.Drawing` shim: `Bitmap`, `Color`, `Rectangle`, `Brush`/
    `SolidBrush`/`Brushes`, `Graphics` (stub), `Font` (stub), `Imaging`
    (`BitmapData`, `ImageLockMode`, `ImageFormat`, `PixelFormat`,
    `ImageCodecInfo` stub), implemented over a managed decoder
    (`StbImageSharp`). `LockBits` must return a pinned 32bpp BGRA buffer
    with the stride semantics `Level`'s constructor expects, `GetPixel`/
    `SetPixel`/`MakeTransparent`/`Clone`/`Save(PNG)` must match GDI+
    results for the pixels the game reads. Prove it with a terrain-hash
    test on the two shipped maps.
17. Steam: keep `using Steamworks;` in the original files and complete the
    `Steamworks` shim (started in `Game/Game/SteamworksStub.cs`; move it to
    `Shims/`) so client and server compile; offline it produces an identity
    the way `Vexillum.SetSteamIdentity` does today. The game must start,
    join a server and play with no Steam and no network access to
    `playvexillum.com`. All HTTP calls have short timeouts and fail
    silently to the log.
18. The server must build and run with no graphics device and no window;
    `Util.IsServer` guards stay in place.
19. New entry-point code (not the author's) may add `--root <dir>`,
    `--connect <host>:<port>`, `--port <n>` and honour
    `VEXILLUM_LOG_STDOUT=1` (echo `Util.Debug` to stdout) and
    `VEXILLUM_DEBUG_PORT=<n>` (in-process debug console, `Shims/DebugHost`)
    so the MCP tools can drive and inspect both programs. Without
    `--connect` the client behaves exactly as before; without the env vars
    nothing extra runs.
20. `Lzma/` and `Game/Game/util/misc/` (Jon Skeet's MiscUtil) are vendored
    third-party code: touch only their project files.
21. Port one hazard class per commit (see `docs/PORTING.md` steps). Each
    commit builds. Do not delete the old `.csproj`/`.sln` until the new
    solution builds every project. `Test/` binaries are the reference build;
    never delete or overwrite them.

### D. Process rules

22. Before editing a subsystem, read the matching section of
    `docs/ARCHITECTURE.md`; after finishing a step, update the status row in
    `docs/PORTING.md` in the same commit.
23. Run the MCP `invariant_check` tool before committing anything under
    `Game/Game/net`, `Game/Game/Entities`, `Game/Game/game`, `Server/`,
    `Game/Game/util/TerrainArray.cs` or `Game/Game/LevelLoader.cs`. Run
    `port_audit` after any porting commit and paste its summary into the
    commit message when a hazard class reaches zero.
24. Never commit `bin/`, `obj/`, `.DS_Store`, `debug_*.log`, `lock`,
    `.claude/settings.local.json`. Commit only when asked.
25. Keep the original author's naming and file layout. New code (shims,
    entry points) lives outside the historical directories and follows the
    existing style (4-space indent, Allman braces, `camelCase` fields as the
    files around it do); do not reformat files you are not otherwise changing.
    Every edit to a historical file carries a `// PORT:` comment.
26. Do not add analyzers, nullable annotations, `var`-everywhere rewrites,
    or LINQ-ification passes. Warnings are fine; the port is judged on
    behaviour, not on lint.

## Claude tooling in this repo

* Skills (`/name`): `build-loop` (compile-fix cycle), `xna-migration`
  (API mapping for this codebase), `port-audit` (hazard scan + PORTING.md
  update), `run-vexillum` (run server/client, smoke test, screenshots),
  `map-tools` (inspect, extract, build maps), `content-pipeline` (xnb, mgfx,
  fonts).
* Agents: `port-auditor` (read-only hazard and invariant report),
  `build-fixer` (drives the build green under the invariants),
  `netcode-guardian` (reviews protocol-touching diffs), `runtime-tester`
  (runs the game and reports what happened).
* MCP server `vexillum-dev` (`.claude/mcp/vexillum_dev.py`, launched by
  `.claude/mcp/serve.sh`; also a CLI: `python3 .claude/mcp/vexillum_dev.py <tool> k=v`):
  - build and checks: `build`, `port_audit`, `invariant_check`,
    `preservation_check`, `runtime_status`;
  - timed runs: `run_server`, `run_client`, `smoke_test` (start, capture, stop);
  - persistent runs: `proc_start` / `proc_stop` / `proc_status` / `proc_logs`
    / `screenshot` keep a server and any number of clients running across
    tool calls (state in `/tmp/vexillum-dev/proc`);
  - in-process debugging: `eval` runs a C# script inside a running client or
    server (Roslyn scripting, state persists; `Game`/`Server` dynamics,
    `Sync(() => ...)` for the game thread, `Get`/`Set`/`Call` for private
    members, `Dump`); `probe` runs canned inspections (players, entities,
    frame, view, gamemode, level, threads). This needs the process started
    by `proc_start` (it sets `VEXILLUM_DEBUG_PORT`); the console lives in
    `Shims/DebugHost` and is never active otherwise;
  - content: `map_info`, `extract_map`, `create_map`, `terrain_reference`,
    `xnb_info`, `decompile`.
  Tool docs are in the server file; `runtime_status` is the right first call
  in a fresh session. Do not run two servers on one port: timed and
  persistent tools share port 24224 unless you pass `port=`.
