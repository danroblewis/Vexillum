// MonoGame port of the Nuclex Framework (Vexillum, 2026). New file, CPL 1.0
// like the rest of the library (see LICENSE-CPL.txt).
//
// Replaces WindowMessageKeyboard, which read WM_KEYDOWN / WM_KEYUP / WM_CHAR
// from the Win32 message loop. Key presses and releases come from diffing
// Keyboard.GetState() once per Update() (one KeyPressed per physical press,
// no auto-repeat), characters come from the GameWindow.TextInput event
// (which MonoGame's DesktopGL platform raises for typed text as well as for
// Backspace, Tab and Return, exactly the control characters WM_CHAR sent).
// Without a game window (headless use), characters are synthesized from the
// key presses with the chat pad character map as a fallback.

using System;
using System.Collections.Generic;

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Nuclex.Input.Devices {

  /// <summary>Keyboard polled through MonoGame's Keyboard and GameWindow classes</summary>
  internal class MonoGameKeyboard : BufferedKeyboard, IDisposable {

    /// <summary>Initializes a new MonoGame keyboard</summary>
    /// <param name="window">
    ///   Game window whose TextInput event provides the entered characters.
    ///   Can be null, in which case characters are derived from key presses.
    /// </param>
    public MonoGameKeyboard(GameWindow window) {
      this.window = window;
      this.previousPressedKeys = new Keys[0];
      this.queuedCharacters = new Queue<char>();

      if(this.window != null) {
        this.textInputDelegate = new EventHandler<TextInputEventArgs>(textInput);
        this.window.TextInput += this.textInputDelegate;
      }
    }

    /// <summary>Immediately releases all resources owned by the instance</summary>
    public void Dispose() {
      if(this.window != null) {
        this.window.TextInput -= this.textInputDelegate;
        this.window = null;
      }
    }

    /// <summary>Whether the input device is connected to the system</summary>
    public override bool IsAttached {
      get { return true; }
    }

    /// <summary>Human-readable name of the input device</summary>
    public override string Name {
      get { return "PC Keyboard"; }
    }

    /// <summary>Polls the keyboard and fires events for all state changes</summary>
    public override void Update() {
      poll();
      base.Update();
    }

    /// <summary>
    ///   Compares the current keyboard state against the previous one and
    ///   buffers the resulting key and character events
    /// </summary>
    private void poll() {
      KeyboardState current = Keyboard.GetState();
      Keys[] currentPressedKeys = current.GetPressedKeys();

      // Keys that were down before but are not down anymore have been released
      for(int index = 0; index < this.previousPressedKeys.Length; ++index) {
        Keys key = this.previousPressedKeys[index];
        if(!current.IsKeyDown(key)) {
          BufferKeyRelease(key);
        }
      }

      // Keys that are down now but were not down before have been pressed
      for(int index = 0; index < currentPressedKeys.Length; ++index) {
        Keys key = currentPressedKeys[index];
        if(!wasPressed(key)) {
          BufferKeyPress(key);
          if(this.window == null) {
            bufferCharacterFromKey(key, ref current);
          }
        }
      }

      this.previousPressedKeys = currentPressedKeys;

      // Characters typed since the last update (delivered by the window's
      // TextInput event on the game thread between two updates)
      lock(this.queuedCharacters) {
        while(this.queuedCharacters.Count > 0) {
          BufferCharacterEntry(this.queuedCharacters.Dequeue());
        }
      }
    }

    /// <summary>Checks whether a key was pressed in the previous state</summary>
    /// <param name="key">Key that will be checked</param>
    /// <returns>True if the key was pressed in the previous state</returns>
    private bool wasPressed(Keys key) {
      for(int index = 0; index < this.previousPressedKeys.Length; ++index) {
        if(this.previousPressedKeys[index] == key) {
          return true;
        }
      }
      return false;
    }

    /// <summary>Called when the game window receives text input</summary>
    /// <param name="sender">Window that received the text input</param>
    /// <param name="arguments">Contains the entered character</param>
    private void textInput(object sender, TextInputEventArgs arguments) {
      lock(this.queuedCharacters) {
        this.queuedCharacters.Enqueue(arguments.Character);
      }
    }

    /// <summary>
    ///   Derives an entered character from a key press (fallback used when
    ///   no game window is available to provide text input)
    /// </summary>
    /// <param name="key">Key that has been pressed</param>
    /// <param name="current">Current keyboard state (for the shift keys)</param>
    private void bufferCharacterFromKey(Keys key, ref KeyboardState current) {
      int keyIndex = (int)key;
      if((keyIndex < 0) || (keyIndex >= XnaKeyboard.characterMap.Length)) {
        return;
      }

      char character = XnaKeyboard.characterMap[keyIndex];
      if(character == '\0') {
        return;
      }

      bool isShiftPressed =
        current.IsKeyDown(Keys.LeftShift) ||
        current.IsKeyDown(Keys.RightShift);

      if(isShiftPressed) {
        BufferCharacterEntry(char.ToUpper(character));
      } else {
        BufferCharacterEntry(character);
      }
    }

    /// <summary>Game window providing the text input events</summary>
    private GameWindow window;
    /// <summary>Delegate for the textInput() method</summary>
    private EventHandler<TextInputEventArgs> textInputDelegate;
    /// <summary>Keys that were pressed when the keyboard was last polled</summary>
    private Keys[] previousPressedKeys;
    /// <summary>Characters entered since the keyboard was last polled</summary>
    private Queue<char> queuedCharacters;

  }

} // namespace Nuclex.Input.Devices
