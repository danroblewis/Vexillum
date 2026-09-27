#!/bin/bash

# Vexillum Build Script
# This script builds the Vexillum game using Mono's xbuild

cd "$(dirname "$0")"

echo "Building Vexillum with Mono xbuild..."
echo "======================================="
echo ""

# Try using xbuild (Mono's build tool)
if command -v xbuild &> /dev/null; then
    echo "Using xbuild..."
    xbuild Vexillum.sln /p:Configuration=Debug /verbosity:minimal
elif command -v msbuild &> /dev/null; then
    echo "Using msbuild..."
    msbuild Vexillum.sln /p:Configuration=Debug /verbosity:minimal
else
    echo "ERROR: Neither xbuild nor msbuild found!"
    echo "Please install Mono: brew install mono"
    exit 1
fi

BUILD_STATUS=$?

echo ""
echo "======================================="
if [ $BUILD_STATUS -eq 0 ]; then
    echo "✅ BUILD SUCCESSFUL!"
    echo ""
    echo "Binaries location:"
    echo "  - Game library: Game/Game/bin/x86/Debug/Game.dll"
    echo "  - Main executable: ZombieSurvival/bin/x86/Debug/VexillumGame.exe"
    echo "  - Server: Server/bin/Debug/VexillumServer.exe"
    echo ""
    echo "To run the game on macOS with Mono:"
    echo "  mono ZombieSurvival/bin/x86/Debug/VexillumGame.exe"
else
    echo "❌ BUILD FAILED with exit code $BUILD_STATUS"
    echo ""
    echo "Please review the error messages above."
fi

exit $BUILD_STATUS

