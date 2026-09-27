This was a 2D game I made using the Microsoft XNA Framework in 2012-2013. It actually got accepted to Steam Greenlight in 2015 but by then I'd mostly lost interest. Code is probably not very good, it was my first and only time using C#.

https://www.youtube.com/watch?v=4cut5IVPDrA  
https://www.youtube.com/watch?v=g-VFNvZIwUw

---

## Building today (2026)

The game builds and runs again on current .NET and MonoGame (DesktopGL),
natively on macOS arm64, Linux and Windows, with the `dotnet` CLI only:

```
dotnet build Vexillum.sln
cd Test && dotnet ../Server/bin/Debug/net9.0/VexillumServer.dll                          # dedicated server, port 24224
cd Test && dotnet ../ZombieSurvival/bin/Debug/net9.0/VexillumGame.dll                    # client, main menu as in 2013
cd Test && dotnet ../ZombieSurvival/bin/Debug/net9.0/VexillumGame.dll --connect 127.0.0.1:24224   # client, joins that server
VEXILLUM_LOG_STDOUT=1 <either command>                                                   # echo the debug log to stdout
python3 .claude/mcp/vexillum_dev.py smoke_test seconds=30 clients=2                     # loopback server + two clients
```

The 2013 sources are unchanged apart from lines marked `// PORT:` (one line
in `Game/Game/Vexillum.cs`; the two `PortProgram.cs` entry points are new
files). XNA namespaces that MonoGame lacks, Nuclex (source port), Steamworks
(offline), `System.Drawing` and `System.Windows.Forms` are provided by the
`Shims/` projects, so the original files compile as they were written.
Details, status and what is left: [docs/PORTING.md](docs/PORTING.md).
