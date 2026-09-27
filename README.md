This was a 2D game I made using the Microsoft XNA Framework in 2012-2013. It actually got accepted to Steam Greenlight in 2015 but by then I'd mostly lost interest. Code is probably not very good, it was my first and only time using C#.

https://www.youtube.com/watch?v=4cut5IVPDrA  
https://www.youtube.com/watch?v=g-VFNvZIwUw

---

## Building today (2026)

The game compiles and runs again on current .NET and MonoGame (DesktopGL),
natively on macOS (Apple Silicon and Intel), Linux and Windows. The only
prerequisite is the .NET SDK 8 or newer (https://dotnet.microsoft.com/download);
it is the compiler, the dependency manager and the test runner. Third-party
code is not checked in: every library (MonoGame, LZMA, MiscUtil, StbImageSharp,
Roslyn scripting, xunit) is a NuGet package pinned in `Directory.Packages.props`
and `packages.lock.json`, downloaded on the first build. No Wine, Mono,
Visual Studio or XNA.

```
make            # compile everything            (= dotnet build Vexillum.sln)
make test       # run the tests                 (= dotnet test Vexillum.sln)
make server     # start the dedicated server, headless, on port 24224
make client     # start the game as shipped in 2013 (main menu)
make play       # start the game and join CONNECT (default 127.0.0.1:24224)
make dist RID=osx-arm64   # self-contained folder in dist/osx-arm64 (also linux-x64, win-x64)
```

Compiling produces native executables `Server/bin/Debug/net9.0/VexillumServer`
and `ZombieSurvival/bin/Debug/net9.0/VexillumGame` (plus `.dll` assemblies,
which are the compiled code they load). Both programs must be started from
a directory laid out like `Test/` (it holds `Content/`, `Maps/` and the
server config); `make server`/`make client` do that for you. The server list
in the menu shows servers on your LAN and public servers that announced
themselves; "Direct IP Join" still works as before.

The 2013 sources are unchanged apart from lines marked `// PORT:` (three
lines in total, in `Game/Game/Vexillum.cs` and `Game/Game/Util.cs`; the two
`PortProgram.cs` entry points are new files). XNA namespaces that MonoGame
lacks, Nuclex (source port), Steamworks (offline), `System.Drawing`,
`System.Windows.Forms` and the old playvexillum.com master server are provided
by the `Shims/` projects, so the original files compile as they were written.
Details, status and what is left: [docs/PORTING.md](docs/PORTING.md).
