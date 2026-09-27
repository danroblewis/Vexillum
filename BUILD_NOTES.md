# Vexillum Build Configuration

## Summary of Changes Made

This document summarizes the modifications made to build the Vexillum game on macOS using Mono and MonoGame.

### 1. Installed Dependencies

- **Mono 6.14.1** - Provides .NET Framework support on macOS
- **MonoGame 3.7.1** - Open-source XNA Framework replacement

### 2. Library Setup

Created `/Users/danroblewis/Vexillum/lib/MonoGame/` with:
- `MonoGame.Framework.dll` (v3.7.1 for .NET 4.5)
- `libopenal.dylib` (OpenAL audio library)
- `libSDL2-2.0.0.dylib` (SDL2 for input/window management)

### 3. External Dependencies Configured

The following DLLs were placed in `/Users/danroblewis/Vexillum/dlls/`:
- `Nuclex.Input.dll`
- `Nuclex.Support.dll`
- `Nuclex.UserInterface.dll`
- `Platform.dll`
- `SlimDX.DirectInput.dll`

### 4. Project File Modifications

#### Updated Projects:
- **Game/Game/Game.csproj** - Core game library
- **Platform/PlatformWindows.csproj** - Platform abstraction
- **Server/Server.csproj** - Server executable
- **ZombieSurvival/Vexillum.csproj** - Main game executable

#### Changes Made:
1. Replaced XNA Framework references with MonoGame.Framework
2. Updated DLL paths to use `dlls/` directory
3. Commented out XNA Game Studio build targets
4. Commented out missing references:
   - `Steamworks.NET.dll` (not provided)
   - `nunit.framework.dll` (test framework, not needed for build)

### 5. Known Limitations

#### Projects NOT Updated:
- **MapTools.csproj** - Map creation tool
- **CreateMap.csproj** - Map creation utility
- **ExtractMap.csproj** - Map extraction utility
- **ServerStart.csproj** - Server launcher GUI
- **VexillumContent.contentproj** - Content pipeline project

These projects may require additional configuration or are not critical for the main game build.

#### Missing/Commented References:
- **Steamworks.NET** - Steam integration disabled
- **nunit.framework** - Unit testing disabled

## How to Build

### Option 1: Using the Build Script

```bash
chmod +x build.sh
./build.sh
```

### Option 2: Manual Build

```bash
# Using xbuild (Mono's legacy build tool)
xbuild Vexillum.sln /p:Configuration=Debug

# OR using modern msbuild if available
msbuild Vexillum.sln /p:Configuration=Debug
```

### Option 3: Build Individual Projects

```bash
# Build in dependency order:
xbuild Lzma/Lzma.csproj
xbuild Platform/PlatformWindows.csproj
xbuild Game/Game/Game.csproj
xbuild Server/Server.csproj
xbuild ZombieSurvival/Vexillum.csproj
```

## Running the Game

After a successful build:

```bash
# Run the main game
mono ZombieSurvival/bin/x86/Debug/VexillumGame.exe

# OR run the server
mono Server/bin/Debug/VexillumServer.exe
```

## Potential Issues

### 1. Missing Steamworks Integration
The game originally used Steam integration, which has been disabled. This may cause errors in:
- Steam authentication code
- Steam multiplayer matchmaking  
- Steam achievements/stats

**Solution**: The code that uses Steamworks.NET will need to be wrapped in conditional compilation or try-catch blocks.

### 2. MonoGame API Differences
MonoGame aims for XNA compatibility, but some minor differences may exist:
- Content loading paths
- Audio format support
- Shader compatibility

**Solution**: Test the game and fix any runtime errors as they appear.

### 3. macOS-Specific Issues
- Window management may behave differently
- Input handling might need adjustment
- File paths use `/` instead of `\`

### 4. Content Pipeline
The XNA Content Pipeline is not fully replaced. Pre-built content (`.xnb` files) should work, but rebuilding content may require:
- MonoGame Content Builder Tool (MGCB)
- Or using the existing `.xnb` files from the `Test/Content/` directory

## Next Steps

1. **Test the build** by running `./build.sh`
2. **Fix compilation errors** if any appear
3. **Handle runtime errors** when launching the game
4. **Update Steam-dependent code** if needed
5. **Test gameplay** to ensure MonoGame compatibility

## Project Structure

```
Vexillum/
├── build.sh                    # Build script
├── BUILD_NOTES.md             # This file
├── lib/MonoGame/              # MonoGame libraries
├── dlls/                      # External dependencies
├── Lzma/                      # Compression library
├── Platform/                  # Platform abstraction (Windows)
├── PlatformLinux/             # Platform abstraction (Linux)
├── Game/Game/                 # Core game library
├── Server/                    # Dedicated server
├── ServerStart/               # Server launcher GUI
├── ZombieSurvival/            # Main game executable
├── MapTool/                   # Map tools library
├── CreateMap.cs/              # Map creation tool
├── ExtractMap/                # Map extraction tool
└── Test/                      # Game content and data
```

## Troubleshooting

### Build fails with "reference not found"
- Check that all DLLs in `dlls/` directory are present
- Verify file permissions: `chmod +r dlls/*.dll`

### Build fails with "XNA targets not found"
- Already fixed - XNA targets have been commented out

### MonoGame.Framework not found
- Verify `lib/MonoGame/MonoGame.Framework.dll` exists
- Check file permissions

### Runtime errors about missing methods
- MonoGame may not implement all XNA features
- Wrap problematic code in try-catch and implement workarounds

## Resources

- [MonoGame Documentation](https://docs.monogame.net/)
- [XNA to MonoGame Migration Guide](https://github.com/MonoGame/MonoGame/wiki/XNA-to-MonoGame-Cheatsheet)
- [Mono Project](https://www.mono-project.com/)

