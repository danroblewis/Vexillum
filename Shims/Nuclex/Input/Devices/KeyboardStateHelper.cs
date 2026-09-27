#region CPL License
/*
Nuclex Framework
Copyright (C) 2002-2011 Nuclex Development Labs

This library is free software; you can redistribute it and/or
modify it under the terms of the IBM Common Public License as
published by the IBM Corporation; either version 1.0 of the
License, or (at your option) any later version.

This library is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
IBM Common Public License for more details.

You should have received a copy of the IBM Common Public
License along with this library
*/
#endregion

// MonoGame port (Vexillum, 2026): the original file built delegates by
// reflection against XNA's private KeyboardState.AddPressedKey(int) and
// RemovePressedKey(int). MonoGame has no such members, so the same two
// delegates are implemented on the public KeyboardState API instead:
// the pressed key list is copied, modified and a new state constructed.

using System;
using System.Collections.Generic;

using Microsoft.Xna.Framework.Input;

namespace Nuclex.Input.Devices {

  /// <summary>Helper methods for the keyboard state class</summary>
  internal static class KeyboardStateHelper {

    /// <summary>Adds a key to the list of pressed keys in a keyboard state</summary>
    /// <param name="keyboardState">Keyboard state the key will be added to</param>
    /// <param name="key">Key that will be added to the keyboard state</param>
    public delegate void AddPressedKeyDelegate(
      ref KeyboardState keyboardState, int key
    );

    /// <summary>Removes a key from the list of pressed keys in a keyboard state</summary>
    /// <param name="keyboardState">Keyboard state the key will be removed from</param>
    /// <param name="key">Key that will be removed from the keyboard state</param>
    public delegate void RemovePressedKeyDelegate(
      ref KeyboardState keyboardState, int key
    );

    /// <summary>Adds a key to the list of pressed keys in a keyboard state</summary>
    private static void addPressedKey(ref KeyboardState keyboardState, int key) {
      Keys keyToAdd = (Keys)key;
      if(keyboardState.IsKeyDown(keyToAdd)) {
        return;
      }

      Keys[] pressedKeys = keyboardState.GetPressedKeys();
      Keys[] newPressedKeys = new Keys[pressedKeys.Length + 1];
      Array.Copy(pressedKeys, newPressedKeys, pressedKeys.Length);
      newPressedKeys[pressedKeys.Length] = keyToAdd;

      keyboardState = new KeyboardState(
        newPressedKeys, keyboardState.CapsLock, keyboardState.NumLock
      );
    }

    /// <summary>Removes a key from the list of pressed keys in a keyboard state</summary>
    private static void removePressedKey(ref KeyboardState keyboardState, int key) {
      Keys keyToRemove = (Keys)key;
      if(!keyboardState.IsKeyDown(keyToRemove)) {
        return;
      }

      Keys[] pressedKeys = keyboardState.GetPressedKeys();
      var newPressedKeys = new List<Keys>(pressedKeys.Length);
      for(int index = 0; index < pressedKeys.Length; ++index) {
        if(pressedKeys[index] != keyToRemove) {
          newPressedKeys.Add(pressedKeys[index]);
        }
      }

      keyboardState = new KeyboardState(
        newPressedKeys.ToArray(), keyboardState.CapsLock, keyboardState.NumLock
      );
    }

    /// <summary>Adds a key to the list of pressed keys in a keyboard state</summary>
    public static readonly AddPressedKeyDelegate AddPressedKey =
      new AddPressedKeyDelegate(addPressedKey);

    /// <summary>Removes a key from the list of pressed keys in a keyboard state</summary>
    public static readonly RemovePressedKeyDelegate RemovePressedKey =
      new RemovePressedKeyDelegate(removePressedKey);

  }

} // namespace Nuclex.Input.Devices
