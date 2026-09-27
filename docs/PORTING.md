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
  the blur is reproduced without a shader (step 8).

## Why the 2025 Mono attempt (`BUILD_NOTES.md`, `build.sh`) cannot work

It swapped the XNA references for `lib/MonoGame/MonoGame.Framework.dll` 3.7.1
but kept `dlls/Nuclex.*.dll`, `dlls/Platform.dll` and `dlls/SlimDX.DirectInput.dll`.
Those assemblies are compiled against the strong-named
`Microsoft.Xna.Framework, PublicKeyToken=842cf8be1de50553`; the binding
redirect in `Game/Game/app.config` cannot map them to an assembly with a
different name and key. Nuclex.Input also needs SlimDX (native DirectInput).
The approach is dead on arrival, and `xbuild` is itself deprecated. Do not
extend it; replace it.

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
| 9 | Server on `net9.0`: no graphics device; `Util.IsServer` paths already skip texture loads. `ServerStart` compiles against the WinForms shim but is not shipped; default `settings.txt` creation moves to launcher code. `Heartbeat`/`HttpGet` must time out quietly (`HttpWebRequest` still exists in .NET 9, obsolete but functional; `Timeout = 10000` is already set). | startup part done (2026-09-27); headless run still to verify | `Server/PortProgram.cs` creates `Server/settings.txt` from a verbatim copy of `HostServerForm.defaultConfig` when it is missing and rewrites only the `port` line when `--port` differs from the file (the MCP tools pass 24224, which matches the checked-in `Test/Server/settings.txt`, so it is not modified). `ServerStart` stays out of the solution. Still to verify once `Game` builds: `Server` runs with no graphics device (`Util.IsServer` paths), `Heartbeat.Send` fails quietly offline, `Ready for connections` appears. |
| 10 | Runtime directory: `dotnet run` for client and server must use `Test/` (or a copy) as working directory; document in the run skill. Add `Content/`, `Maps/`, `Server/` copy-to-output or a `--root` argument. | done (2026-09-27) | `--root <dir>` on both entry points (`Directory.SetCurrentDirectory` before anything else; default: current directory). One MonoGame difference had to be handled: `ContentManager` resolves `Content.RootDirectory` against `TitleContainer.Location` = the executable's directory (on macOS `../Resources` of it first), not the current directory, so with `dotnet VexillumGame.dll --root Test` every `Content.Load` would look in `bin/Debug/net9.0/Content`. `Vexillum.Port.RuntimeDirectory.UseCurrentDirectoryForContent()` (`Shims/XnaCompat/Content/RuntimeDirectory.cs`) sets the internal `TitleContainer.Location` property to the current directory by reflection (verified on 3.8.4; the client logs a warning if the property is gone) so `Content/`, `Maps/`, `Server/`, `settings.xml`, `controls.xml` and the Nuclex skin all resolve under `--root`. No copy-to-output; `Test/` stays the single runtime directory. |
| 11 | Smoke test on loopback: server up, client connects with the random identity, reaches packet 9, moves, fires, terrain deforms, disconnect clean. Automate through the MCP `smoke_test` tool. | todo | |
| 12 | Cleanup: remove `lib/MonoGame`, `dlls/`, `BUILD_NOTES.md`, `build.sh`, `Icons.res` references, `.DS_Store` files; update `README.md` build section. | todo | Keep `Test/*.exe` as the reference build. |

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
  left in place, unbuilt; delete it in step 12 if nobody wants the CLI.
* `ZombieSurvival/Vexillum.csproj` drops the author's `Win32Resource`
  (absolute `C:\Users\Jacob\...` path to `Icons.res`); an `ApplicationIcon`
  from `ZombieSurvival/Game.ico` can be added later.
* `dlls/`, `lib/`, `build.sh`, `BUILD_NOTES.md` are unreferenced by any
  project or solution file and can be deleted in step 12.

## Open TODOs left by steps 7, 8, 10 (2026-09-27) — for the integration agent

* **Build**: `Vexillum.sln` still fails with the step-3 baseline of 132 errors,
  all in `Game` (Nuclex 113, System.Drawing 15, WinForms 4); nothing in
  `PortProgram.cs`, `XnaEffectContent.cs` or `RuntimeDirectory.cs` is on the
  list (`Shims/XnaCompat` builds; the two `PortProgram.cs` files compile
  against stubs). After steps 4-6 land, build the solution and verify:
  * `ZombieSurvival/Vexillum.csproj` and `Server/Server.csproj` differ from
    step 1 only by `<StartupObject>`; the author's `Program` classes are
    non-public and reflection finds `Main` (`BindingFlags.NonPublic |
    Static`). If the Nuclex port makes `Program.cs` need anything else, keep
    it in the csproj, not in `Program.cs`.
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

## Hazard inventory (2026-09-27)

Run the `port_audit` MCP tool for the live list. Snapshot of files by hazard:

* `System.Drawing`: Util, GraphicsHelper, Level, LevelLoader, ClientLevel,
  MenuLevel, ScrollPanel, ChatPanel, net/Client (one `Color.Red`),
  TextRenderer, view/AssetManager, view/TerrainParticle, Server/ServerLevel,
  MapTool/MapCreator, ServerStart/*.
* `System.Windows.Forms`: Vexillum (MessageBox, message filter), util/KeyboardMessageFilter,
  ui/KeySelectorControl (`KeysConverter`), ZombieSurvival/Program, ServerStart/*.
* `Nuclex`: Vexillum, Util, LocalPlayer, all `ui/*Dialog`, ui/Menu,
  ui/CustomInputControl(+Renderer), ui/KeySelectorControl, ui/WebControl(+Renderer),
  view/AbstractView, view/GameView, view/MenuView, view/MultiplayerView,
  Entities/Weapons/{Weapon,SMG,RocketLauncher,ClusterBombLauncher} (`MouseButtons` only).
* `Steamworks`: SteamworksStub, Vexillum, steam/*, net/Client, ui/MainMenu,
  game/Identity, Server/ServerSteamAPI, Server/ServerPlayer.
* `Awesomium` + `unsafe`: ui/XNASurface, ui/WebControl, ui/WebControlRenderer
  (all fully commented out already).
* XNA namespaces gone in MonoGame: `GamerServices` (Vexillum, three weapons,
  ZombieSurvival), `Storage` (ControlSystem), `Media` (harmless, exists in MonoGame).
* `DllImport`: util/KeyboardMessageFilter.
* Hard-coded paths: `ZombieSurvival/Vexillum.csproj` `Win32Resource` points at
  `C:\Users\Jacob\...`; content project references `..\..\ExEnFontShim` and a
  Nuclex binaries folder outside the repo.

## Known original bugs (do not fix silently; log here and fix deliberately)

* `Util.EscapeUriString` builds `sb` then returns the unescaped `value`.
* `ServerPlayer.SetMovement` compares `movement[2]` against `direction` and
  `movement[3]` against `jumping` (indices shifted by one) before assigning
  correctly; the net effect is that `movementChanged` fires too often.
* `Server.Chat` `/kick` and `/ban` compare `p.name` (the issuer) instead of
  `other.name`, so they only ever hit the issuer.
* `Server.IsFull` uses `>` so `maxPlayers + 1` players can join.
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
