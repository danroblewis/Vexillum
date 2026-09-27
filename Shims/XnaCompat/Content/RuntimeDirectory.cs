using System;
using System.IO;
using System.Reflection;
using Microsoft.Xna.Framework;

// Port glue (docs/PORTING.md step 10): the game's own file access
// (new Bitmap("Content/..."), "Maps/", "Server/settings.txt", settings.xml,
// the Nuclex skin XML) is relative to the current directory, and so was XNA's
// ContentManager in practice because the game ran from its install folder.
// MonoGame's ContentManager instead resolves Content.RootDirectory against
// TitleContainer.Location, which is the directory of the executable (on macOS
// first "../Resources" of it). With `dotnet run` / `dotnet VexillumGame.dll`
// that is bin/Debug/net9.0, so every Content.Load would miss the runtime
// directory chosen with --root. TitleContainer.Location is an internal static
// property with a private setter; pointing it at the current directory makes
// both kinds of file access agree without touching the author's
// `Content.RootDirectory = "Content"`.
namespace Vexillum.Port
{
    public static class RuntimeDirectory
    {
        /// <summary>
        /// Makes MonoGame's TitleContainer (and therefore every ContentManager)
        /// resolve relative content paths against the current directory.
        /// Call after Directory.SetCurrentDirectory and before the Game is
        /// constructed. Returns false if MonoGame's internals changed.
        /// </summary>
        public static bool UseCurrentDirectoryForContent()
        {
            try
            {
                PropertyInfo location = typeof(TitleContainer).GetProperty("Location",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (location == null)
                    return false;
                location.SetValue(null, Directory.GetCurrentDirectory());
                return string.Equals((string)location.GetValue(null), Directory.GetCurrentDirectory(), StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
