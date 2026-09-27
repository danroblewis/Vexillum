using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.GamerServices;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Media;
using Vexillum.Entities.Weapons;
using Vexillum.view;
using Vexillum.Entities;
using Vexillum.ui;
using Nuclex.UserInterface;
using Nuclex.Input;
using ui;
using Nuclex.UserInterface.Visuals.Flat;
using System.Resources;
using Vexillum.net;
using Vexillum.util;
using Vexillum.Game;
using System.Threading;
using Vexillum.mp;
using Vexillum.steam;
using Steamworks;
using Vexillum.game;

namespace Vexillum
{
    public class Vexillum : Microsoft.Xna.Framework.Game
    {
        public const int VERSION = 1;
        public static bool IsServer = false;

        public static Vexillum game;
        public const string Server = "http://playvexillum.com/game/";
        private ClientSteamAPI steam;

        private GraphicsDeviceManager graphics;
        private SpriteBatch spriteBatch;
        private SamplerState samplerState;
        private GuiManager gui;
        private InputManager input;
        private Dictionary<Keys, bool> pressedKeys = new Dictionary<Keys, bool>();

        private Level level;

        private Client client = null;
        private MPClient mpClient = null;

        private AbstractView view;
        public AbstractView View
        {
            get
            {
                return view;
            }
            set
            {
                view = value;
                gui.Screen = view.GetScreen();
            }
        }

        public const int GameWidth = 840;
        public const int GameHeight = 630;
        public const int Scale = 1;
        public const int WindowWidth = GameWidth * Scale;
        public const int WindowHeight = GameHeight * Scale;
        public bool waitingForServer = false;

        public Vexillum()
        {
            game = this;
            steam = new ClientSteamAPI(this);
            steam.Initialize();
            SetSteamIdentity();

            graphics = new GraphicsDeviceManager(this);
            graphics.PreferMultiSampling = false;
            graphics.PreferredBackBufferWidth = WindowWidth;
            graphics.PreferredBackBufferHeight = WindowHeight;
            graphics.PreparingDeviceSettings += new EventHandler<PreparingDeviceSettingsEventArgs>(delegate(object sender, PreparingDeviceSettingsEventArgs e)
            {
                e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
            });

            input = new InputManager(Services, Window.Handle);
            gui = new GuiManager(Services);

            IsMouseVisible = true;

            Content.RootDirectory = "Content";

            Components.Add(input);
            Components.Add(gui);
            gui.DrawOrder = 1000;
            this.Exiting += new EventHandler<EventArgs>(OnExit);

            Settings.LoadSettings();

        }
        private void OnExit(object source, EventArgs args)
        {
            SteamManager.Shutdown();
            if (client != null)
            {
                client.Disconnect(true);
            }
            Util.CloseLockFile();
            Util.WriteDebugLog();
        }

        protected override void Initialize()
        {
            base.Initialize();
            TargetElapsedTime = new TimeSpan(0, 0, 0, 0, VexillumConstants.TIME_PER_FRAME);

            var keyboardFilter = new KeyboardMessageFilter();
            System.Windows.Forms.Application.AddMessageFilter(keyboardFilter);

            IsFixedTimeStep = true;
            SoundEffect.DistanceScale = 250;

            //mainScreen.Desktop.Children.Add(new IPJoinDialog());

            input.GetKeyboard().KeyPressed += KeyPressed;
            input.GetKeyboard().CharacterEntered += CharacterEntered;
            input.GetKeyboard().KeyReleased += KeyReleased;
            input.GetMouse().MouseButtonPressed += MouseDown;
            input.GetMouse().MouseButtonReleased += MouseUp;
            input.GetMouse().MouseMoved += MouseMove;
            input.GetMouse().MouseWheelRotated += MouseWheelRotate;
        }

        private void KeyPressed(Keys key)
        {
            //Steam overlay
            if (pressedKeys.ContainsKey(Keys.LeftShift) && key == Keys.Tab)
                return;
            if (View.GetScreen().FocusedControl == null)
                View.KeyPressed(key, !pressedKeys.ContainsKey(key));
            pressedKeys[key] = true;
        }
        private void CharacterEntered(char character)
        {
            if (View.GetScreen().FocusedControl == null)
                View.CharacterEntered(character);
        }

        private void KeyReleased(Keys key)
        {
            if (View.GetScreen().FocusedControl == null)
                View.KeyReleased(key);
            pressedKeys.Remove(key);
        }

        private void MouseDown(MouseButtons button)
        {
            if (!View.GetScreen().IsInputCaptured)
            {
                View.GetScreen().FocusedControl = null;
                View.MouseDown(button);
            }
        }

        private void MouseUp(MouseButtons button)
        {
            if (!View.GetScreen().IsInputCaptured)
            {
                View.MouseUp(button);
            }
        }

        private void MouseWheelRotate(float ticks)
        {
            if (!View.GetScreen().IsInputCaptured)
            {
                view.MouseWheelMoved(ticks);
            }
        }

        private void MouseMove(float x, float y)
        {
            if (x !=-1 && y != -1 && !View.GetScreen().IsMouseOverGui)
            {
                if (Mouse.GetState().LeftButton == ButtonState.Pressed)
                    View.MouseDrag(MouseButtons.Left, x, y);
                if (Mouse.GetState().RightButton == ButtonState.Pressed)
                    View.MouseDrag(MouseButtons.Right, x, y);
                else
                    View.MouseMove(x, y);
            }
        }

        protected override void LoadContent()
        {
            //SpriteFont font = Content.Load<SpriteFont>("TitleFont");
            gui.Visualizer = FlatGuiVisualizer.FromFile(Services, "Content/ui/DarknessUI.xml");
            ((FlatGuiVisualizer)gui.Visualizer).RendererRepository.AddAssembly(typeof(Vexillum).Assembly);

            samplerState = new SamplerState();
            samplerState.Filter = TextureFilter.Point;

            spriteBatch = new SpriteBatch(GraphicsDevice);
            SurvivalGameModeClient.LoadContent();

            TextRenderer.LoadFonts(Content);
            GameView.LoadShaders(Content);

            AssetManager.addSound(Sounds.EXPLOSION, Content.Load<SoundEffect>("explosion"));
            AssetManager.addSound(Sounds.ROCKET, Content.Load<SoundEffect>("Rocket"));
            AssetManager.addSound(Sounds.WALK1, Content.Load<SoundEffect>("walk1"));
            AssetManager.addSound(Sounds.WALK2, Content.Load<SoundEffect>("walk2"));
            AssetManager.addSound(Sounds.WALK3, Content.Load<SoundEffect>("walk3"));
            AssetManager.addSound(Sounds.WALK4, Content.Load<SoundEffect>("walk4"));
            AssetManager.addSound(Sounds.WALK5, Content.Load<SoundEffect>("walk5"));
            AssetManager.addSound(Sounds.CLICK, Content.Load<SoundEffect>("click"));
            AssetManager.addSound(Sounds.CLICK2, Content.Load<SoundEffect>("click2"));
            AssetManager.addSound(Sounds.SMG, Content.Load<SoundEffect>("smg"));
            AssetManager.addSound(Sounds.SWORD1, Content.Load<SoundEffect>("sword1"));
            AssetManager.addSound(Sounds.SWORD2, Content.Load<SoundEffect>("sword2"));
            AssetManager.addSound(Sounds.SWORD3, Content.Load<SoundEffect>("sword3"));

            HumanoidTypes.LoadContent();

            ControlSystem.LoadControls();
            SetMenuView(false);

            if (Identity.username == null)
            {
                Vexillum.game.MessageBox("Could not determine your Steam display name. Make sure you're starting the game from Steam.");
            }
            else
            {
                Vexillum.game.SetMenuView(true);
            }

            //view.OpenWindow(new LoginDialog());
            /*WebControl web = new WebControl();
            web.Bounds = new UniRectangle(0, 0, 840, 630);
            web.UpdateSize();
            view.GetScreen().Desktop.Children.Add(web);*/
        }

        public void SetSteamIdentity()
        {
            Identity.username = new Random().NextDouble()+" "+SteamFriends.GetPersonaName();
            Identity.uid = new CSteamID((ulong)new Random().Next(1000));// SteamUser.GetSteamID();
        }

        public GameView SetGameView(ClientLevel level)
        {
            View = new GameView(GraphicsDevice, GameWidth, GameHeight, level);
            return (GameView)View;
        }

        public void SetMultiplayerView()
        {
            View = new MultiplayerView(GraphicsDevice, GameWidth, GameHeight);
        }

        public void SetMenuView(bool showUI)
        {
            if (!(View is MainMenuView))
                View = new MainMenuView(GraphicsDevice, GameWidth, GameHeight, null);
            ((MainMenuView)View).SetMenuVisible(showUI);
        }

        protected override void UnloadContent()
        {

        }

        protected override void Update(GameTime gameTime)
        {
            View.Step(gameTime);
            try
            {
                base.Update(gameTime);
            }
            catch (Exception ex)
            {
            }
            SteamManager.Update();
        }

        public void BeginSpriteBatch(Effect effect)
        {
            spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, SamplerState.PointClamp, null, null, null, Matrix.CreateScale(1));
        }
        public void BeginSpriteBatchScaled(Effect effect)
        {
            spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, SamplerState.PointClamp, null, null, null, Matrix.CreateScale(1));
        }
        public void BeginSpriteBatch()
        {
            BeginSpriteBatch(null);
        }
        public void BeginSpriteBatchScaled()
        {
            BeginSpriteBatchScaled(null);
        }
        public void BeginSpriteBatchRotated(Matrix matrix)
        {
            spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, SamplerState.PointClamp, null, null, null, matrix);
        }

        protected override void Draw(GameTime gameTime)
        {
            GraphicsDevice.Clear(Color.Black);

            BeginSpriteBatch();
            View.DrawStuff(GraphicsDevice, spriteBatch);
            spriteBatch.End();

            try
            {
                base.Draw(gameTime);
            }
            catch (Exception ex)
            {
            }
        }

        public void MessageBox(string message)
        {
            ErrorDialog dialog = new ErrorDialog(message);
            View.GetScreen().Desktop.Children.Add(dialog);
            dialog.BringToFront();
        }

        public StatusDialog StatusBox(string message)
        {
            StatusDialog dialog = new StatusDialog(message);
            View.GetScreen().Desktop.Children.Add(dialog);
            dialog.BringToFront();
            return dialog;
        }

        public void Connect(string ip, int port)
        {
            if (View != null)
                View.SetMenuVisible(false);
            client = new Client(ip, port);
            client.Start();
        }
        public MPClient MPConnect(MultiplayerView view, string ip, int port)
        {
            if (View != null)
                View.SetMenuVisible(false);
            mpClient = new MPClient(view, ip, port);
            mpClient.Start();

            return mpClient;
        }

        public bool CheckStatus(string ip, int port)
        {
            client = new Client(ip, port);
            return client.CheckStatus();
        }

        public void ConnectWhenServerReady(string ip, int port, int max)
        {
            SetMenuView(false);
            int n = 0;
            waitingForServer = true;
            while (waitingForServer && n < max)
            {
                Thread.Sleep(500);
                if (waitingForServer && CheckStatus(ip, port))
                {
                    waitingForServer = false;
                    Connect(ip, port);
                    return;
                }
                n++;
            }
            if (n >= max)
            MessageBox("Connection timed out.");
            SetMenuView(true);
        }

        public void Disconnect()
        {
            client.Disconnect(true);
        }
        public void MPDisconnect()
        {
            mpClient.Disconnect(true);
        }
        public static void Error(String message)
        {
            game.Exit();
            System.Windows.Forms.MessageBox.Show("Unexpected error occured: "+message+"\nInformation about the problem has been saved. Please restart the Game and submit a bug report to help us fix it!", "Error");

        }

        public static object TerrainParticle { get; set; }
    }
}
