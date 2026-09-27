// MonoGame port of the Nuclex Framework (Vexillum, 2026). New file, CPL 1.0
// like the rest of the library (see LICENSE-CPL.txt).
//
// The original InputManager only needed the Win32 window handle (it hooked
// the message loop). On MonoGame the keyboard needs the GameWindow instead,
// because entered characters arrive through GameWindow.TextInput. The
// handle Vexillum passes is Game.Window.Handle, so this helper finds the
// GameWindow that belongs to it.

using System;
using System.Reflection;

using Microsoft.Xna.Framework;

namespace Nuclex.Input {

  /// <summary>Finds the game window an input manager should listen to</summary>
  internal static class GameWindowLocator {

    /// <summary>Locates the game window for the specified window handle</summary>
    /// <param name="services">
    ///   Game service container, searched for a registered Game or GameWindow
    /// </param>
    /// <param name="windowHandle">Handle of the game's main window</param>
    /// <returns>The game window or null if none could be found</returns>
    public static GameWindow Locate(IServiceProvider services, IntPtr windowHandle) {
      GameWindow window;

      // A game could register itself or its window as a service explicitly
      if(services != null) {
        window = services.GetService(typeof(GameWindow)) as GameWindow;
        if(window != null) {
          return window;
        }
        Game game = services.GetService(typeof(Game)) as Game;
        if(game != null) {
          return game.Window;
        }
      }

      // MonoGame keeps the running game in an internal static property
      // (Game.Instance). It is set by the Game constructor, so it is available
      // by the time a game constructs its InputManager.
      window = windowOfCurrentGame();
      if(window != null) {
        return window;
      }

      return null;
    }

    /// <summary>Returns the window of the currently running MonoGame game</summary>
    /// <returns>The game window or null if no game is running</returns>
    private static GameWindow windowOfCurrentGame() {
      try {
        PropertyInfo instanceProperty = typeof(Game).GetProperty(
          "Instance",
          BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        );
        if(instanceProperty == null) {
          return null;
        }

        Game game = instanceProperty.GetValue(null, null) as Game;
        if(game == null) {
          return null;
        }

        return game.Window;
      }
      catch(Exception) {
        return null;
      }
    }

  }

} // namespace Nuclex.Input
