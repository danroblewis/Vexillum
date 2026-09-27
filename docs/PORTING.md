# Porting plan and status

Living document. Update the status column when a step lands. Every step must
leave `dotnet build Vexillum.sln` green (once the new solution exists) and
must not violate an invariant in `CLAUDE.md`.

## Target

* .NET 8 or later SDK-style projects (`net9.0` on this machine because the
  9.0.305 SDK is the only one installed). One new solution file.
* MonoGame 3.8.4 `DesktopGL` from NuGet (already in the local NuGet cache at
  `~/.nuget/packages/monogame.framework.desktopgl/3.8.4`). Same binaries run
  on macOS, Windows and Linux.
* Native on Apple Silicon, Linux and Windows. No Wine, Proton or Rosetta
  anywhere in the build or at runtime (CLAUDE.md "Platform policy").
* Original source files preserved; missing libraries supplied by a `Shims/`
  project and a source port of Nuclex (CLAUDE.md "Preservation comes first").
* No Windows-only assemblies referenced anywhere; the shims carry the same
  namespace names but are cross-platform managed code.
* Prebuilt `.xnb` sounds and fonts from `Test/Content` keep being used until a
  content pipeline is set up; `Blur.xnb` cannot be rebuilt without Wine, so
  the blur shader is served as GLSL assembled in code instead (step 8).

## How to build and run

Everything is the `dotnet` SDK (compiler + NuGet + test runner), native on
Apple Silicon/Linux/Windows; no Wine, Mono, `msbuild`/`xbuild` or `mgfxc`.
The `Makefile` wraps the usual commands. The runtime directory is `Test/`
(`Content/`, `Maps/`, `Server/settings.txt`, `settings.xml`, `controls.xml`).

```
make                # dotnet build Vexillum.sln -c Debug      15 projects, 0 errors
make test           # dotnet test Vexillum.sln                31 xunit tests
make server         # cd Test && ../Server/bin/Debug/net9.0/VexillumServer
make client         # cd Test && ../ZombieSurvival/bin/Debug/net9.0/VexillumGame
make play CONNECT=127.0.0.1:24224     # same, with --connect
make smoke          # loopback server + two clients through the vexillum-dev tool
make check          # invariant_check + preservation_check
make dist RID=osx-arm64               # dotnet publish --self-contained into dist/<rid>/
```

`dotnet build` emits, per executable project, a native launcher
(`VexillumServer`, `VexillumGame`, `.exe` on Windows) and the IL assembly it
loads (`VexillumServer.dll`, `VexillumGame.dll`). Running `dotnet X.dll` is
just another way to start the same program; it is not a build step.
Options: `--root <dir>`, `--connect host:port` (client), `--port n` (server);
`VEXILLUM_LOG_STDOUT=1` echoes `Util.Debug` to stdout, `VEXILLUM_DEBUG_PORT=n`
opens the in-process debug console.

## Why the 2025 Mono attempt (`BUILD_NOTES.md`, `build.sh`) could not work

It swapped the XNA references for `lib/MonoGame/MonoGame.Framework.dll` 3.7.1
but kept `dlls/Nuclex.*.dll`, `dlls/Platform.dll` and `dlls/SlimDX.DirectInput.dll`.
Those assemblies are compiled against the strong-named
`Microsoft.Xna.Framework, PublicKeyToken=842cf8be1de50553`; the binding
redirect in `Game/Game/app.config` cannot map them to an assembly with a
different name and key. Nuclex.Input also needs SlimDX (native DirectInput).
The approach was dead on arrival, and `xbuild` is itself deprecated. The
files were removed in step 12 (they are in git history before that commit).

## Steps

| # | Step | Status | Notes |
|---|---|---|---|
| 0 | Claude environment: `CLAUDE.md`, docs, skills, agents, MCP server | done (2026-09-27) | This commit. |
| 1 | New SDK-style solution `Vexillum.sln` replacing the 2010 one; projects `Lzma`, `Game`, `Server`, `Client` (from `ZombieSurvival/`), `MapTools`, `CreateMap`, `ExtractMap`. Delete `PlatformLinux`, fold `Platform/Vec2.cs` into `Game/util`. Keep old files until the new build is green, then remove. | done (2026-09-27) | `Directory.Build.props` holds the shared settings (`net9.0`, `GenerateAssemblyInfo=false`, NoWarn list). Every legacy `.csproj` was replaced in place with the same file and assembly name; `Platform/PlatformWindows.csproj` is kept as its own project (assembly `Platform`) rather than folded into `Game` so `Vec2.cs` stays untouched. `PlatformLinux/`, the 2025 `Game/Game/app.config` and the content project are out of the solution (the `ZombieSurvivalContent/` folder and assets stay). `Shims/{XnaCompat,Drawing,Steamworks,WinForms,Nuclex}` and `Tests/Vexillum.Tests` (xunit) exist as buildable projects so steps 4-7 only add source files. `ServerStart/ServerStart.csproj` exists but is not in the solution: its Designer code needs `Form`, `Button`, `TextBox`, `ComponentResourceManager`, `Icon`, which the WinForms shim does not aim to provide (see step 9). No `AllowUnsafeBlocks` in `Game` (Awesomium files are fully commented out). Old-solution Xbox/x86 configurations are gone. |
| 2 | Compile `Lzma` and `MapTools` on `net9.0`. Remove the unused `GetJpgEncoder` (only `System.Drawing` use in MapTools). | done (2026-09-27) | `Lzma` builds with `Compress/LzmaAlone/**` excluded from compile (standalone tool with a second `Main`; sources untouched). `GetJpgEncoder` was **kept** rather than removed: `MapTools` references `Shims/Drawing`, whose `System.Drawing.Imaging.ImageFormat` (real GDI+ Guids) and `ImageCodecInfo` (`GetImageEncoders()` returns an empty array) make `MapCreator.cs` compile unchanged. `CreateMap` and `ExtractMap` build too. |
| 2b | Revert the 2025 source edits from commit `c948e3c` (`git checkout 370aa81 -- <file>` for the seven `.cs` files it touched, except keep `SteamworksStub.cs` content as the seed of the Steamworks shim) so the historical files are byte-identical to the author's. | done (2026-09-27) | The seven files equal `370aa81` again; `SteamworksStub.cs` moved (`git mv`) to `Shims/Steamworks/`. `preservation_check`: 0 files differ. |
| 3 | `Shims/XnaCompat`: empty namespaces `Microsoft.Xna.Framework.GamerServices`, `.Storage`, `.Net` so the original `using`s compile; `Game` compiles against MonoGame with the UI, Steam and Drawing files still failing. | done (2026-09-27) | MonoGame 3.8.4 lacks all three namespaces; each gets an `internal static class NamespacePlaceholder`. Zero edits to historical files. `Game` now fails with 132 errors, all rooted in missing shim types: Nuclex 113 (CS0234 x35 `Nuclex.UserInterface.Controls/.Visuals/.Input`, CS0246 x74 `MouseButtons`, `ButtonControl`, `WindowControl`, `LabelControl`, `InputControl`, `IWritable`, `IFocusable`, `Screen`, `GuiManager`, `InputManager`, `Control`, `ListControl`, `OptionControl`, `IOpeningLocator`, `IFlatGuiGraphics`, `IFlatControlRenderer<>`, `RectangleF`; CS0538 x4), System.Drawing 15 (CS1069 `Bitmap` x11, `Font` x2, `Graphics`, `Brush`), System.Windows.Forms 4 (`KeysConverter`, `Message` x2, `IMessageFilter`). Steamworks: 0 (the stub already covers every member used). One vendored file is excluded from compile in `Game.csproj`: `util/misc/IO/NonClosingStreamWrapper.cs` overrides `MarshalByRefObject.CreateObjRef`, a .NET Framework remoting API absent from .NET 9; nothing uses the class and CLAUDE.md rule 20 allows only project-file changes to MiscUtil. `Server`, `VexillumGame` and `Tests` build once `Game` does; a scratch build of `Vexillum.Tests` without the `Game` reference passes its one test. |
| 4 | `Shims/System.Drawing` (see CLAUDE.md rule 16) so `Util`, `Level`, `ClientLevel`, `MenuLevel`, `LevelLoader`, `AssetManager`, `TerrainParticle`, `GraphicsHelper`, `TextRenderer`, `ScrollPanel`, `ChatPanel`, `ServerLevel`, `MapCreator` compile unchanged. `Bitmap` over `StbImageSharp` decode; `Save(stream, ImageFormat.Png)` via a minimal PNG encoder (zlib is in the BCL). | done (2026-09-27) | `Shims/Drawing` now holds `Image` (abstract), `Bitmap`, `PngEncoder`, `Graphics`/`Brush`/`SolidBrush`/`Brushes`/`Font`/`FontStyle` stubs and `Imaging.{BitmapData,ImageLockMode,PixelFormat,ImageFormat,ImageCodecInfo}`; `Color`/`Rectangle`/`PointF`/`Size` stay the framework's (System.Drawing.Primitives). `Bitmap` stores 32bppArgb as B,G,R,A rows (stride `Width*4`); `LockBits` for a 32bpp format pins that buffer and hands out the surface itself (sub-rectangles offset `Scan0`, full stride, like GDI+), 24bpp/PArgb go through a conversion buffer copied back on `UnlockBits`. `MakeTransparent(c)` keys on all four channels (so `Color.Transparent` = A0,FFFFFF leaves opaque JPEG pixels alone), `MakeTransparent()` keys on the bottom-left pixel; `Clone()` is a deep copy returning `object`; `Save(PNG)` writes 8-bit RGBA, filter None, one zlib IDAT, CRCs, stream left at its end (MonoGame's `Texture2D.FromStream` rewinds that case). Decoding via `StbImageSharp` (PNG exact, JPEG from stb's decoder). Every historical file that names `System.Drawing` compiles unchanged: `Game` errors went 132 -> 117, all Nuclex/WinForms; `MapTools`, `CreateMap`, `ExtractMap` build. `Tests/Vexillum.Tests/DrawingShimTests.cs` runs a verbatim copy of the `Level` constructor loop on `bases.map` and `complex.map` and matches the oracle SHA-256 of `ToBytes()`, plus PNG round-trip, `MakeTransparent`, `Clone`, `LockBits` layout and content-file tests (9 tests green through a scratch mirror of the test project without the `Game` reference; `dotnet test Tests/Vexillum.Tests` itself waits for `Game` to compile in step 6). Shim PNGs were also checked with Python PIL: CRCs valid, pixels identical, PNG decode identical to PIL's. |
| 5 | `Shims/Steamworks`: complete the `Steamworks` namespace (`CSteamID`, `HAuthTicket`, `AppId_t`, `Callback<T>`, `GameOverlayActivated_t`, `ValidateAuthTicketResponse_t`, `EAuthSessionResponse`, `EBeginAuthSessionResult`, `SteamAPI`, `SteamUser`, `SteamFriends`, `SteamUtils`, `Packsize.Test()`, `DllCheck.Test()`) as an offline implementation. | done (2026-09-27) | `Shims/Steamworks/SteamworksStub.cs` is the full offline implementation, Steamworks.NET names and signatures throughout (`Packsize.Test()`/`DllCheck.Test()` are methods; `(AppId_t)349380` casts from an int literal; `HAuthTicket`/`CSteamID`/`AppId_t` carry `==`/`!=`; enums carry the real Steam values). Semantics: `SteamAPI.Init()` true, `RestartAppIfNecessary` false, `GetPersonaName()` = `Environment.UserName` (or `Player`), `GetAuthSessionTicket` = `HAuthTicket.Invalid` with length 0 so packet 1 is unchanged, `BeginAuthSession` OK, `GetSteamID()` = SHA-256 of user name + process id (stable per process, distinct per client on one machine; `SteamUser.ComputeOfflineSteamID` exposes the derivation). `Callback<T>` registers with an in-process `CallbackDispatcher`; nothing posts events on its own, `SteamAPI.RunCallbacks()` drains anything posted via `CallbackDispatcher.Post`. Tests: `Tests/Vexillum.Tests/SteamworksShimTests.cs` (7 tests); run with `dotnet test Tests/Vexillum.Tests -p:VexillumTestsNoGame=true` until `Game` is green. `dotnet build Game` shows 0 errors naming a Steamworks type (the 146 remaining are Nuclex/Drawing/WinForms). Not implemented (not needed to compile or run): real overlay/auth callbacks, `SteamUtils` beyond the warning hook. |
| 6 | `Shims/Nuclex`: source port of Nuclex `Support`, `Input`, `UserInterface` to MonoGame (drop DirectInput/SlimDX, `NMock`, unit tests, `System.Windows.Forms` uses inside Nuclex.Input). `InputManager` reads MonoGame `Keyboard`/`Mouse` state and `Window.TextInput`. All original dialogs, `CustomInputControl(+Renderer)` and the Darkness skin then work unchanged. `Awesomium` files stay commented out as they are. | done (2026-09-27) | r1404 sources (GitHub mirror `remiomosowon/NuclexFramework`) vendored under `Shims/Nuclex/{Support,Input,UserInterface}` with `LICENSE-CPL.txt`; every library change is listed in `Shims/Nuclex/NUCLEX-PORT-NOTES.md`. One assembly (`Vexillum.Shims.Nuclex`). PC keyboard/mouse are new `MonoGameKeyboard`/`MonoGameMouse` devices (state diff per `Update()`, characters from `GameWindow.TextInput`, wheel in `delta/120` ticks, `(-1,-1)` on leaving the window); DirectInput, `WindowMessageFilter` and touch mocks dropped, the rest byte-for-byte r1404 (7 small edits, see notes). The embedded Suave default skin was extracted from the shipped DLL so `GuiManager.Initialize()` behaves as before. `Game` now has zero errors naming a Nuclex type (33 left: System.Drawing 29, WinForms 4). `Tests/Vexillum.Tests/NuclexShimTests.cs` (7 tests: UniRectangle layout, mouse press through `DefaultInputCapturer` -> `Pressed` + `IsInputCaptured`, keyboard into `InputControl`, embedded resources, `DarknessUI.xml` validates against `skin.xsd`) passes; it runs once `Game` builds. TODO: keyboard auto-repeat (`KeyPressed` once per physical press now; Windows repeated `WM_KEYDOWN`), and confirm at runtime that the XNA 4.0 Suave `.xnb` files load under MonoGame (guarded by try/catch). |
| 7 | `Shims/System.Windows.Forms`: `MessageBox.Show` (logs + stderr), `KeysConverter`, `IMessageFilter`, `Message`, `Application.AddMessageFilter` (no-op). Original `Program.cs` files then compile; the `#if WINDOWS \|\| XBOX` in the client `Program.cs` is satisfied by defining `WINDOWS` in the new csproj (it is the author's constant, not a platform switch). New `--root/--connect` handling goes in a small `Launcher` wrapper project or a `// PORT:` block, owner's choice. | done (2026-09-27): WinForms shim + entry points | WinForms shim: `Shims/WinForms/{MessageBox,Application,KeysConverter,Controls}.cs`. `MessageBox.Show(text[, caption[, buttons]])` writes `[MessageBox] caption: text` to stderr and `Debug`, returns `DialogResult.OK`, never blocks. `Application.AddMessageFilter` keeps the filter in a list that nothing pumps, so `KeyboardMessageFilter.PreFilterMessage` (and its `user32` `TranslateMessage` DllImport) is never invoked; WM_CHAR text input must come from MonoGame `Window.TextInput` (step 6). `Application.Run(Form)` throws `NotSupportedException`. `Controls.cs` stubs `Control`/`ContainerControl`/`Form`/`TextBox`/`Button`/`Label` (over `System.ComponentModel.Component`, so `Dispose(bool)` and `IContainer` come from .NET) for `ServerStart`'s Designer code; `HostServerForm*.cs` and `Program.cs` compile against it except for one `System.Drawing.Icon` cast, which step 4 must provide. Tests: `Tests/Vexillum.Tests/WinFormsShimTests.cs`. Entry points: Neither `Program.cs` is edited. `ZombieSurvival/PortProgram.cs` (`<StartupObject>Vexillum.PortProgram`) and `Server/PortProgram.cs` (`<StartupObject>Server.PortProgram`) parse `--root <dir>` (chdir first), `--connect <host>:<port>` / `--port <n>`, `--help`, ignore unknown arguments, and invoke the author's non-public `Program.Main` through reflection, unwrapping `TargetInvocationException` and rethrowing with `ExceptionDispatchInfo` so the author's `UnhandledException` handler still runs and the process exits non-zero (verified on .NET 9: exit code 134 for main-thread and background-thread crashes). `VEXILLUM_LOG_STDOUT=1` adds a `ConsoleTraceListener` to `Trace.Listeners`, which verified empirically routes `Debug.Print` to stdout on .NET 9 (Debug builds only: `Debug.Print` is `[Conditional("DEBUG")]` and the SDK defines `DEBUG` for `-c Debug`; Release keeps only `debug_client.log`). No `// PORT:` edit in `Util.Debug` was needed. `--connect` starts a background thread that polls every 250 ms (60 s max) for `Vexillum.game.View is MainMenuView` with the menu visible (that assignment is the last thing `LoadContent` does) and then calls `game.ConnectWhenServerReady(host, port, 40)` on that thread, the same call `Client.cs` makes from its reader thread on packet 253. Two clients in one `--root` work because `Util.OpenLockFile` swallows the sharing violation. If `Program.Main` returns without creating the game (`SteamManager.Initialize()` false) the client exits 1 with a stderr line. Server: `Program.Main` returns after starting the threads; foreground threads keep a .NET 9 process alive (verified), and `PortProgram` additionally joins `Server.stepThread` (via reflection) so the exit is explicit, then flushes the log. Both files were compiled against stubs of the touched members (`Program`, `Util`, `Vexillum`, `AbstractView`, `MainMenuView`, `Menu.visible`, `Server.running`/`stepThread`); the real build waits on steps 4-6. |
| 8 | Content: keep `.xnb` fonts/sounds. `Blur.xnb` is XNA DX9 bytecode and cannot load; MonoGame's `mgfxc` needs Wine, which is banned. Reproduce the blur without a shader: `Blur.fx` computes `(tex(uv) + tex(uv + d)) / 2`, so draw the render target twice at half alpha, the second copy offset by `d * targetSize`, with an additive blend, inside the MonoGame `Effect` shim (`Shims/XnaCompat/Effect`) or a `// PORT:` edit in `GameView.LoadShaders`/`DrawStuff`. Content name `Blur` stays. | done (2026-09-27), zero edits to `GameView.cs` | The author's `Content.Load<Effect>("Blur")` now loads the shipped `Test/Content/Blur.xnb` unchanged. `ContentTypeReaderManager.LoadAssetReaders` looks the XNB's reader string up in its type-creator table *before* resolving the type, so `Vexillum.Port.XnaEffectContent.Register()` (`Shims/XnaCompat/Content/XnaEffectContent.cs`, called by `PortProgram` before `Program.Main`) registers a creator for the XNA string `Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, ...`. That reader skips the DX9 bytecode and returns `new Effect(device, BlurMgfx.Bytes)`, an MGFX v10 / OpenGL-profile blob assembled in code (`BinaryWriter`, layout from `Effect.ReadEffect` and `Shader(BinaryReader)` in 3.8.4): on DesktopGL an MGFX shader is GLSL *source*, so no `mgfxc` and no Wine. The pixel shader is `Blur.fx` transcribed to GLSL (`(tex(uv) + tex(uv + d)) / 2`, alpha from `tex(uv)`), with the uniform/varying names MonoGame's own `SpriteEffect` vertex shader uses (`vTexCoord0`, `vFrontColor`, `ps_s0`, `ps_uniforms_vec4[]`), because `GameView.DrawStuff` applies the pass inside an Immediate-mode `SpriteBatch` (the XNA idiom), and MonoGame's `SpriteBatch` behaves the same way (verified in the decompiled 3.8.4 `SpriteBatch`/`SpriteBatcher`/`EffectPass`). `Parameters["d"]` is a `Vector`/`Single` 1x2 parameter, technique `Desaturate`, pass `Pass1`. Verified on this Mac with a scratch MonoGame app that loaded the real `Blur.xnb` through the reader and ran the exact `DrawStuff` sequence into a render target: `d = 0` gives 529200/529200 pixels identical to the source, non-zero `d` gives the point-sampled shifted average with max error 1/255 (rounding of `.5`), alpha stays 255, and a plain batch afterwards draws normally. The two-draw fallback is documented in the open TODOs with the corrected tint/blend maths in case a driver rejects the GLSL. |
| 9 | Server on `net9.0`: no graphics device; `Util.IsServer` paths already skip texture loads. `ServerStart` compiles against the WinForms shim but is not shipped; default `settings.txt` creation moves to launcher code. `Heartbeat`/`HttpGet` must time out quietly (`HttpWebRequest` still exists in .NET 9, obsolete but functional; `Timeout = 10000` is already set). | done (2026-09-27) | `Server/PortProgram.cs` creates `Server/settings.txt` from a verbatim copy of `HostServerForm.defaultConfig` when it is missing and rewrites only the `port` line when `--port` differs from the file (the MCP tools pass 24224, which matches the checked-in `Test/Server/settings.txt`, so it is not modified). `ServerStart` stays out of the solution. Verified at integration (2026-09-27) with the MCP `run_server` tool from `Test/`: `PortProgram: cwd=.../Test`, `Loading level...`, `Ready for connections`, then the master-server heartbeat fails with a DNS `WebException` that the author's own `catch` writes to `Server/debug_server.log` (`Error contacting the master server`); no graphics device, no window, clean SIGTERM exit. |
| 10 | Runtime directory: `dotnet run` for client and server must use `Test/` (or a copy) as working directory; document in the run skill. Add `Content/`, `Maps/`, `Server/` copy-to-output or a `--root` argument. | done (2026-09-27) | `--root <dir>` on both entry points (`Directory.SetCurrentDirectory` before anything else; default: current directory). One MonoGame difference had to be handled: `ContentManager` resolves `Content.RootDirectory` against `TitleContainer.Location` = the executable's directory (on macOS `../Resources` of it first), not the current directory, so with `dotnet VexillumGame.dll --root Test` every `Content.Load` would look in `bin/Debug/net9.0/Content`. `Vexillum.Port.RuntimeDirectory.UseCurrentDirectoryForContent()` (`Shims/XnaCompat/Content/RuntimeDirectory.cs`) sets the internal `TitleContainer.Location` property to the current directory by reflection (verified on 3.8.4; the client logs a warning if the property is gone) so `Content/`, `Maps/`, `Server/`, `settings.xml`, `controls.xml` and the Nuclex skin all resolve under `--root`. No copy-to-output; `Test/` stays the single runtime directory. |
| 11 | Smoke test on loopback: server up, client connects with the random identity, reaches packet 9, moves, fires, terrain deforms, disconnect clean. Automate through the MCP `smoke_test` tool. | done (2026-09-27) | `smoke_test seconds=30 clients=2` starts the server and two clients in `Test/` with `VEXILLUM_LOG_STDOUT=1`; the oracle requires two remote logins on the server, `Set terrain state` on every client and entity-create broadcasts (`Create:N@frame`) shared across both client logs, with no `Disconnected`. Final run: `SMOKE: PASS clients=2 ... logins=2 terrain=2/2 shared entity creates across clients=15` (see "Final results" below). The visual check of the level view (blur, Nuclex skin, HUD) was done from the smoke screenshots and passed. |
| 12 | Cleanup: remove `lib/MonoGame`, `dlls/`, `BUILD_NOTES.md`, `build.sh`, `Icons.res` references, `.DS_Store` files; update `README.md` build section. | done (2026-09-27) | `lib/`, `build.sh`, `BUILD_NOTES.md` removed from git; `dlls/` (never tracked, ignored by `*.dll`) deleted from disk; the seven tracked `.DS_Store` files outside `Test/` untracked and deleted; `.gitignore` covers `bin/`, `obj/`, `.DS_Store`, `Test/lock`, `Test/debug_*.log`, `Test/Server/debug_*.log`, `.claude/settings.local.json`; no `bin/`/`obj/` path is tracked. `Icons.res`/`Icons.rct`/`icon1.ico` stay (author's icons; no project references them). `Test/` untouched: `Test/*.exe` remain the reference build, and the tracked `Test/.DS_Store` and `Test/lock` are left as they are (owner's call). `README.md` gained a "Building today (2026)" section under the author's text. A full rebuild after the removal confirms nothing referenced the deleted files. |

## Open TODOs left by step 1-3 (2026-09-27)

* `Shims/Drawing` (step 4 done): defining `System.Drawing.Bitmap`/`Brush`/
  `SolidBrush` and `System.Drawing.Imaging.*` in the shim assembly coexists
  with the framework's `Color`, `Rectangle`, `PointF` (System.Drawing.Primitives)
  with no ambiguity. Left for later: `Save` only encodes PNG (`ImageFormat.Jpeg`
  throws `NotSupportedException`; nothing in the game saves JPEG), `Graphics`
  draws nothing, `LockBits` supports the 32bpp formats and 24bppRgb only, and
  JPEG texels come from stb's decoder rather than GDI+'s (backgrounds may
  differ by a rounding step; the collision PNGs, which are what gameplay reads,
  decode exactly). GDI+'s `MakeTransparent` also round-trips pixels through
  premultiplied compositing, which can nudge semi-transparent RGB by one; the
  shim leaves those pixels untouched.
* `ServerStart` is outside `Vexillum.sln` (step 1 notes). The WinForms shim
  (step 7) now covers everything its Designer code uses except
  `System.Drawing.Icon`. To add it back to the solution: (a) define `Icon` in
  `Shims/Drawing` (step 4) and change `Form.Icon` in
  `Shims/WinForms/Controls.cs` from `object` to `Icon`; (b) add the
  `System.Configuration.ConfigurationManager` package to
  `ServerStart.csproj` for `Properties/Settings.Designer.cs`
  (`ApplicationSettingsBase`); (c) check that `HostServerForm.resx` (embeds
  a serialized `System.Drawing.Icon`) still passes `GenerateResource`, or
  exclude the `.resx` files in the csproj. Even then `Application.Run`
  throws: the form is compiled for preservation, not shipped; the default
  `settings.txt` creation moves to launcher code (step 9).
* `Lzma/Compress/LzmaAlone/LzmaAlone.csproj` (legacy, standalone tool) is
  left in place, unbuilt (still there after step 12; delete it if nobody
  wants the CLI).
* `ZombieSurvival/Vexillum.csproj` drops the author's `Win32Resource`
  (absolute `C:\Users\Jacob\...` path to `Icons.res`); an `ApplicationIcon`
  from `ZombieSurvival/Game.ico` can be added later.
* `dlls/`, `lib/`, `build.sh`, `BUILD_NOTES.md` were unreferenced by any
  project or solution file and were deleted in step 12. The MCP `decompile`
  tool can no longer read the old `dlls/Nuclex.*.dll`; the Nuclex r1404
  sources under `Shims/Nuclex/` and `Shims/Nuclex/NUCLEX-PORT-NOTES.md` are
  the reference now.

## Open TODOs left by steps 7, 8, 10 (2026-09-27) — for the integration agent

* **Build (done at integration, 2026-09-27)**: after merging steps 4-7
  (`Drawing`, `Steamworks`, `WinForms`, `Nuclex`, entry points) into
  `port/monogame`, `dotnet build Vexillum.sln` reports 0 errors and 0 warnings
  for all 14 projects (`Lzma`, `MapTools`, `CreateMap`, `ExtractMap`,
  `PlatformWindows`, five `Shims/*`, `Game`, `Server`, `Vexillum` client,
  `Vexillum.Tests`). Exactly one historical line had to change, because no shim
  can reach it: `Game/Game/Vexillum.cs:98` subscribes `OnExit` with
  `new EventHandler<ExitingEventArgs>(OnExit)` (`// PORT:`), since MonoGame's
  `Game.Exiting` is `EventHandler<ExitingEventArgs>` and .NET Core's
  `EventHandler<T>` lost the `in` contravariance that XNA/.NET 4.5 had (a
  scratch program confirmed `EventHandler<EventArgs>` -> `EventHandler<X>` is
  CS0029 on net9.0). Two non-historical fixes: `ZombieSurvival/PortProgram.cs`
  needed `using Vexillum.util;` for `VexillumConstants`, and
  `WinFormsShimTests.cs` needed `global::Vexillum.util` because `Vexillum`
  inside `namespace Vexillum.Tests` binds to the class. `dotnet test
  Tests/Vexillum.Tests`: 31/31 pass with the `Game` reference, including the
  new `LevelTerrainTests.cs`, which loads `bases.map` and `complex.map` with
  the real `LevelLoader.LoadData` (`Util.IsServer = true`, cwd = `Test/`),
  runs the real `Level` constructor through a minimal concrete subclass and
  matches the oracle SHA-256 of `GetTerrainState()` for both maps
  (`059ee417...` and `d5d14f4c...`), then round-trips `SetTerrainState`.
  `invariant_check` OK (32 checks), `preservation_check` reports only the one
  `// PORT:` line plus the two `PortProgram.cs` additions. The runtime checks
  (`run_client --connect`, `smoke_test` with two clients, visual check of the
  blur and the Nuclex skin) were done in step 11.
* **Steam gate**: the author's client `Main` returns (and `PortProgram` exits
  1 with "Vexillum did not start") unless `SteamManager.Initialize()` is true.
  The current `Shims/Steamworks` stub has `SteamAPI.Init()` returning `false`
  and `Packsize.Test`/`DllCheck.Test` as fields; step 5 must make `Init()`
  return `true` offline (CLAUDE.md rule 17) before the client can start.
* **Runtime checks** (`run_server`, `run_client`, `smoke_test`):
  * server: `PortProgram: cwd=.../Test port=24224` then `Ready for
    connections`; Ctrl-C/SIGTERM path: the author's `CancelKeyPress` handler
    calls `server.Stop()`, which ends the stepping thread and the join.
  * client without `--connect`: main menu, debug lines on stdout under
    `VEXILLUM_LOG_STDOUT=1` (Debug build).
  * client with `--connect 127.0.0.1:24224`: log shows `PortProgram: main
    menu ready after N ms, connecting to ...`, then the usual `Connecting
    to`, `Set terrain state`. If the menu never becomes visible (e.g. the
    Nuclex port changes when `Menu.Show()` runs), the thread gives up after
    60 s and logs it; relax the condition to `View is MainMenuView` then.
  * blur: the level view must look identical to the reference build and
    shake on explosions. If `Content.Load<Effect>("Blur")` throws
    `ContentLoadException` or `InvalidOperationException("Shader Compilation
    Failed")`, the GLSL in `BlurMgfx.PixelShaderGlsl` was rejected by that
    driver; compare with the SpriteEffect GLSL that
    `scratch/parse_mgfx.py`-style dumping of the embedded MGFX shows and
    adjust. Only then fall back to the two-draw `// PORT:` edit in
    `GameView.DrawStuff` with these corrected maths (the skill text's
    `White * 0.5f` + `Additive` is wrong on MonoGame: `Color * float` also
    scales alpha and `Additive` uses `SourceAlpha`, giving 0.25x): keep the
    existing NonPremultiplied batch, draw `shaderTarget` at (0,0) with
    `Color.White`, then draw it again at `(-d.X * width, -d.Y * height)` with
    `new Color(255, 255, 255, 128)`; at `d = 0` that is exactly the source
    (`0.5x + 0.5x`), otherwise the shifted average within 1/255.
* **RenderTargetUsage**: `GameView.CloneRenderTarget` keeps the default
  `DiscardContents`, which MonoGame implements as a clear to
  `GraphicsDevice.DiscardColor` on `SetRenderTarget` (decompiled
  `ApplyRenderTargets`). No `// PORT:` edit: `ClientLevel.Draw` covers the
  whole 840x630 target every frame (both shipped maps carry an 840x630
  `sky.jpg` drawn twice at `xOffset` and `xOffset - 840`, then main/borders),
  so the clear is always overwritten, and XNA's `DiscardContents` had the
  same contract. If a purple frame ever shows (a map without `sky`, or the
  camera outside the level), add `false, SurfaceFormat.Color,
  DepthFormat.None, 0, RenderTargetUsage.PreserveContents` to that
  constructor as a marked one-line edit. `ScrollPanel` (step 6) creates its
  own targets; check it the same way.
* **Release builds** do not echo `Util.Debug` to stdout (`Debug.Print` is
  compiled out); the MCP tools build Debug, so nothing to do unless the
  smoke test moves to Release, in which case the fallback is the one-line
  `// PORT:` `Console.WriteLine` in `Util.Debug`.
* `Trace.Listeners.Add(new ConsoleTraceListener())` also echoes any
  `Debug.WriteLine` from MonoGame itself; harmless, but grep the smoke log
  for `Ready for connections`/`Set terrain state`, not for line counts.

## Dependencies as imports (added 2026-09-27, after the port)

No third-party binaries or vendored sources remain. `Lzma/` (7-Zip C# SDK
copy) became the `LZMA-SDK` 22.1.1 package plus the game's 90-line
`SevenZipHelper.cs` under `Shims/Lzma/`; `Game/Game/util/misc/` (Jon Skeet's
MiscUtil) became the `JTForks.MiscUtil` 1.285.0 package (netstandard, same
code: `EndianBinaryReader.ReadString`/`Write(string)` verified identical, so
the wire format is unchanged). Versions live in `Directory.Packages.props`,
resolved graphs in `packages.lock.json` per project, the feed in
`nuget.config`; `dotnet restore` (implicit in `make`) fetches everything.
Packages in use: MonoGame.Framework.DesktopGL, StbImageSharp, LZMA-SDK,
JTForks.MiscUtil, Microsoft.CodeAnalysis.CSharp.Scripting (debug console),
xunit (tests).

## Master server replacement (added 2026-09-27, after the port)

`Shims/MasterServer` (assembly `Vexillum.Port.MasterServer`) replaces the
dead playvexillum.com scripts; see `docs/ARCHITECTURE.md` "Web services".
Historical edits: two `// PORT:` lines at the top of `Util.HttpPost` and
`Util.HttpGet` (Game/Game/Util.cs:50 and :83) that route the request to
`MasterServer.TryHandle`; `Vexillum.Server` and the rest of both methods are
untouched and still used for any other path. Verified: server heartbeat
visible on ntfy.sh, LAN beacon picked up by a client on the same machine,
the author's Server List dialog lists `[LAN] Vexillum Server`.

## Debug host (added 2026-09-27, after the port)

`Shims/DebugHost` (assembly `Vexillum.Port.DebugHost`) is dev tooling, not
part of the game: when the launcher sees `VEXILLUM_DEBUG_PORT=<n>` it opens a
localhost TCP console that evaluates C# with Roslyn scripting inside the
process, with `Sync` to run on the game thread and reflection helpers for
private state. `.claude/mcp/vexillum_dev.py` drives it (`proc_start`,
`eval`, `probe`). No historical file changed for it; both `PortProgram.cs`
files gained the env-var check.

## Decisions (settled 2026-09-27 by the preservation rule)

* **UI:** port Nuclex from source (option 1). Myra or hand-rolled dialogs
  would mean rewriting the author's UI files, which preservation forbids.
* **Images:** `StbImageSharp` (public domain, decode only) inside the
  `System.Drawing` shim; PNG encode for `Bitmap.Save` via a minimal
  encoder over `System.IO.Compression.ZLibStream`. ImageSharp is not needed.
* **Shader:** no shader compiler at all. The blur runs as a real pixel shader
  after all: on DesktopGL an MGFX effect carries GLSL source, so the blob is
  assembled in code and served for the shipped `Blur.xnb` through a
  registered content reader (step 8); the two-draw fallback is kept in the
  open TODOs. If MonoGame ever ships a Wine-free effect compiler, the
  original `Blur.fx` can be compiled again and the hand-assembled blob removed.
* **Steam:** the `Steamworks` namespace is shimmed offline; the names match
  Steamworks.NET so a real integration can return later without edits.

## Hazard inventory (2026-09-27, refreshed at finalization after step 12)

Run the `port_audit` MCP tool for the live list. After steps 1-12 every
hazard class **resolves to a shim or the source port**; the audit counts
references, not failures. Final summary: `PORT AUDIT: 237 source hazards in
54 files; 3 project-file hazards` (unchanged by the cleanup: the three
project-file hazards are all `ZombieSurvivalContent/VexillumContent.contentproj`,
the unbuilt XNA content project, kept as content source; the `dlls/` and
`lib/MonoGame` `HintPath` classes are at zero and the folders are gone; the
count rose from 189 earlier because the `Tests/Vexillum.Tests/*.cs` files
name the shimmed namespaces on purpose).

* `System.Drawing` (18 files, 72 hits -> `Shims/Drawing`): Util, GraphicsHelper,
  Level, LevelLoader, ClientLevel, MenuLevel, ScrollPanel, ChatPanel,
  net/Client (one `Color.Red`), TextRenderer, view/AssetManager,
  view/TerrainParticle, Server/ServerLevel, MapTool/MapCreator, ServerStart/*
  (not built), tests.
* `System.Windows.Forms` (8 files, 18 hits -> `Shims/WinForms`): Vexillum
  (MessageBox, message filter), util/KeyboardMessageFilter,
  ui/KeySelectorControl (`KeysConverter`), ZombieSurvival/Program, ServerStart/*
  (not built), tests.
* `Nuclex` (25 files, 85 hits -> `Shims/Nuclex` source port): Vexillum, Util,
  LocalPlayer, all `ui/*Dialog`, ui/Menu, ui/CustomInputControl(+Renderer),
  ui/KeySelectorControl, view/AbstractView, view/GameView, view/MenuView,
  view/MultiplayerView, Entities/Weapons/{Weapon,SMG,RocketLauncher,
  ClusterBombLauncher} (`MouseButtons` only), tests.
* `Steamworks` (9 files, 52 hits -> `Shims/Steamworks`, offline): Vexillum,
  steam/*, net/Client, ui/MainMenu, game/Identity, Server/ServerSteamAPI,
  Server/ServerPlayer, tests.
* `Awesomium` + `unsafe`: ui/XNASurface, ui/WebControl, ui/WebControlRenderer
  (all fully commented out already).
* XNA namespaces gone in MonoGame (-> `Shims/XnaCompat`): `GamerServices`
  (Vexillum, three weapons, ZombieSurvival), `Storage` (ControlSystem),
  `Media` (harmless, exists in MonoGame).
* `DllImport`: util/KeyboardMessageFilter (inert: the WinForms shim never pumps
  message filters).
* `Process.Start` of sibling exes: ui/ServerDialog, ServerStart/HostServerForm
  (runtime concern only; TODO for step 11/12).
* Hard-coded paths: the author's `Win32Resource` (`C:\Users\Jacob\...`) is
  not carried into the new csproj; the content project references
  `..\..\ExEnFontShim` and a Nuclex binaries folder outside the repo.

### `// PORT:` edits to historical files (complete list)

| File:line | Change | Reason |
|---|---|---|
| `Game/Game/Vexillum.cs:98` | `new EventHandler<EventArgs>(OnExit)` -> `new EventHandler<ExitingEventArgs>(OnExit)` | MonoGame's `Game.Exiting` is `EventHandler<ExitingEventArgs>`; .NET Core's `EventHandler<T>` is not contravariant, so the XNA-era delegate object cannot convert and no shim can intercept an event on MonoGame's own `Game` class. `OnExit(object, EventArgs)` itself is unchanged. |

New non-historical files inside historical directories (flagged by
`preservation_check` as ADDED, not edits): `Server/PortProgram.cs`,
`ZombieSurvival/PortProgram.cs` (step 7 entry points).

## Final results (2026-09-27, port/monogame after step 12)

All gates were run on the finished tree (macOS 26, M4 arm64, .NET SDK 9.0.305,
MonoGame 3.8.4 DesktopGL) after the cleanup, in this order.

**Build** (`python3 .claude/mcp/vexillum_dev.py build target=Vexillum.sln`):

```
BUILD: SUCCESS (exit 0) target=Vexillum.sln config=Debug
errors=0 warnings=0
```

**Tests** (`dotnet test Vexillum.sln`):

```
Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 1 s - Vexillum.Tests.dll (net9.0)
```

**Invariants** (`invariant_check`): `INVARIANTS: OK`, 32 checks OK
(`StreamHelper.entityTypes` order, `PlayerClass`/`KeyAction`/`Sounds` enum
order, `FRAME_RATE`, `PROTOCOL_VERSION`, `DEFAULT_PORT`, `MAX_PING`, map magic
in `LevelLoader.cs` and `MapUtil.cs`, `GameWidth`/`GameHeight`/`Scale`,
`Gravity`, `friction`, `TerrainArray.SetShort0`, little-endian converters in
`Client.cs`/`ServerPlayer.cs`, `GameModeCommand` values, and a parameterless
constructor on all 13 entity/weapon types).

**Preservation** (`preservation_check`):

```
PRESERVATION vs 370aa81: 0 file(s) with unmarked edits or deletions, 3 file(s) differ
OK        Game/Game/Vexillum.cs: +1 -1 lines, 0 added lines without '// PORT:'
ADDED     Server/PortProgram.cs (new file in a historical dir; should it live in Shims/?)
ADDED     ZombieSurvival/PortProgram.cs (new file in a historical dir; should it live in Shims/?)
```

Complete list of `// PORT:` lines in historical files (one):

```
Game/Game/Vexillum.cs:98
-            this.Exiting += new EventHandler<EventArgs>(OnExit);
+            this.Exiting += new EventHandler<ExitingEventArgs>(OnExit); // PORT: MonoGame's Game.Exiting is EventHandler<ExitingEventArgs>; .NET Core's EventHandler<T> is not contravariant
```

**Port audit** (`port_audit`): `PORT AUDIT: 237 source hazards in 54 files;
3 project-file hazards`, `Shims project present: True`. Every source hazard
class (System.Drawing 18 files/72, System.Windows.Forms 8/18, Nuclex 25/85,
Steamworks 9/52, DllImport 1/1, XNA GamerServices 5/5, XNA Storage 1/1,
`#if WINDOWS` 1/1, Process.Start 2/2) resolves to a `Shims/` project or is
inert; the three project-file hazards are the unbuilt
`ZombieSurvivalContent/VexillumContent.contentproj`.

**Smoke test** (`smoke_test seconds=30 clients=2`):

```
SMOKE: PASS
clients=2 ['terminated (SIGTERM), exit 143', 'terminated (SIGTERM), exit 143']; server terminated (SIGTERM), exit 143; screenshot: /tmp/vexillum-dev/smoke-1790507420.png
logins=2 terrain=2/2 shared entity creates across clients=15
```

Both clients logged `PortProgram: main menu ready after 1000 ms, connecting
to 127.0.0.1:24224`, `Connecting to 127.0.0.1:24224`, `Set terrain state
(4468 bytes)` and the same `Create:11@578 ... Create:25@702` entity
broadcasts (15 shared creates in the final run); the server logged two
`Connection from 127.0.0.1` logins and clean disconnects.

**Terrain oracle**: `LevelTerrainTests` and `DrawingShimTests` match the
independent Python SHA-256 of `TerrainArray.ToBytes()` for both shipped maps
(`bases.map` `059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3`,
`complex.map` `d5d14f4c61b455f074f82222ad6da7a5a491bf4ddcd53bde8056c3f2974695b6`).

### What was shimmed (where to look)

| Original dependency | Provided by | Notes |
|---|---|---|
| XNA 4.0 (`Microsoft.Xna.Framework.*`) | MonoGame 3.8.4 DesktopGL (NuGet) | real framework |
| `Microsoft.Xna.Framework.GamerServices`, `.Storage`, `.Net` | `Shims/XnaCompat` | empty namespaces so the author's `using`s compile; also the `Blur.xnb` XNA-effect reader (GLSL assembled in code, no `mgfxc`) and the `--root` content-directory fix |
| Nuclex `Support`/`Input`/`UserInterface` | `Shims/Nuclex` (r1404 source port, CPL) | DirectInput/SlimDX, NMock, tests dropped; MonoGame keyboard/mouse devices; `NUCLEX-PORT-NOTES.md` lists every library change |
| Steamworks.NET | `Shims/Steamworks` | offline: `Init()` true, persona = user name, stable per-process `CSteamID`, invalid auth ticket (packet 1 unchanged) |
| `System.Drawing` (GDI+) | `Shims/Drawing` | `Bitmap` over StbImageSharp, `LockBits` with GDI+ stride semantics, PNG encoder, stub `Graphics`/`Font`/`Brush` |
| `System.Windows.Forms` | `Shims/WinForms` | `MessageBox` to stderr+log, `KeysConverter`, inert message filters, Designer stubs for `ServerStart` |
| `Platform.dll` (`Vec2`) | `Platform/PlatformWindows.csproj` rebuilt on net9.0 | source untouched |
| LZMA SDK | `Lzma/Lzma.csproj` on net9.0 | `LzmaAlone` excluded |

### What is left (not required to compile or run)

* **`ServerStart` GUI** (`ServerStart/`, WinForms host-server form): compiles
  against the WinForms shim except `System.Drawing.Icon`, stays out of
  `Vexillum.sln`, and `Application.Run` would throw. Its one job (writing a
  default `Server/settings.txt`) is done by `Server/PortProgram.cs`. See the
  step 1-3 TODO above for the three things needed to add it back.
* **Lobby / master server / web services**: `Util.HttpGet` heartbeat to
  `playvexillum.com`, the server list in `ui/ServerDialog.cs`, bug reports in
  `ui/ReportBugDialog.cs` and the login flow in `ui/LoginDialog.cs` all fail
  quietly to the log (10 s `HttpWebRequest` timeout, author's `catch`). Direct
  IP join (`ui/IPJoinDialog.cs`, `--connect`) is the way in. `ServerDialog`'s
  `Process.Start` of a sibling `.exe` does nothing useful on macOS/Linux.
* **Steam**: the shim is offline only (no overlay, auth tickets, friends).
  Names match Steamworks.NET, so the real package can be swapped in later
  without touching the author's files.
* **Nuclex keyboard auto-repeat**: `KeyPressed` fires once per physical press
  (Windows repeated `WM_KEYDOWN`); holding Backspace in a text box deletes one
  character. `Shims/Nuclex/Input/Devices/MonoGameKeyboard.cs`.
* **Content pipeline**: the 2013 `.xnb` fonts/sounds/skin from `Test/Content`
  are loaded as they are; `ZombieSurvivalContent/` is source only. New assets
  need MGCB (see the `content-pipeline` skill). `Blur.fx` is served through the
  hand-assembled GLSL blob; if MonoGame ever ships a Wine-free effect
  compiler, compile `Blur.fx` and delete `BlurMgfx`.
* **Release builds** do not echo `Util.Debug` to stdout (`Debug.Print` is
  compiled out); the tools use Debug.
* **Housekeeping the owner may want**: `Test/.DS_Store` and `Test/lock` are
  still tracked (left because `Test/` is never touched by the port);
  `Lzma/Compress/LzmaAlone/` is unbuilt legacy; an `ApplicationIcon` from
  `ZombieSurvival/Game.ico` or `icon1.ico` could replace the author's
  `Win32Resource` `Icons.res`; `Server/PortProgram.cs` and
  `ZombieSurvival/PortProgram.cs` could move to a `Launcher/` project if the
  owner prefers historical directories to hold only historical files.
* **Known original bugs** (next section) are deliberately unfixed.

## Known original bugs (do not fix silently; log here and fix deliberately)

* `Util.EscapeUriString` builds `sb` then returns the unescaped `value`.
* `ServerPlayer.SetMovement` compares `movement[2]` against `direction` and
  `movement[3]` against `jumping` (indices shifted by one) before assigning
  correctly; the net effect is that `movementChanged` fires too often.
* `Server.Chat` `/kick` and `/ban` compare `p.name` (the issuer) instead of
  `other.name`, so they only ever hit the issuer. The command also splits on
  spaces, so a name containing a space (every offline identity, which is
  `<random> <persona>`) can never be named at all; the e2e test
  `test_disconnect_by_server_shows_the_reason_and_returns_to_the_menu`
  renames its client to a one-word op name for that reason.
* `ServerDialog.ShowHostDialog` checks for `./ops.txt` but writes
  `Server/ops.txt`, so on a platform where `VexillumServerStart.exe` starts
  it rewrites the ops file with the current username on every press (found
  while writing `Tests/e2e/test_main_menu.py`; here `Process.Start` throws
  first, so nothing is written).
* `Server.IsFull` uses `>` so `maxPlayers + 1` players can join.
* `Server.UpdateBots` loops forever on the `Server Main` thread when
  `RemoveBot` has to remove a bot but finds none of the class it picks
  (`numBlue > numGreen ? Blue : Green`, computed from counts that still
  include the joining/leaving human): every `AddTask` then stalls and the
  server stops answering, although the acceptor thread keeps accepting.
  Reproduced by `maxbots 0` with one human joining (`maxAllowedBots` = -1
  and there is no bot to remove) and by `maxbots 2` when a second human
  joins while the single bot is on the other team. Found by the acceptance
  harness (Tests/Vexillum.Acceptance, `KnownServerBugs`); tests keep
  `maxbots` at the default 6 or use `maxbots 1` for single-client runs.
* `Vexillum.BeginSpriteBatch(Effect)` ignores the effect parameter (see
  ARCHITECTURE.md rendering notes). Behaviour depends on Immediate mode.
* `Level.Explode(int,int,int,Player,Weapon)` seeds `Random` with
  `DateTime.Now.Millisecond`, so client-initiated explosions are not
  deterministic across peers (server-initiated ones send the seed).
* `TextRenderer.DrawFormattedString` draws one `DrawString` per character.
  Slow but it defines the exact kerning the UI was laid out for.
* `HumanoidEntity.Health` setter calls `Level.OnEntityDeath` while `Level`
  can be null during construction (`SetType` sets `Health` before the entity
  is added). Works today because `Health = MaxHealth` is nonzero.
