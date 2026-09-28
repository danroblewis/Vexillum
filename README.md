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
