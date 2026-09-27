// MonoGame port of the Nuclex Framework (Vexillum, 2026). New file, CPL 1.0
// like the rest of the library (see LICENSE-CPL.txt).
//
// Replaces WindowMessageMouse, which read WM_MOUSEMOVE / WM_?BUTTONDOWN /
// WM_MOUSEWHEEL / WM_MOUSELEAVE from the Win32 message loop. The state
// returned by Mouse.GetState() is diffed once per Update(): positions are
// reported in window pixels, one MouseButtonPressed/Released per button
// change, and wheel rotation in the units the original used (a delta of the
// scroll wheel value divided by 120, i.e. one notch = 1.0). Like the
// original's WM_MOUSELEAVE handling, a cursor that leaves the client area is
// reported once as a move to (-1, -1).

using System;

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Nuclex.Input.Devices {

  /// <summary>Mouse polled through MonoGame's Mouse class</summary>
  internal class MonoGameMouse : BufferedMouse, IDisposable {

    /// <summary>Initializes a new MonoGame mouse</summary>
    /// <param name="window">
    ///   Game window the mouse belongs to (used for its client bounds). Can be null.
    /// </param>
    public MonoGameMouse(GameWindow window) {
      this.window = window;
      this.cursorInside = true;
    }

    /// <summary>Immediately releases all resources owned by the instance</summary>
    public void Dispose() {
      this.window = null;
    }

    /// <summary>Moves the mouse cursor to the specified location</summary>
    /// <param name="x">X coordinate of the cursor's new location in window pixels</param>
    /// <param name="y">Y coordinate of the cursor's new location in window pixels</param>
    public override void MoveTo(float x, float y) {
      Mouse.SetPosition((int)x, (int)y);
    }

    /// <summary>Whether the input device is connected to the system</summary>
    public override bool IsAttached {
      get { return true; }
    }

    /// <summary>Human-readable name of the input device</summary>
    public override string Name {
      get { return "PC Mouse"; }
    }

    /// <summary>Polls the mouse and fires events for all state changes</summary>
    public override void Update() {
      poll();
      base.Update();
    }

    /// <summary>
    ///   Compares the current mouse state against the previous one and buffers
    ///   the resulting movement, button and wheel events
    /// </summary>
    private void poll() {
      MouseState current = Mouse.GetState();

      // The first poll only establishes the reference state; otherwise the
      // accumulated wheel value and the initial cursor position would be
      // reported as a rotation and a movement that never happened.
      if(!this.initialized) {
        this.previous = current;
        this.initialized = true;
        return;
      }

      // Cursor movement (WM_MOUSEMOVE), including leaving the client area
      if((current.X != this.previous.X) || (current.Y != this.previous.Y)) {
        bool inside = isInsideClientArea(current.X, current.Y);
        if(inside) {
          BufferCursorMovement((float)current.X, (float)current.Y);
        } else if(this.cursorInside) {
          BufferCursorMovement(-1.0f, -1.0f); // as WM_MOUSELEAVE did
        }
        this.cursorInside = inside;
      }

      // Button state changes (WM_?BUTTONDOWN / WM_?BUTTONUP)
      bufferButtonChange(
        this.previous.LeftButton, current.LeftButton, MouseButtons.Left
      );
      bufferButtonChange(
        this.previous.MiddleButton, current.MiddleButton, MouseButtons.Middle
      );
      bufferButtonChange(
        this.previous.RightButton, current.RightButton, MouseButtons.Right
      );
      bufferButtonChange(
        this.previous.XButton1, current.XButton1, MouseButtons.X1
      );
      bufferButtonChange(
        this.previous.XButton2, current.XButton2, MouseButtons.X2
      );

      // Wheel rotation (WM_MOUSEWHEEL reported WHEEL_DELTA / 120 ticks)
      int wheelDelta = current.ScrollWheelValue - this.previous.ScrollWheelValue;
      if(wheelDelta != 0) {
        BufferWheelRotation((float)wheelDelta / 120.0f);
      }

      this.previous = current;
    }

    /// <summary>Buffers a press or release event if a button's state changed</summary>
    /// <param name="previousState">State the button had in the previous poll</param>
    /// <param name="currentState">State the button has now</param>
    /// <param name="button">Button whose state is being compared</param>
    private void bufferButtonChange(
      ButtonState previousState, ButtonState currentState, MouseButtons button
    ) {
      if(previousState == currentState) {
        return;
      }

      if(currentState == ButtonState.Pressed) {
        BufferButtonPress(button);
      } else {
        BufferButtonRelease(button);
      }
    }

    /// <summary>Determines whether a position lies inside the window's client area</summary>
    /// <param name="x">X coordinate in window pixels</param>
    /// <param name="y">Y coordinate in window pixels</param>
    /// <returns>True if the position is inside the client area</returns>
    private bool isInsideClientArea(int x, int y) {
      if(this.window == null) {
        return true;
      }

      Rectangle clientBounds = this.window.ClientBounds;
      return
        (x >= 0) && (y >= 0) &&
        (x < clientBounds.Width) && (y < clientBounds.Height);
    }

    /// <summary>Game window the mouse belongs to</summary>
    private GameWindow window;
    /// <summary>Whether the reference state has been established</summary>
    private bool initialized;
    /// <summary>Mouse state when the mouse was last polled</summary>
    private MouseState previous;
    /// <summary>Whether the cursor was inside the client area at the last poll</summary>
    private bool cursorInside;

  }

} // namespace Nuclex.Input.Devices
