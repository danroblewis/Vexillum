This was a 2D game I made using the Microsoft XNA Framework in 2012-2013. It actually got accepted to Steam Greenlight in 2015 but by then I'd mostly lost interest. Code is probably not very good, it was my first and only time using C#.

https://www.youtube.com/watch?v=4cut5IVPDrA  
https://www.youtube.com/watch?v=g-VFNvZIwUw

---

## Building today (2026)

The game compiles and runs on current .NET with MonoGame (DesktopGL), natively
on macOS, Linux and Windows. Prerequisite: the .NET SDK 8 or newer
(https://dotnet.microsoft.com/download); dependencies come from NuGet and one
git submodule (the Nuclex GUI library ported to MonoGame).

```
git submodule update --init
make            # compile everything            (= dotnet build Vexillum.sln)
make server     # start the dedicated server, headless, on port 24224
make client     # start the game (main menu as in 2013)
make play       # start the game and join 127.0.0.1:24224
```

The 2013 sources are unchanged apart from one line marked `// PORT:` in
`Game/Game/Vexillum.cs`; the XNA namespaces MonoGame lacks, `System.Drawing`,
`System.Windows.Forms` and Steamworks are provided by the small projects under
`Shims/`, so the original files compile as they were written. Both programs
run from a directory laid out like `Test/` (`make server`/`make client` do that).

### Server list and LAN games

The in-game server list used to come from `playvexillum.com`, which no longer
exists. `Shims/MasterServer` now answers the game's three requests to that
site (`servers.php`, `ping.php`, `reportBug.php`) in the original text format,
so the menus work as they were written (two lines marked `// PORT:` in
`Game/Game/Util.cs` route the requests to it):

* **LAN:** a running server answers discovery queries and sends a beacon on
  UDP 24224 every second; the server list shows every server on the local
  network with a `[LAN]` prefix, no configuration needed.
* **Internet:** a server whose `settings.txt` says `public true` posts a
  heartbeat every 10 minutes to the public ntfy.sh topic `vexillum-servers-v1`.
  The list shows servers heard from in the last 22 minutes that answer a
  status probe. Players still need the server's TCP port forwarded, as in 2013.
* **Bug reports** are saved to `bugreports/` next to the game.

Environment variables: `VEXILLUM_MASTER=off` disables the internet registry,
`VEXILLUM_MASTER_URL` and `VEXILLUM_MASTER_TOPIC` point it at another ntfy
server or topic (a self-hosted one works), `VEXILLUM_LAN=off` disables LAN
discovery and `VEXILLUM_LAN_PORT` moves it off 24224.
