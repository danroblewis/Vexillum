using System;

using Microsoft.Xna.Framework.Input;

using Nuclex.Input;
using Nuclex.Input.Devices;
using Nuclex.UserInterface;
using Nuclex.UserInterface.Controls;
using Nuclex.UserInterface.Controls.Desktop;
using Nuclex.UserInterface.Input;

using Xunit;

namespace Vexillum.Tests
{
    /// <summary>
    /// Exercises the Nuclex source port (Shims/Nuclex) without a graphics
    /// device: control tree layout, and input delivered through the same
    /// IInputCapturer path the GuiManager uses at runtime. Skin loading
    /// needs a GraphicsDevice and is covered by the runtime smoke test.
    /// </summary>
    public class NuclexShimTests
    {
        // The game's window; AbstractView sets the desktop to 100% of it.
        private const float ScreenWidth = 840f;
        private const float ScreenHeight = 630f;

        private static Screen CreateScreenWithWindowAndButton(
            out WindowControl window, out ButtonControl button)
        {
            Screen screen = new Screen(ScreenWidth, ScreenHeight);
            screen.Desktop.Bounds = new UniRectangle(
                new UniScalar(0f, 0f), new UniScalar(0f, 0f),
                new UniScalar(1f, 0f), new UniScalar(1f, 0f));

            // Centered 200x100 window, the way the dialogs position themselves
            window = new WindowControl();
            window.Title = "Test";
            window.Bounds = new UniRectangle(
                new UniScalar(0.5f, -100f), new UniScalar(0.5f, -50f),
                new UniScalar(0f, 200f), new UniScalar(0f, 100f));
            screen.Desktop.Children.Add(window);

            // 80x24 button at (10, 20) inside the window, like Menu.AddButton
            button = new ButtonControl();
            button.Text = "Press";
            button.Bounds = new UniRectangle(
                new UniVector(new UniScalar(0f, 10f), new UniScalar(0f, 20f)),
                new UniVector(80f, 24f));
            window.Children.Add(button);

            return screen;
        }

        [Fact]
        public void AbsoluteBoundsResolveAgainstScreenSize()
        {
            WindowControl window;
            ButtonControl button;
            Screen screen = CreateScreenWithWindowAndButton(out window, out button);

            RectangleF windowBounds = window.GetAbsoluteBounds();
            Assert.Equal(0.5f * ScreenWidth - 100f, windowBounds.X);
            Assert.Equal(0.5f * ScreenHeight - 50f, windowBounds.Y);
            Assert.Equal(200f, windowBounds.Width);
            Assert.Equal(100f, windowBounds.Height);

            RectangleF buttonBounds = button.GetAbsoluteBounds();
            Assert.Equal(windowBounds.X + 10f, buttonBounds.X);
            Assert.Equal(windowBounds.Y + 20f, buttonBounds.Y);
            Assert.Equal(80f, buttonBounds.Width);
            Assert.Equal(24f, buttonBounds.Height);

            // Fractions follow the screen size, offsets do not
            screen.Width = 1000f;
            Assert.Equal(400f, window.GetAbsoluteBounds().X);
            Assert.Equal(410f, button.GetAbsoluteBounds().X);
        }

        [Fact]
        public void UniScalarAndRectangleArithmeticMatchNuclex()
        {
            UniScalar half = new UniScalar(0.5f, -10f);
            Assert.Equal(410f, half.ToOffset(840f));

            UniRectangle rectangle = new UniRectangle(10f, 20f, 30f, 40f);
            RectangleF resolved = rectangle.ToOffset(840f, 630f);
            Assert.Equal(10f, resolved.X);
            Assert.Equal(20f, resolved.Y);
            Assert.Equal(40f, resolved.Right);
            Assert.Equal(60f, resolved.Bottom);
        }

        [Fact]
        public void MousePressThroughInputCapturerFiresButtonAndCapturesInput()
        {
            WindowControl window;
            ButtonControl button;
            Screen screen = CreateScreenWithWindowAndButton(out window, out button);

            int pressedCount = 0;
            button.Pressed += delegate(object sender, EventArgs arguments)
            {
                ++pressedCount;
            };

            // Same path as GuiManager.Initialize(): an IInputService feeding the
            // DefaultInputCapturer, whose receiver is the screen.
            using (MockInputManager input = new MockInputManager())
            using (DefaultInputCapturer capturer = new DefaultInputCapturer(input))
            {
                capturer.InputReceiver = screen;
                MockedMouse mouse = input.GetMouse();

                Assert.False(screen.IsInputCaptured);
                Assert.False(screen.IsMouseOverGui);

                // Move over the button: (330 + 5, 285 + 5) in screen pixels
                RectangleF buttonBounds = button.GetAbsoluteBounds();
                mouse.MoveTo(buttonBounds.X + 5f, buttonBounds.Y + 5f);
                input.Update();
                Assert.True(screen.IsMouseOverGui);
                Assert.False(screen.IsInputCaptured);

                // Press: the button captures the input, Pressed fires on release
                mouse.Press(MouseButtons.Left);
                input.Update();
                Assert.True(screen.IsInputCaptured);
                Assert.Equal(0, pressedCount);

                mouse.Release(MouseButtons.Left);
                input.Update();
                Assert.Equal(1, pressedCount);
                Assert.False(screen.IsInputCaptured);

                // Clicking outside every control is not captured, which is
                // what lets Vexillum.MouseDown() reach the game view.
                mouse.MoveTo(5f, 5f);
                mouse.Press(MouseButtons.Left);
                input.Update();
                Assert.False(screen.IsMouseOverGui);
                Assert.False(screen.IsInputCaptured);
                mouse.Release(MouseButtons.Left);
                input.Update();
                Assert.Equal(1, pressedCount);
            }
        }

        [Fact]
        public void KeyboardEventsReachFocusedInputControlThroughCapturer()
        {
            Screen screen = new Screen(ScreenWidth, ScreenHeight);
            InputControl input = new InputControl();
            input.Bounds = new UniRectangle(10f, 10f, 200f, 24f);
            screen.Desktop.Children.Add(input);

            using (MockInputManager inputManager = new MockInputManager())
            using (DefaultInputCapturer capturer = new DefaultInputCapturer(inputManager))
            {
                capturer.InputReceiver = screen;
                screen.FocusedControl = input;
                Assert.Same(input, screen.FocusedControl);

                MockedKeyboard keyboard = inputManager.GetKeyboard();
                keyboard.Type("ab");
                inputManager.Update();
                Assert.Equal("ab", input.Text);

                keyboard.Press(Keys.Back);
                keyboard.Release(Keys.Back);
                inputManager.Update();
                Assert.Equal("a", input.Text);
            }
        }

        [Fact]
        public void EmbeddedSkinResourcesArePresent()
        {
            // The skin schema FlatGuiGraphics validates every skin against
            System.Reflection.Assembly nuclex = typeof(GuiManager).Assembly;
            using (System.IO.Stream schema = nuclex.GetManifestResourceStream(
                typeof(GuiManager), "Resources.skin.xsd"))
            {
                Assert.NotNull(schema);
                Assert.NotNull(Nuclex.Support.XmlHelper.LoadSchema(schema));
            }

            // The default "Suave" skin GuiManager.Initialize() falls back to
            System.Resources.ResourceManager resources = (System.Resources.ResourceManager)
                typeof(GuiManager).Assembly
                    .GetType("Nuclex.UserInterface.Resources.SuaveSkinResources")
                    .GetProperty("ResourceManager",
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.NonPublic)
                    .GetValue(null, null);
            foreach (string name in new string[] { "SuaveSkin", "SuaveSheet", "DefaultFont", "TitleFont" })
            {
                byte[] data = resources.GetObject(name) as byte[];
                Assert.NotNull(data);
                Assert.True(data.Length > 0, name);
            }
            byte[] sheet = (byte[])resources.GetObject("SuaveSheet");
            Assert.Equal((byte)'X', sheet[0]);
            Assert.Equal((byte)'N', sheet[1]);
            Assert.Equal((byte)'B', sheet[2]);
        }

        [Fact]
        public void DarknessSkinValidatesAgainstSchema()
        {
            // The game's skin (Test/Content/ui/DarknessUI.xml) must pass the
            // same XSD validation FlatGuiGraphics.loadSkin() performs; only
            // the resource loading (fonts, texture) needs a graphics device.
            string skinPath = FindRuntimeFile("Content/ui/DarknessUI.xml");
            if (skinPath == null)
            {
                return; // repository layout not available (e.g. running from a scratch copy)
            }

            System.Xml.Schema.XmlSchema schema;
            using (System.IO.Stream schemaStream = typeof(GuiManager).Assembly
                .GetManifestResourceStream(typeof(GuiManager), "Resources.skin.xsd"))
            {
                schema = Nuclex.Support.XmlHelper.LoadSchema(schemaStream);
            }

            System.Xml.Linq.XDocument skin =
                Nuclex.Support.XmlHelper.LoadDocument(schema, skinPath);
            System.Xml.Linq.XElement resources = skin.Element("skin").Element("resources");
            Assert.NotNull(resources.Element("font"));
            Assert.NotNull(resources.Element("bitmap"));
            Assert.Contains(
                skin.Element("skin").Element("frames").Elements("frame"),
                delegate(System.Xml.Linq.XElement frame)
                {
                    return frame.Attribute("name").Value == "window";
                });
        }

        /// <summary>Locates a file under the Test/ runtime directory of the repository</summary>
        private static string FindRuntimeFile(string relativePath)
        {
            string root = Environment.GetEnvironmentVariable("VEXILLUM_ROOT");
            if (string.IsNullOrEmpty(root))
            {
                string directory = AppContext.BaseDirectory;
                while (directory != null)
                {
                    if (System.IO.File.Exists(System.IO.Path.Combine(directory, "Vexillum.sln")))
                    {
                        root = directory;
                        break;
                    }
                    directory = System.IO.Path.GetDirectoryName(directory);
                }
            }
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            string path = System.IO.Path.Combine(root, "Test", relativePath);
            return System.IO.File.Exists(path) ? path : null;
        }

        [Fact]
        public void MouseButtonsMatchUtilMapping()
        {
            // Util.GetMouseButtonInt / GetMouseButton rely on these three members
            Assert.Equal(1, (int)MouseButtons.Left);
            Assert.Equal(2, (int)MouseButtons.Middle);
            Assert.Equal(4, (int)MouseButtons.Right);
            Assert.True(MouseButtons.Left.HasFlag(MouseButtons.Left));
        }
    }
}
