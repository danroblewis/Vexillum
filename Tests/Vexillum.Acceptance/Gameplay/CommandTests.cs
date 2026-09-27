using System;
using System.IO;
using System.Text.RegularExpressions;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    /// <summary>Chat and the slash commands; "alice" is an op through Server/ops.txt, "mallory" is banned through Server/banned.txt.</summary>
    public class CommandsFixture : GameplayFixture
    {
        protected override void Settings(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases complex");
            runtime.SetServerSetting("respawntime", "300");
            runtime.SetPlayerList("ops", "alice");
            runtime.SetPlayerList("banned", "mallory");
        }
    }

    public class CommandTests : IClassFixture<CommandsFixture>
    {
        private readonly CommandsFixture fx;

        public CommandTests(CommandsFixture fx)
        {
            this.fx = fx;
        }

        /// <summary>PlayerList is internal to the server assembly: reach Check(list, name) through reflection.</summary>
        private static string BannedCheck(string name)
        {
            return "(bool)typeof(Server.Server).Assembly.GetType(\"Server.PlayerList\").GetMethod(\"Check\").Invoke(null, new object[] { \"banned\", \"" + name + "\" })";
        }

        private static string Colour(PlayerClass c)
        {
            return c == PlayerClass.Green ? TextUtil.COLOR_GREEN : c == PlayerClass.Blue ? TextUtil.COLOR_BLUE : TextUtil.COLOR_GRAY;
        }

        // chat 60 broadcast with the team colour prefix
        [Fact]
        public void Chat_is_broadcast_to_everyone_with_the_senders_team_colour()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("chatter")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("listener")))
            {
                int mA = a.PacketCount, mB = b.PacketCount;
                a.SendChat("hello there");
                string expected = Colour(a.Me.Class) + a.Name + TextUtil.COLOR_WHITE + "> hello there";
                Assert.Equal(expected, a.WaitFor<ChatPacket>(p => p.Text.EndsWith("hello there"), TimeSpan.FromSeconds(5), mA).Text);
                Assert.Equal(expected, b.WaitFor<ChatPacket>(p => p.Text.EndsWith("hello there"), TimeSpan.FromSeconds(5), mB).Text);
                Assert.NotNull(fx.Server.WaitFor(Regex.Escape(a.Name + ": hello there"), 5));
                // an unknown command answers only the sender
                int mA2 = a.PacketCount, mB2 = b.PacketCount;
                a.SendChat("/frobnicate");
                Assert.Equal(TextUtil.COLOR_ORANGE + "Unknown command: /frobnicate", a.WaitFor<ChatPacket>(null, TimeSpan.FromSeconds(5), mA2).Text);
                Assert.True(b.NoneWithin<ChatPacket>(null, TimeSpan.FromSeconds(1), mB2));
                fx.Leave(b);
                fx.Leave(a);
            }
        }

        // SRV-15
        [Fact]
        public void Op_status_from_ops_txt_gates_newgame_op_and_deop()
        {
            using (ScriptedClient a = fx.Join("alice"))
            using (ScriptedClient b = fx.Join("bob"))
            {
                Assert.True(fx.IsOp("alice"));
                Assert.False(fx.IsOp("bob"));

                int mB = b.PacketCount;
                a.SendChat("/op bob");
                Assert.Equal(TextUtil.COLOR_ORANGE + "You're now an op!", b.WaitFor<ChatPacket>(null, TimeSpan.FromSeconds(5), mB).Text);
                Assert.True(fx.IsOp("bob"));
                // PlayerList.Save opens ops.txt in the working directory with FileMode.Truncate; the file only
                // exists under Server/, so the save fails and is logged; the list lives on in memory only
                Assert.NotNull(fx.Server.WaitFor("Error saving ops list", 5));
                Assert.False(File.Exists(Path.Combine(fx.Runtime.Root, "ops.txt")), "no ops.txt written in the runtime root");
                Assert.Equal("alice\n", File.ReadAllText(Path.Combine(fx.Runtime.ServerDir, "ops.txt")));

                // the new op changes the level: 253 to everyone, then the sockets are closed.
                // (alice leaves first: a level change with two connected humans can hang UpdateBots, see the fixture)
                fx.Leave(a);
                fx.PrepareLevelChange(b);
                mB = b.PacketCount;
                b.SendChat("/newgame complex");
                b.WaitFor<LevelChangingPacket>(null, TimeSpan.FromSeconds(10), mB);
                Assert.True(b.WaitForClose(TimeSpan.FromSeconds(10)));
                GameplayFixture.WaitUntil(() => fx.Console.ServerReady() && fx.Console.ServerMap() == "complex", TimeSpan.FromSeconds(30), "level changed to complex");
                Assert.Equal(0, fx.BotCount());
            }

            // op status survives the level change (the in-memory list), so /deop is meaningful
            using (ScriptedClient a = fx.Join("alice"))
            using (ScriptedClient b = fx.Join("bob"))
            {
                Assert.Equal("complex", a.MapName);
                Assert.True(fx.IsOp("bob"));
                int mB = b.PacketCount;
                a.SendChat("/deop bob");
                Assert.Equal(TextUtil.COLOR_ORANGE + "You're no longer an op!", b.WaitFor<ChatPacket>(null, TimeSpan.FromSeconds(5), mB).Text);
                Assert.False(fx.IsOp("bob"));

                int mA = a.PacketCount;
                mB = b.PacketCount;
                b.SendChat("/newgame");
                Assert.Equal(TextUtil.COLOR_ORANGE + "You need to be op to do that!", b.WaitFor<ChatPacket>(null, TimeSpan.FromSeconds(5), mB).Text);
                Assert.True(a.NoneWithin<LevelChangingPacket>(null, TimeSpan.FromSeconds(2), mA), "no level change for a non-op");
                Assert.Equal("complex", fx.Console.ServerMap());

                int errors = fx.Server.Count("Error: Level nomap does not exist");
                a.SendChat("/newgame nomap");
                Assert.True(fx.Server.WaitForCount(Regex.Escape("Error: Level nomap does not exist."), errors + 1, TimeSpan.FromSeconds(5)));
                Assert.True(a.NoneWithin<LevelChangingPacket>(null, TimeSpan.FromSeconds(2), mA), "no level change for a missing map");
                Assert.Equal("complex", fx.Console.ServerMap());
                fx.Leave(b);
                fx.Leave(a);
            }
        }

        // SRV-28 (known original bug, preserved): PlayerList.Save opens <list>.txt in the working directory with
        // FileMode.Truncate while Load reads Server/<list>.txt, so no list change ever reaches the disk
        [Fact]
        public void Op_deop_ban_and_unban_change_the_lists_in_memory_only_and_never_the_files()
        {
            string opsFile = Path.Combine(fx.Runtime.ServerDir, "ops.txt");
            string bannedFile = Path.Combine(fx.Runtime.ServerDir, "banned.txt");
            string opsBefore = File.ReadAllText(opsFile), bannedBefore = File.ReadAllText(bannedFile);
            Assert.False(File.Exists(Path.Combine(fx.Runtime.Root, "ops.txt")), "precondition: no ops.txt in the working directory");
            Assert.False(File.Exists(Path.Combine(fx.Runtime.Root, "banned.txt")), "precondition: no banned.txt in the working directory");
            string other = GameplayFixture.Unique("fred");
            string ghost = GameplayFixture.Unique("ghost");
            using (ScriptedClient a = fx.Join("alice"))
            using (ScriptedClient f = fx.Join(other))
            {
                Assert.True(fx.IsOp("alice"));
                Assert.False(fx.IsOp(other));
                Assert.False(fx.Console.EvalT<bool>(BannedCheck(ghost)));
                int opsErrors = fx.Server.Count("Error saving ops list");
                int bannedErrors = fx.Server.Count("Error saving banned list");

                // /op and /ban: the in-memory lists change, the save fails and is logged
                int mF = f.PacketCount;
                a.SendChat("/op " + other);
                Assert.Equal(TextUtil.COLOR_ORANGE + "You're now an op!", f.WaitFor<ChatPacket>(null, TimeSpan.FromSeconds(5), mF).Text);
                Assert.True(fx.IsOp(other));
                Assert.True(fx.Server.WaitForCount("Error saving ops list", opsErrors + 1, TimeSpan.FromSeconds(5)), "ops save failure logged");
                a.SendChat("/ban " + ghost);
                GameplayFixture.WaitUntil(() => fx.Console.EvalT<bool>(BannedCheck(ghost)), TimeSpan.FromSeconds(5), ghost + " banned in memory");
                Assert.True(fx.Server.WaitForCount("Error saving banned list", bannedErrors + 1, TimeSpan.FromSeconds(5)), "banned save failure logged");
                // the failure is the Truncate open of a file that does not exist in the working directory
                Assert.Contains("FileNotFoundException", fx.Server.Lines("Error saving ops list")[opsErrors]);
                Assert.Contains("FileNotFoundException", fx.Server.Lines("Error saving banned list")[bannedErrors]);

                // /deop and /unban save (and fail) the same way
                mF = f.PacketCount;
                a.SendChat("/deop " + other);
                Assert.Equal(TextUtil.COLOR_ORANGE + "You're no longer an op!", f.WaitFor<ChatPacket>(null, TimeSpan.FromSeconds(5), mF).Text);
                Assert.False(fx.IsOp(other));
                Assert.True(fx.Server.WaitForCount("Error saving ops list", opsErrors + 2, TimeSpan.FromSeconds(5)), "second ops save failure logged");
                a.SendChat("/unban " + ghost);
                GameplayFixture.WaitUntil(() => !fx.Console.EvalT<bool>(BannedCheck(ghost)), TimeSpan.FromSeconds(5), ghost + " unbanned in memory");
                Assert.True(fx.Server.WaitForCount("Error saving banned list", bannedErrors + 2, TimeSpan.FromSeconds(5)), "second banned save failure logged");

                // the op still gates commands through the in-memory list: alice keeps her op from Server/ops.txt
                int mA = a.PacketCount;
                a.SendChat("/op " + other);
                Assert.True(fx.Server.WaitForCount("Error saving ops list", opsErrors + 3, TimeSpan.FromSeconds(5)));
                Assert.True(fx.IsOp(other));
                Assert.True(a.NoneWithin<ChatPacket>(p => p.Text.EndsWith("You need to be op to do that!"), TimeSpan.FromSeconds(0.5), mA));

                // nothing reached the disk: the Server/ lists are unchanged and no list file appeared in the working directory
                Assert.Equal(opsBefore, File.ReadAllText(opsFile));
                Assert.Equal(bannedBefore, File.ReadAllText(bannedFile));
                Assert.DoesNotContain(other, File.ReadAllText(opsFile));
                Assert.DoesNotContain(ghost, File.ReadAllText(bannedFile));
                Assert.False(File.Exists(Path.Combine(fx.Runtime.Root, "ops.txt")), "no ops.txt written in the runtime root");
                Assert.False(File.Exists(Path.Combine(fx.Runtime.Root, "banned.txt")), "no banned.txt written in the runtime root");
                fx.Leave(f);
                fx.Leave(a);
            }
        }

        // SRV-16 (known original bug: the loop compares the issuer's own name)
        [Fact(Skip = "Known original bug: Server.Chat /kick compares p.name (the issuer) instead of other.name, so /kick <other> never disconnects the target, docs/PORTING.md")]
        public void Kick_disconnects_the_named_player()
        {
            using (ScriptedClient a = fx.Join("alice"))
            using (ScriptedClient c = fx.Join("carl"))
            {
                fx.ExpectDisconnect(c);
                a.SendChat("/kick carl");
                DisconnectPacket d = c.WaitFor<DisconnectPacket>(null, TimeSpan.FromSeconds(5));
                Assert.Equal("You were kicked", d.Reason);
                fx.Leave(a);
            }
        }

        [Fact(Skip = "Known original bug: Server.Chat /ban compares p.name (the issuer) instead of other.name, so /ban <other> records the name but never disconnects the target, docs/PORTING.md")]
        public void Ban_disconnects_the_named_player()
        {
            using (ScriptedClient a = fx.Join("alice"))
            using (ScriptedClient c = fx.Join("carl"))
            {
                fx.ExpectDisconnect(c);
                a.SendChat("/ban carl");
                DisconnectPacket d = c.WaitFor<DisconnectPacket>(null, TimeSpan.FromSeconds(5));
                Assert.Equal("You've been banned!", d.Reason);
                fx.Leave(a);
            }
        }

        // SRV-16: what the commands do today
        [Fact]
        public void Kick_and_ban_leave_the_target_connected_but_ban_blocks_their_next_login()
        {
            using (ScriptedClient a = fx.Join("alice"))
            using (ScriptedClient c = fx.Join("carl"))
            {
                int mC = c.PacketCount, mA = a.PacketCount;
                a.SendChat("/kick carl");
                Assert.True(c.NoneWithin<DisconnectPacket>(null, TimeSpan.FromSeconds(2), mC), "target not kicked (issuer-name comparison)");
                Assert.True(a.NoneWithin<DisconnectPacket>(null, TimeSpan.FromSeconds(0.1), mA), "issuer not kicked either: its name is not 'carl'");
                Assert.True(fx.PlayerExists("carl"));

                a.SendChat("/ban carl");
                Assert.True(c.NoneWithin<DisconnectPacket>(null, TimeSpan.FromSeconds(2), mC), "target stays connected");
                Assert.True(fx.Console.EvalT<bool>(BannedCheck("carl")));
                Assert.NotNull(fx.Server.WaitFor("Error saving banned list", 5));
                Assert.False(File.Exists(Path.Combine(fx.Runtime.Root, "banned.txt")));
                fx.Leave(c);

                // the ban is enforced at the next login
                using (ScriptedClient again = new ScriptedClient(fx.Server))
                {
                    again.Login(again.SteamId, "carl");
                    DisconnectPacket d = again.WaitFor<DisconnectPacket>(null, TimeSpan.FromSeconds(5));
                    Assert.Equal("You're banned!", d.Reason);
                    Assert.True(again.WaitForClose(TimeSpan.FromSeconds(5)));
                }
                // /unban lifts it
                a.SendChat("/unban carl");
                GameplayFixture.WaitUntil(() => !fx.Console.EvalT<bool>(BannedCheck("carl")), TimeSpan.FromSeconds(5), "unbanned");
                using (ScriptedClient back = fx.Join("carl"))
                {
                    Assert.Equal("carl", back.Me.Name);
                    fx.Leave(back);
                }
                fx.Leave(a);
            }
        }

        // SRV-16: the issuer's own name is the only one the loop can match
        [Fact]
        public void Kicking_your_own_name_disconnects_yourself()
        {
            using (ScriptedClient a = fx.Join("alice"))
            using (ScriptedClient d = fx.Join(GameplayFixture.Unique("dave")))
            {
                int mD = d.PacketCount;
                fx.ExpectDisconnect(a);
                a.SendChat("/kick alice");
                DisconnectPacket dc = a.WaitFor<DisconnectPacket>(null, TimeSpan.FromSeconds(5));
                Assert.Equal("You were kicked", dc.Reason);
                Assert.True(a.WaitForClose(TimeSpan.FromSeconds(5)));
                d.WaitFor<EntityRemovePacket>(p => p.EntityId == a.MyEntityId, TimeSpan.FromSeconds(5), mD);
                Assert.Equal(TextUtil.COLOR_ORANGE + "alice left the game", d.WaitFor<ChatPacket>(p => p.Text.EndsWith("left the game"), TimeSpan.FromSeconds(5), mD).Text);
                GameplayFixture.WaitUntil(() => !fx.PlayerExists("alice"), TimeSpan.FromSeconds(5), "alice removed");
                fx.Leave(d);
            }
        }

        // non-ops are refused
        [Fact]
        public void Op_commands_are_refused_for_non_ops()
        {
            using (ScriptedClient e = fx.Join(GameplayFixture.Unique("eve")))
            {
                foreach (string cmd in new string[] { "/newgame", "/ban x", "/unban x", "/kick x", "/op x", "/deop x" })
                {
                    int m = e.PacketCount;
                    e.SendChat(cmd);
                    Assert.NotNull(e.WaitFor<ChatPacket>(p => p.Text == TextUtil.COLOR_ORANGE + "You need to be op to do that!", TimeSpan.FromSeconds(5), m));
                    Assert.Equal("bases complex", fx.Runtime.GetServerSetting("maps"));
                }
                Assert.False(fx.IsOp(e.Name));
                fx.Leave(e);
            }
        }

        // SRV-23
        [Fact]
        public void Rejected_connections_broadcast_a_phantom_leave_message()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("idle")))
            {
                Assert.Equal(1, fx.Console.EvalT<int>("Sync(() => Server.gameMode.numPlayers)"));
                // (a) a status probe is an accepted TcpClient that becomes a ServerPlayer named "unknown"
                int mA = a.PacketCount;
                Assert.Equal(true, ScriptedClient.Probe("127.0.0.1", fx.Server.Port, TimeSpan.FromSeconds(5)));
                ChatPacket unknown = a.WaitFor<ChatPacket>(p => p.Text.EndsWith("left the game"), TimeSpan.FromSeconds(5), mA);
                Assert.Equal(TextUtil.COLOR_ORANGE + "unknown left the game", unknown.Text);
                // PlayerRemoved decrements numPlayers and UpdatePlayerList recounts it from Server.players at once
                GameplayFixture.WaitUntil(() => !fx.PlayerExists("unknown"), TimeSpan.FromSeconds(5), "probe connection removed");
                Assert.Equal(1, fx.Console.EvalT<int>("Sync(() => Server.gameMode.numPlayers)"));

                // (b) a banned name is refused after the login packet, and its removal is announced as well
                int mA2 = a.PacketCount;
                using (ScriptedClient m = new ScriptedClient(fx.Server))
                {
                    m.Login(m.SteamId, "mallory");
                    Assert.Equal("You're banned!", m.WaitFor<DisconnectPacket>(null, TimeSpan.FromSeconds(5)).Reason);
                }
                ChatPacket mallory = a.WaitFor<ChatPacket>(p => p.Text.EndsWith("left the game"), TimeSpan.FromSeconds(5), mA2);
                Assert.Equal(TextUtil.COLOR_ORANGE + "mallory left the game", mallory.Text);
                GameplayFixture.WaitUntil(() => !fx.PlayerExists("mallory"), TimeSpan.FromSeconds(5), "rejected login removed");
                Assert.Equal(1, fx.Console.EvalT<int>("Sync(() => Server.gameMode.numPlayers)"));
                Assert.Equal(1, fx.HumanCount());
                fx.Leave(a);
            }
        }
    }
}
