using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using Vexillum.util;
using System.Diagnostics;
using SevenZip.Compression.LZMA;
using Microsoft.Xna.Framework;
using Vexillum.Entities;
using Vexillum.Game;
using Vexillum.view;
using Vexillum.ui;
using Vexillum.Entities.Weapons;
using System.Collections;
using Microsoft.Xna.Framework.Input;
using MiscUtil.Conversion;
using MiscUtil.IO;
using Vexillum.game;
using System.Security.Cryptography;
using Vexillum.steam;
using Steamworks;

namespace Vexillum.net
{
    public class Client : Disconnectable
    {
        private string ip;
        private int port;
        private TcpClient client;
        private Socket socket;
        private SteamManager.SteamAuthTicket authTicket;

        private NetworkStream stream;
        private EndianBinaryReader reader;
        private MemoryStream writebuffer;
        private EndianBinaryWriter writer;
        private StreamHelper helper;

        private DataTaskQueue tasks;

        private Thread pingThread;
        private Thread positionThread;

        private long pingReceiveTime;
        private long pingSendTime;
        private float lastMouseAngle;
        private int cFrame = -1;

        private bool connected = true;

        private Stopwatch timer = new Stopwatch();

        private ClientLevel level;
        private GameView view;
        public LocalPlayer player;
        private static StatusDialog status;

        private string gameModeName;

        private Vec2 sendPosition;
        private float sendAngle;

        private List<Player> players = new List<Player>();

        private string mapName;
        private byte[] levelBytes;
        private int levelPos = 0;

        public Client(string ip, int port)
        {
            this.ip = ip;
            this.port = port;

        }

        public bool CheckStatus()
        {
            bool status = this.RequestStatus();
            return status;
        }

        public void Start()
        {
            Thread thread = new Thread(new ThreadStart(this.Run));
            thread.Name = "ClientThread";
            thread.Start();
            /*new Thread(new ThreadStart(delegate()
            {
                Random r = new Random();
                while(true)
                {
                    if (player != null)
                    {
                        try
                        {
                            SendChat("/" + (r.NextDouble() < 0.5 ? "green" : "blue"));
                            SendWeaponSelect(0);
                            SendWeaponActivate(0, 1);
                            Thread.Sleep(100);
                        }
                        catch (Exception ex)
                        {
                            return;
                        }
                    }
                }
            })).Start();*/
        }

        private void Cancel(object souce, EventArgs ea)
        {
            if (Vexillum.game.waitingForServer)
            {
                Vexillum.game.waitingForServer = false;
                Vexillum.game.SetMenuView(true);
            }
            else
                Disconnect(true);
        }


        public void Disconnect()
        {
            Disconnect(false);
        }
        public void Disconnect(bool userDisconnect)
        {
            Disconnect(userDisconnect ? null : "Disconnected from server.");
        }

        public void Disconnect(string msg)
        {
            if (SteamManager.Initialized && authTicket != null && authTicket.ticket != HAuthTicket.Invalid)
                SteamUser.CancelAuthTicket(authTicket.ticket);
            authTicket = null;
            Util.Debug("Disconnected: " + msg);
            if (!connected)
                return;
            if (status != null)
            {
                status.Close();
                status = null;
            }
            try
            {
                connected = false;
                pingThread.Interrupt();
                if(positionThread != null)
                    positionThread.Interrupt();
                tasks.Stop();
                reader.Close();
                writer.Close();
                socket.Close();
            }
            catch (Exception ex)
            {
            }
            Vexillum.game.SetMenuView(true);
            if(msg != null)
                Vexillum.game.MessageBox(msg);
        }

        private void ShowLoadingScreen(string msg)
        {
            if (status == null)
            {
                Vexillum.game.SetMenuView(false);
                status = Vexillum.game.StatusBox(msg);
            }
            status.SetCancelAction(new EventHandler(Cancel));
            status.SetMessage(msg);
        }

        public bool RequestStatus()
        {
            try
            {
                client = new TcpClient(ip, port);
                socket = client.Client;
                stream = new NetworkStream(socket);
            }
            catch (Exception ex)
            {
                return false;
            }

            stream.WriteByte((byte)255);
            stream.Flush();

            bool status = new BinaryReader(stream).ReadBoolean();
            return status;
        }

        public void Run()
        {
            Util.Debug("Connecting to " + ip + ":" + port);
            ShowLoadingScreen("Connecting...");
            try
            {
                client = new TcpClient(ip, port);
                socket = client.Client;
                stream = new NetworkStream(socket);
                reader = new EndianBinaryReader(EndianBitConverter.Little, stream);
                writebuffer = new MemoryStream();
                writer = new EndianBinaryWriter(EndianBitConverter.Little, stream);
                helper = new StreamHelper(reader, writer);
            }
            catch (Exception ex)
            {
                Disconnect("Could not connect to the server.");
                return;
            }
            tasks = new DataTaskQueue(this, stream);
            tasks.Start();
            pingThread = new Thread(new ThreadStart(this.Ping));
            pingThread.Name = "PingThread";
            pingThread.Start();

            ShowLoadingScreen("Logging in...");
            authTicket = SteamManager.GetSessionTicket();
            SendLogin(Identity.uid.m_SteamID, Identity.username, authTicket.token, authTicket.tokenLength);
            while (connected)
            {
                try
                {
                    int cmd = reader.ReadByte();
                    ProcessPacket(cmd);
                }
                catch (ThreadInterruptedException ex)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Disconnect();
                    Util.Debug("Disconnected due to exception: " + ex);
                    if(!(ex is IOException))
                        Util.Debug(ex.StackTrace);
                    return;
                }
            }
        }
        private void SendInfoResponse()
        {
            sendPosition = Vec2.Zero;
            sendAngle = 0;
            lastMouseAngle = 0;
            ShowLoadingScreen("Loading map...");
            level = LevelLoader.Load(mapName);
            SendStatus(true, LevelLoader.LevelMd5(mapName));
        }
        private void ProcessPacket(int cmd)
        {
            //if(((cmd < 30 || cmd > 32) && cmd != 8))
                //Util.Debug("Packet: " + cmd);
            switch (cmd)
            {
                case 0: //Ping
                    pingReceiveTime = timer.ElapsedMilliseconds;
                    SendPing();
                    break;
                case 2: //Server ID
                    int protocolVersion = reader.ReadByte();
                    if (protocolVersion != VexillumConstants.PROTOCOL_VERSION)
                        Disconnect("Wrong protocol version");
                    //gameModeName = reader.ReadString();
                    mapName = reader.ReadString();
                    if (LevelLoader.LevelExists(mapName))
                    {
                        SendInfoResponse();
                    }
                    else
                    {
                        ShowLoadingScreen("Downloading map...");
                        SendStatus(false, new byte[16]);
                    }
                    break;
                case 3: //Terrain Data
                    ShowLoadingScreen("Loading terrain...");
                    int length = reader.ReadInt32();
                    byte[] bytes = SevenZipHelper.Decompress(reader.ReadBytes(length));
                    level.SetTerrainState(bytes);
                    Util.Debug("Set terrain state ("+length+" bytes)");
                    break;
                case 4: //Entity List
                    int count = reader.ReadInt16();
                    for (int i = 0; i < count; i++)
                    {
                        short id = reader.ReadInt16();
                        Type t = helper.ReadEntityType();
                        PlayerClass type;
                        Vec2 position = helper.ReadVec2();
                        // Util.Debug(level, "Created Entity: "+id+" "+t+" "+position);
                        Entity entity;
                        if (t == typeof(HumanoidEntity) || t == typeof(HumanoidEntity))
                        {
                            type = (PlayerClass) helper.ReadEnum(typeof(PlayerClass));
                            entity = HumanoidTypes.CreateHumanoid(type);
                        }
                        else
                        {
                            entity = (Entity)Activator.CreateInstance(t);
                        }
                        entity.Position = position;
                        entity.ID = id;
                        level.AddEntity(entity);
                    }
                    status.Close();
                    status = null;
                    break;
                case 5: //Player Spawn
                    int pid = reader.ReadInt16();
                    ulong steamId = reader.ReadUInt64();
                    string name = reader.ReadString();
                    float health = reader.ReadSingle();
                    PlayerClass newClass = (PlayerClass) helper.ReadEnum(typeof(PlayerClass));
                    int numWeapons = reader.ReadByte();
                    Weapon[] weapons = new Weapon[numWeapons];
                    for (int i = 0; i < numWeapons; i++)
                    {
                        weapons[i] = (Weapon)Activator.CreateInstance(helper.ReadEntityType());
                    }
                    int weaponIndex = reader.ReadByte();


                    Player p;
                    if (steamId == Identity.uid.m_SteamID)
                    {
                        p = new LocalPlayer(name, (HumanoidEntity)level.GetEntityByID(pid));
                        p.Entity.Health = health;
                        p.Entity.isPlayer = true;
                        player = (LocalPlayer)p;
                        player.SetClient(this);
                        if (!player.SetGameMode(new SurvivalGameModeClient(this)))
                            Disconnect("Invalid Game mode: " + gameModeName);
                        else
                            player.GetGameMode().SetLevel(level);
                        player.GetGameMode().SetClass(p, newClass);
                        p.Inventory = weapons;
                        p.SelectWeapon(weaponIndex);
                        players.Add(p);
                        player.GetGameMode().PlayerHealthChanged(player, null);
                    }
                    else
                    {
                        TaskDelegate addedDelegate = delegate()
                        {
                            HumanoidEntity playerEntity = (HumanoidEntity)level.GetEntityByID(pid);
                            playerEntity.Health = health;
                            playerEntity.isPlayer = true;
                            p = new NetworkPlayer(name, playerEntity);
                            p.Entity = playerEntity;
                            p.CurrentClass = PlayerClass.None; // PORT: fix: a new Player starts as Green (enum 0), so SetClass(Green) returned early and never called SetType (MaxHealth 0: dead, invisible)
                            player.GetGameMode().SetClass(p, newClass);
                            playerEntity.Health = health; // PORT: fix: re-apply the sent health now that SetType has set MaxHealth
                            p.Inventory = weapons;
                            p.SelectWeapon(weaponIndex);
                            players.Add(p);
                            player.GetGameMode().PlayerAdded(p);
                            if (Vexillum.game.View != null && Vexillum.game.View is GameView)
                                ((GameView)Vexillum.game.View).UpdateScoreboard();
                        };
                        if (view == null)
                            addedDelegate();
                        else
                            AddTask(addedDelegate);
                    }
                    break;
                case 8: //Frame Sync
                    int f8 = cFrame = helper.ReadFrameByte(cFrame);
                    AddTask(delegate()
                    {
                        view.SetFrame(f8);
                    });
                    break;
                case 9: //Level Finish
                    view = Vexillum.game.SetGameView(level);
                    AddTask(delegate()
                    {
                        level.ApplyTerrainState();
                        player.GetGameMode().PlayerAdded(player);
                        view.SetPlayers(players);
                    });

                    view.SetLocalPlayer(player);
                    positionThread = new Thread(new ThreadStart(SendPlayerPosition));
                    positionThread.Name = "UpdateThread";
                    positionThread.Start();
                    break;
                case 11:
                    int weaponAmmoIndex = reader.ReadByte();
                    int totalAmmo = reader.ReadByte();
                    int weaponClipAmmo = reader.ReadByte();
                    AddTask(delegate()
                    {
                        ((ReloadableWeapon)player.Inventory[weaponAmmoIndex]).SetAmmo(totalAmmo, weaponClipAmmo);
                    });
                    break;
                case 13:
                    int weaponSelectID = reader.ReadInt16();
                    int idx = reader.ReadByte();
                    AddTask(delegate()
                    {
                        Entity e = level.GetEntityByID(weaponSelectID);
                        if (e != null && e is HumanoidEntity)
                        {
                            ((HumanoidEntity)e).player.SelectWeapon(idx);
                        }
                        else
                            Util.Debug(level, "#13 Invalid entity type: " + weaponSelectID);
                    });
                    break;
                case 14:
                    int weaponFireID = reader.ReadInt16();
                    int idx2 = reader.ReadByte();
                    AddTask(delegate()
                    {
                        Entity e = level.GetEntityByID(weaponFireID);
                        if (e != null && e is HumanoidEntity)
                            ((HumanoidEntity)e).stance.Fire();
                        else
                            Util.Debug(level, "#14 Invalid entity type: " + weaponFireID);
                    });
                    break;
                case 15:
                    int explodeFrame = reader.ReadInt32();
                    Vec2 pos = helper.ReadVec2();
                    int radius = reader.ReadInt16();
                    int randomSeed = reader.ReadInt32();
                    bool nonlethal = reader.ReadBoolean();
                    AddTask(delegate()
                    {
                        level.Explode((int)pos.X, (int)pos.Y, radius, randomSeed, nonlethal, null, null);
                    }, explodeFrame, null);
                    break;
                case 18:
                    Vec2 playerPos = Util.Round(helper.ReadVec2());
                    AddTask(delegate()
                    {
                        player.Entity.Position = playerPos;
                    });
                    break;
                case 20:
                    int hitscanID = reader.ReadInt16();
                    AddTask(delegate()
                    {
                        Entity e = level.GetEntityByID(hitscanID);
                        if (e != null && e is HumanoidEntity)
                            level.AddHitscan(((HumanoidEntity)e).Weapon.GetPivot(), ((HumanoidEntity)e).ArmAngle, e);
                        else
                            Util.Debug(level, "#20 Invalid entity type: " + hitscanID);
                    });
                    break;
                case 21:
                    int playerClassID = reader.ReadInt16();
                    PlayerClass playerClass = (PlayerClass)helper.ReadEnum(typeof(PlayerClass));
                    int numClassWeapons = reader.ReadByte();
                    Weapon[] classWeapons = new Weapon[numClassWeapons];
                    for (int i = 0; i < numClassWeapons; i++)
                    {
                        classWeapons[i] = (Weapon)Activator.CreateInstance(helper.ReadEntityType());
                    }
                    AddTask(delegate()
                    {
                        Player classPlayer = GetPlayer(playerClassID);
                        if (classPlayer != null)
                        {
                            player.GetGameMode().SetClass(classPlayer, playerClass);
                            classPlayer.Inventory = classWeapons;
                            classPlayer.SelectWeapon(0);
                        }
                        else
                            Util.Debug(level, "#21 Null player: " + playerClassID);
                    });
                    break;
                case 22:
                    int hFrame = reader.ReadInt32();
                    int pID = reader.ReadInt16();
                    int hID = reader.ReadInt16();
                    AddTask(delegate()
                    {
                        Entity e = level.GetEntityByID(pID);
                        if (e != null && e is HumanoidEntity) 
                            ((HumanoidEntity)e).SetGrapplingHook(hID == -1 ? null : (GrapplingHook)level.GetEntityByID(hID));
                    }, hFrame, null);
                    break;
                case 30:
                    int f = cFrame = helper.ReadFrameByte(cFrame);
                    AddTask(delegate()
                    {
                        view.SetFrame(f);
                    });
                    int pCount = reader.ReadInt16();
                    for (int i = 0; i < pCount; i++)
                    {
                        int id = reader.ReadInt16();
                        Vec2 position = helper.ReadVec2();
                        BitArray movement = helper.ReadMovementData();
                        AddTask(delegate()
                        {
                            Entity e = level.GetEntityByID(id);
                            if (e != null)
                            {
                                if (e.player == player)
                                {
                                    e.Position = position;
                                }
                                else
                                {
                                    e.SetTargetPosition(position);
                                    if (e is LivingEntity)
                                    {
                                        LivingEntity l = (LivingEntity)e;
                                        l.moving = movement[0];
                                        l.direction = movement[1];
                                        l.jumping = movement[2];
                                    }
                                }
                            }
                            else
                                Util.Debug(level, "#30 Tried to set position of unknown entity: " +id);
                        });
                    }
                    break;
                case 31:
                    int vCount = reader.ReadInt16();
                    for (int i = 0; i < vCount; i++)
                    {
                        int id = reader.ReadInt16();
                        Vec2 velocity = helper.ReadVec2();
                        BitArray movement = helper.ReadMovementData();
                        AddTask(delegate()
                        {
                            Entity e = level.GetEntityByID(id);
                            if (e != null)
                            {
                                e.Velocity = velocity;
                                if(e is LivingEntity)
                                {
                                    LivingEntity l = (LivingEntity)e;
                                    l.moving = movement[0];
                                    l.direction = movement[1];
                                    l.jumping = movement[2];
                                }
                            }
                            else
                                Util.Debug(level, "#31 Tried to set velocity of unknown entity: " + id);
                        });
                    }
                    break;
                case 32:
                    int aCount = reader.ReadInt16();
                    for (int i = 0; i < aCount; i++)
                    {
                        int id = reader.ReadInt16();
                        float armAngle = helper.ReadAngle();
                        AddTask(delegate()
                        {
                            Entity e = level.GetEntityByID(id);
                            if (e != null && e is LivingEntity)
                            {
                                ((LivingEntity)e).ArmAngle = armAngle;
                            }
                            else
                                Util.Debug(level, "#32 Tried to set position of unknown entity: " + id);
                        });
                    }
                    break;
                case 40:
                    int createFrame = reader.ReadInt32();
                    short entityID = reader.ReadInt16();
                    // Util.Debug(level, "Create:" + entityID + "@" + createFrame);
                    Entity createEntity = (Entity)Activator.CreateInstance(helper.ReadEntityType());
                    Vec2 entityPosition = helper.ReadVec2();
                    bool isPlayer = reader.ReadBoolean();
                    createEntity.ID = entityID;
                    createEntity.Position = entityPosition;
                    createEntity.isPlayer = isPlayer;
                    AddTask(delegate()
                    {
                        level.AddEntity(createEntity);
                        createEntity.enablePhysics = false;
                        AddTask(delegate()
                        {
                            level.EnableEntity(createEntity);
                        }, createFrame, createEntity);
                    });
                    break;
                case 41:
                    int removeFrame = reader.ReadInt32();
                    int removeEntityID = reader.ReadInt16();
                    // Util.Debug(level, "Remove:" + removeEntityID + "@" + removeFrame);
                    AddTask(delegate()
                    {
                        Entity e = level.GetEntityByID(removeEntityID);
                        if(e != null)
                            level.RemoveEntity(e);
                        else
                            Util.Debug(level, "#30 Tried to set remove unknown entity: " + removeEntityID);
                        if (e is HumanoidEntity && ((HumanoidEntity)e).player != null)
                        {
                            Player pl = ((HumanoidEntity)e).player;
                            players.Remove(pl);
                            if (Vexillum.game.View != null && Vexillum.game.View is GameView)
                                ((GameView)Vexillum.game.View).UpdateScoreboard();
                            player.GetGameMode().PlayerRemoved(pl);
                        }
                    }, removeFrame, null);
                    break;
                case 42:
                    int projectileFrame = reader.ReadInt32();
                    short projectileID = reader.ReadInt16();
                    Util.Debug(level, "Create:" + projectileID + "@" + projectileFrame);
                    Projectile createProjectile = (Projectile)Activator.CreateInstance(helper.ReadEntityType());
                    Vec2 projectilePosition = helper.ReadVec2();
                    createProjectile.ID = projectileID;
                    createProjectile.Position = projectilePosition;
                    float angle = reader.ReadSingle();
                    int ownerID = reader.ReadInt16();
                    AddTask(delegate()
                    {
                        Entity owner = level.GetEntityByID(ownerID);
                        if (owner != null)
                        {
                            level.AddProjectile(createProjectile, angle, owner);
                            if (createProjectile is GrapplingHook)
                                ((HumanoidEntity)owner).SetGrapplingHook((GrapplingHook)createProjectile);
                        }
                        else
                            Util.Debug(level, "#42 Tried to add projectile with unknown owner: " + ownerID);
                    }, projectileFrame, createProjectile);
                    break;
                case 60:
                    string chatMessage = reader.ReadString();
                    AddTask(delegate()
                    {
                        view.AddChat(chatMessage);
                    });
                    break;
                case 61:
                    int messageID = reader.ReadByte();
                    int nArgs = reader.ReadByte();
                    string[] args = new string[nArgs];
                    for (int i = 0; i < nArgs; i++)
                    {
                        args[i] = reader.ReadString();
                    }
                    int messageTime = reader.ReadInt16();
                    if(view != null)
                        AddTask(delegate()
                        {
                            view.Message(Messages.Parse(messageID, args), messageTime);
                        });
                    break;
                case 98:
                    int soundFrame = reader.ReadInt32();
                    byte soundID = reader.ReadByte();
                    short soundEntityID = reader.ReadInt16();
                    AddTask(delegate()
                    {
                        Entity soundEntity = level.GetEntityByID(soundEntityID);
                        if(soundEntity != null)
                            level.PlaySound(null, AssetManager.GetSound(soundID), soundEntity);
                    }, soundFrame, null);
                    break;
                case 110:
                    int healthPlayer = reader.ReadInt16();
                    float newHealth = reader.ReadSingle();
                    AddTask(delegate()
                    {
                        HumanoidEntity e = ((HumanoidEntity)level.GetEntityByID(healthPlayer));
                        e.Health = newHealth;
                        if(newHealth != e.MaxHealth)
                            for (int i = 0; i < 8; i++)
                            {
                                TerrainParticle pt = new TerrainParticle(level, System.Drawing.Color.Red, (int)e.Position.X, (int) e.Position.Y);
                                pt.velocity = new Vec2(level.random.Next(5) - 2, 3);
                                level.AddTerrainParticle(pt);
                            }
                        player.GetGameMode().PlayerHealthChanged(e.player, null);
                    });
                    break;
                case 120:
                    byte c0 = reader.ReadByte();
                    byte v0 = reader.ReadByte();
                    AddTask(delegate()
                    {
                        player.GetGameMode().ProcessCommand(c0, v0);
                    });
                    break;
                case 121:
                    byte c1 = reader.ReadByte();
                    short v1 = reader.ReadInt16();
                    AddTask(delegate()
                    {
                        player.GetGameMode().ProcessCommand(c1, v1);
                    });
                    break;
                case 122:
                    byte c2 = reader.ReadByte();
                    string v2 = reader.ReadString();
                    AddTask(delegate()
                    {
                        player.GetGameMode().ProcessCommand(c2, v2);
                    });
                    break;
                case 253:
                    Disconnect(true);
                    ShowLoadingScreen("Waiting for the server...");
                    Vexillum.game.ConnectWhenServerReady(ip, port, 20);
                    break;
                case 254:
                    Disconnect("Disconnected by server: " + reader.ReadString());
                    break;
                case 130:
                    int nPings = reader.ReadByte();
                    Dictionary<int, int> pings = new Dictionary<int, int>(12);
                    for (int i = 0; i < nPings; i++)
                    {
                        pings[reader.ReadInt16()] = reader.ReadInt16();
                    }
                    AddTask(delegate()
                    {
                        for(int i = 0; i < nPings; i++)
                        {
                            Player pingPlayer = level.GetEntityByID(pings.Keys.ElementAt(i)).player;
                            pingPlayer.pingString = "" + pings.Values.ElementAt(i);
                        }
                    });
                    break;
                case 131:
                    int scoreID = reader.ReadInt16();
                    int score = reader.ReadInt16();
                    AddTask(delegate()
                    {
                        Player scorePlayer = level.GetEntityByID(scoreID).player;
                        scorePlayer.Score = score;
                    });
                    break;
                case 220:
                    int levelLength = reader.ReadInt32();
                    levelBytes = new byte[levelLength];
                    break;
                case 221:
                    int chunkLength = reader.ReadInt16();
                    int levelStart = levelPos;
                    while(levelPos < levelStart+chunkLength)
                    {
                        levelBytes[levelPos] = reader.ReadByte();
                        levelPos++;
                    }
                    break;
                case 222:
                    try
                    {
                        using (FileStream levelOut = File.Open(Path.Combine("Maps", mapName + ".map"), FileMode.Create))
                        {
                            new BinaryWriter(levelOut).Write(LevelLoader.MagicNumber);
                            levelOut.Write(levelBytes, 0, levelBytes.Length);
                            levelBytes = null;
                        }
                        SendInfoResponse();
                    }
                    catch (Exception ex)
                    {
                        Disconnect("Could not install map.");
                    }
                    break;
                default:
                    Disconnect("Invalid command: " + cmd);
                    break;
            }
        }
        private void SendPing()
        {
            lock (tasks)
            {
                writer.Write((byte)0);
                WriteData();
            }
            pingSendTime = timer.ElapsedMilliseconds;
        }
        private void SendLogin(ulong steamId, string name, byte[] authTicket, int authTicketLength)
        {
            lock (tasks)
            {
                writer.Write((byte)1);
                writer.Write(steamId);
                writer.Write(name);
                writer.Write(authTicketLength);
                writer.Write(authTicket, 0, authTicketLength);
                WriteData();
            }
        }
        private void SendStatus(bool ready, byte[] mapmd5)
        {
            lock (tasks)
            {
                writer.Write((byte)2);
                writer.Write(ready);
                writer.Write(mapmd5);
                WriteData();
            }
        }
        public void SendWeaponActivate(int btn, int down)
        {
            lock (tasks)
            {
                writer.Write((byte)11);
                writer.Write((byte)btn);
                writer.Write((byte)down);
                writer.Write((float)player.ArmAngle);
                WriteData();
            }
        }
        public void SendWeaponAction(int idx, KeyAction k)
        {
            lock (tasks)
            {
                writer.Write((byte)12);
                helper.WriteAction(k);
                WriteData();
            }
        }
        public void SendWeaponSelect(int idx)
        {
            lock (tasks)
            {
                writer.Write((byte)13);
                writer.Write((byte)idx);
                WriteData();
            }
        }
        public void SendPosition(bool sendMovement)
        {
            Vec2 intPosition = Util.Round(player.Position);
            if (sendMovement)
            {
            }
            if (sendPosition == intPosition)
            {
                lock (tasks)
                {
                    writer.Write(sendMovement ? (byte)15 : (byte)14);
                    if (sendMovement)
                        SendMovement();
                    sendPosition = intPosition;
                    WriteData();
                }
            }
            else if (sendPosition != Vec2.Zero && Math.Abs(sendPosition.X - intPosition.X) < 127 && Math.Abs(sendPosition.Y - intPosition.Y) < 127)
            {
                lock (tasks)
                {
                    writer.Write(sendMovement ? (byte)17 : (byte)16);
                    writer.Write((sbyte)(intPosition.X - sendPosition.X));
                    writer.Write((sbyte)(intPosition.Y - sendPosition.Y));
                    if (sendMovement)
                        SendMovement();
                    sendPosition = intPosition;
                    WriteData();
                }
            }
            else
            {
                lock (tasks)
                {
                    writer.Write(sendMovement ? (byte)19 : (byte)18);
                    helper.WriteVec2(intPosition);
                    if (sendMovement)
                        SendMovement();
                    sendPosition = intPosition;
                    WriteData();
                }
            }
            sendPosition = intPosition;
        }
        private void SendMovement()
        {
            helper.WriteAngle(player.ArmAngle);
            helper.WriteMovementData(player.Entity.moving, player.Entity.direction, player.Entity.jumping);
        }
        public void SendHitscan()
        {
            lock (tasks)
            {
                if (player.Entity != null)
                {
                    writer.Write((byte)20);
                    helper.WriteAngle(player.ArmAngle);
                    helper.WriteVec2(player.Entity.Weapon.GetPivot());
                    WriteData();
                }
            }
        }
        public void SendChat(string msg)
        {
            if (msg.StartsWith("/lag "))
            {
                int ms = int.Parse(msg.Split(' ')[1]);
                tasks.testLagTime = ms;
            }
            lock (tasks)
            {
                writer.Write((byte)60);
                writer.Write(msg);
                WriteData();
            }
        }
        private void WriteData()
        {
            try
            {
                tasks.Send(writebuffer, false);
            }
            catch (IOException ex)
            {
                Util.Debug(level, "Disconnected due to exception: " + ex);
                if(!(ex is IOException))
                    Util.Debug(ex.StackTrace);
                Disconnect();
            }
        }

        private void SendPlayerPosition()
        {
            while (connected)
            {
                try
                {
                    Thread.Sleep(100);
                    if (player.Entity != null)
                    {
                        if (player.Entity.movementChanged)
                        {
                            SendPosition(true);
                            player.Entity.movementChanged = false;
                        }
                        else if (player.ArmAngle != sendAngle)
                        {
                            SendPosition(true);
                            sendAngle = player.ArmAngle;
                        }
                        else
                            SendPosition(false);
                    }
                }
                catch (Exception ex)
                {
                    return;
                }
            }
        }

        private void Ping()
        {
            while (connected)
            {
                try
                {
                    Thread.Sleep(5000);
                    if (timer.ElapsedMilliseconds - pingReceiveTime > 15000)
                        Disconnect("Lost connection to the server");
                }
                catch (Exception ex)
                {
                    return;
                }
            }
        }

        private Player GetPlayer(int id)
        {
            if (id == -1)
                return player;
            foreach (Player localPlayer in players)
            {
                if (localPlayer.GetID() == id)
                {
                    return localPlayer;
                }
            }
            return null;
        }

        public bool IsConnected()
        {
            return connected;
        }

        private void AddTask(TaskDelegate t)
        {
            if (view == null)
                t();
            else
                view.AddTask(t);
        }

        private void AddTask(TaskDelegate t, int frame, Entity e)
        {
            if (view != null)
            {
                view.AddTask(t, frame, e);
            }
        }
    }
}
