# Vexillum architecture

Reference for how the original 2012-2013 code is put together. Nothing here is a
rule; the rules live in `CLAUDE.md`. Read this before touching a subsystem you
have not worked in yet.

## Projects (as found in `Vexillum.sln`)

| Project | Path | Output | Role | Port status |
|---|---|---|---|---|
| Lzma | `Shims/Lzma/` | `Lzma.dll` | `LZMA-SDK` NuGet package (7-Zip C# SDK) + `SevenZipHelper.cs`. Used by map files and terrain sync. | Done: package import. |
| Platform (Windows) | `Platform/` | `Platform.dll` | One struct, `Vexillum.util.Vec2`, wrapping `Microsoft.Xna.Framework.Vector2`. | Fold into Game. |
| PlatformLinux | `PlatformLinux/` | `Platform.dll` | Abandoned experiment: a `Vec2` with its own floats plus a fake empty `Microsoft.Xna.Framework.Vector2` class. Never referenced by the game. | Delete. |
| Game | `Game/Game/` | `Game.dll` | Everything shared by client and server: entities, physics, level, netcode, UI, views, utilities. | The bulk of the port. |
| Vexillum (client exe) | `ZombieSurvival/` | `VexillumGame.exe` | Entry point only. `ZombieSurvival.cs` is a leftover XNA template class named `Game1`, not used. | Rewrite entry point. |
| Server | `Server/` | `VexillumServer.exe` | Dedicated headless server, bots, CTF game mode. | Port; needs a graphics-free Game. |
| ServerStart | `ServerStart/` | `VexillumServerStart.exe` | WinForms dialog that edits `Server/settings.txt` then launches the server. | Replace with console flags or drop. |
| MapTools | `MapTool/` | `MapTools.dll` | Pack/unpack `.map` files. | Ports as-is except one `System.Drawing.Imaging` helper that is never called. |
| CreateMap, ExtractMap | `CreateMap.cs/`, `ExtractMap/` | console exes | Interactive wrappers over MapTools. | Port as-is. |
| VexillumContent | `ZombieSurvivalContent/` | XNA content project | Sources for fonts, sounds, shader, GUI skin. | Replace with MGCB or keep prebuilt `.xnb`. |

`Test/` is not a test suite. It is the **runtime directory**: the last shipped
Windows build plus `Content/`, `Maps/`, `Server/`, `settings.xml`,
`controls.xml`, `steam_appid.txt`. Both executables expect to run with a
working directory laid out exactly like `Test/`.

## Process model

```
 VexillumGame.exe                               VexillumServer.exe
 ┌──────────────────────────────┐    TCP 24224  ┌──────────────────────────────┐
 │ Vexillum : XNA Game          │◄────────────►│ Server                        │
 │  ├─ AbstractView (current)   │              │  ├─ Server Main thread (Step) │
 │  │   MainMenuView            │              │  │   60 Hz: tasks, level,     │
 │  │   GameView ── ClientLevel │              │  │   AI, weapons, gamemode    │
 │  │   MultiplayerView (lobby) │              │  ├─ Client Acceptor thread    │
 │  ├─ Client (net thread)      │              │  ├─ Entity Updater (50 ms)    │
 │  │   PingThread              │              │  ├─ Ping Sender (5 s)         │
 │  │   UpdateThread (100 ms)   │              │  ├─ SendHeartbeat (45 s, HTTP)│
 │  │   DataSender              │              │  └─ per client: ServerPlayer  │
 │  └─ Nuclex GuiManager/Input  │              │       Client_<ip> thread      │
 └──────────────────────────────┘              │       DataSender thread       │
                                               └──────────────────────────────┘
```

Both sides run the same `Level.DoPhysics()` at `VexillumConstants.FRAME_RATE`
(60 Hz, `TIME_PER_FRAME` = 16 ms). The server is authoritative for entity
creation, removal, health, explosions and hitscans. The client is trusted for
its own position and arm angle (it sends deltas; the server derives velocity).

### Threading rules already in the code

* All mutation of level state happens on one thread per process: the XNA
  `Update` thread on the client, `Server Main` on the server. Network threads
  never touch the level directly; they enqueue closures on a `TaskQueue`.
* `FrameTaskQueue` schedules closures for a specific frame number. Packets
  carrying a frame (entity create/remove, explode, projectile, sound, hook)
  are applied on that frame so both sides agree. If the frame already passed
  the task runs immediately and the entity is stepped forward the missing
  frames.
* `DataTaskQueue` is the per-connection outbound writer: callers write into a
  `MemoryStream` under `lock(tasks)`, then `WriteData()` hands the bytes to the
  sender thread.
* `Server.AddTask` runs inline when already on `Server Main`, otherwise
  enqueues. `Client.AddTask` runs inline until a `GameView` exists.

## Coordinate systems

World space is **Y-up**, pixel units, origin bottom-left of the map. Bitmaps
are Y-down, so every bitmap access flips: `bitmapY = Size.Y - worldY - 1`.
Screen space is Y-down; `GameView.CamStart` is the world position of the
top-left screen pixel (`CamPosition.X - xCenter, CamPosition.Y + yCenter`).
Entities carry `Position` (center), `HalfSize`, and `FeetPosition`.

## Level and terrain

`Level` (abstract) → `ClientLevel` (rendering, particles, sounds, `MenuLevel`)
and `ServerLevel` (flags, frame history, node graph for AI).

`TerrainArray` packs one `int` per pixel:

| bits | meaning |
|---|---|
| 0 | terrain solid (destructible unless collision says Solid) |
| 1 | terrain particle occupies pixel |
| 2 | transparent when destroyed |
| 3 | ladder |
| 4-7 | collision data nibble: 0 Empty, 1 Solid (indestructible), 2..15 hardness |
| 16-31 | entity ID (short) of the entity whose outline covers this pixel |

Entity outlines (not filled boxes) are written into the top 16 bits by
`SetEntityState`; collision checks probe those pixels. `ToBytes`/`SetBytes`
serialise only bit 0, column-major, 8 pixels per byte. That byte array, LZMA
compressed, is what the server sends as packet 3.

`Level`'s constructor derives terrain from the `collision` image (ARGB):

| collision pixel | result |
|---|---|
| `FFFF00FF` magenta | empty |
| `00000000` or `FFFFFFFF` | solid, indestructible |
| `FF0000FF` blue | empty and main image pixel cleared to transparent |
| `FFFFFF00` yellow | empty, ladder |
| `FFFFFF80` | empty, ladder, main pixel cleared |
| anything else | destructible; hardness = clamp(red/16, 2, 15); green == 128 marks "transparent when destroyed" |

## Map file format (`Maps/<name>.map`)

```
int32 LE  magic 0x004F876B
bytes     LZMA "alone" stream: 5 props + int64 LE size + data  (SevenZipHelper)
```
Decompressed payload, written with `BinaryWriter` (7-bit length-prefixed strings):
```
string longName
repeat:
  string fileName            e.g. "main.jpg"; an empty/blank name terminates
  int64  length
  bytes  file contents
```
Shipped maps contain, in this order: `main.jpg`, `background.jpg`,
`collision.png`, `data.txt`, `sky.jpg`, `left.png`, `right.png`,
`bottom.jpg`. `LevelLoader` keys bitmaps by the base name without extension.
`data.txt` lines: `region <name> <x1> <y1> <x2> <y2>` in bitmap (Y-down)
coordinates. Region names: `spawn_green`, `spawn_blue`, `flag_green`,
`flag_blue`, `ladder_*` (ignored). `MapCreator` reads the file list from a
`mapfile.list` next to the tool (not in the repo; derive it from the order
above).

## Entities

`Entity` (abstract, `Vec2 Position/Velocity/Size`, short `ID`, `Level`)
→ `BasicEntity` (static sprite: crate, flags, pixel)
→ `Projectile` (`Rocket`, `ClusterBomb`, `Bomblet`, `GrapplingHook`)
→ `LivingEntity` → `HumanoidEntity` (the players; sprite sheet + `Stance`).

Weapons are not entities but `Weapon` → `ReloadableWeapon` → `RocketLauncher`,
`SMG`, `ClusterBombLauncher`; `Sword` is a `Weapon`. Each weapon has a split
API: `*Client` methods run on the client and send packets, `*Server` methods
run on the server and are authoritative. `Stance` classes draw the
humanoid's upper body holding a weapon.

Entity IDs are `short`, allocated by the server (`Entity.NextID`), reset per
map (`Entity.ResetID`). The client never allocates IDs; it receives them.
`StreamHelper.entityTypes` maps a byte index to a fully qualified type name;
`Activator.CreateInstance(Type.GetType(name))` creates the entity, so entity
and weapon classes must keep their namespaces and public parameterless
constructors.

## Game mode

`IGameMode` (abstract) → `SurvivalGameModeShared` → `SurvivalGameModeClient`
(HUD) and server `SurvivalGameMode` (rules). Despite the name it is
capture-the-flag: two teams (`PlayerClass.Green`, `PlayerClass.Blue`),
`Spectator` when dead. Game mode state reaches the client through
`GameModeCommand` byte/short/string packets 120-122.

## Player identity and Steam

`Identity.uid` is a `CSteamID`, `Identity.username` a display name. The
original build used Steamworks.NET for identity, auth tickets
(`SteamUser.GetAuthSessionTicket` / `BeginAuthSession`) and the overlay pause
hook. The 2025 checkout already replaced the library with
`Game/Game/SteamworksStub.cs`, which is incomplete (server-side symbols such
as `ValidateAuthTicketResponse_t`, `EBeginAuthSessionResult`,
`SteamUser.BeginAuthSession` are missing). `Vexillum.SetSteamIdentity`
currently generates a random name and id, which is what makes
"steamless" testing possible.

## Web services

`Util.HttpGet/HttpPost` were written against `http://playvexillum.com/game/`:
`servers.php` (server list), `ping.php` (server heartbeat), `reportBug.php`.
That site is gone. Since 2026-09-27 both methods hand the request to
`Shims/MasterServer` first (two `// PORT:` lines in `Util.cs`), which answers
the three scripts in the original text format:

* `servers.php`: LAN servers heard on UDP 24224 for ~1.2 s (`[LAN]` prefix,
  sender address = connect address) plus internet servers whose heartbeat
  appeared on the public ntfy.sh topic `vexillum-servers-v1` in the last 22
  minutes and that answer the TCP status probe (byte 255 -> bool).
* `ping.php`: starts/refreshes the UDP beacon (every 2 s, JSON `{v,name,port,
  players,maxplayers,map,key}` to every interface broadcast, 255.255.255.255
  and 127.0.0.1) and, for public servers, publishes the heartbeat JSON to
  ntfy at most every 10 minutes with the public IP from api.ipify.org.
* `reportBug.php`: writes the report to `bugreports/` in the runtime dir.

Environment: `VEXILLUM_MASTER=off`, `VEXILLUM_MASTER_URL`, `VEXILLUM_MASTER_TOPIC`
(any ntfy server/topic, including a self-hosted one), `VEXILLUM_LAN=off`,
`VEXILLUM_LAN_PORT`. Internet play still needs the server's TCP port
forwarded, exactly as in 2013.

The lobby (`MultiplayerView`/`MPClient`) speaks a *different* big-endian,
length-prefixed protocol to a chat server on port 34224 that no longer exists;
it is untouched and fails gracefully.

## Rendering

Fixed 840x630 window, `Scale = 1`. `GameView.DrawStuff` renders the level to
a `RenderTarget2D`, then draws that target through `Blur.fx` (parameter `d` =
camera shake) onto the back buffer, then HUD and GUI on top. Sprite batches
use `SpriteSortMode.Immediate`, `BlendState.NonPremultiplied`,
`SamplerState.PointClamp`. `BeginSpriteBatch(Effect)` ignores its argument:
the code applies `blurEffect.CurrentTechnique.Passes[0]` manually before the
draw (works only because of Immediate mode). `RenderTargetUsage.PreserveContents`
is requested at device creation and the code relies on it.

Fonts: four `SpriteFont`s (`DefaultFont`, `TitleFont`, `FancyFont`,
`TinyFont`) loaded from `.xnb`. `TextRenderer` implements colour codes: `§`
followed by a digit selects `TextRenderer.colors[n]`.

Textures: everything except fonts/sounds/shader is loaded from loose PNG/JPG
files under `Content/` through `System.Drawing.Bitmap` → PNG in memory →
`Texture2D.FromStream`. `Level` keeps `System.Drawing.Bitmap`s alive
(`MainBitmap`, `BackgroundBitmap`) and reads pixels from them at runtime for
terrain particles and for repainting destroyed pixels from the background.

Audio: `SoundEffect` from `.xnb`, positional via `AudioListener`/`AudioEmitter`
with `SoundEffect.DistanceScale = 250`.

## Input and GUI

Nuclex.Input `InputManager` provides keyboard/mouse events (and, on Windows,
DirectInput through SlimDX). Nuclex.UserInterface `GuiManager` + `Screen`
draws windows/buttons/lists using the "Darkness" skin
(`Content/ui/DarknessUI.xml`, `Darkness.xnb`). `Menu`/`MenuItem` are the
game's own sprite-drawn menus; dialogs (`ServerDialog`, `OptionsDialog`,
`IPJoinDialog`, `ErrorDialog`, `StatusDialog`, `ReportBugDialog`,
`LoginDialog`, `DemoDialog`) are Nuclex `WindowControl`s. `ControlSystem`
maps `Keys` to `KeyAction`, persisted in `controls.xml`.

`KeyboardMessageFilter` (WinForms message filter + `user32` P/Invoke) exists
to get WM_CHAR text input; MonoGame exposes `Window.TextInput` for that.
