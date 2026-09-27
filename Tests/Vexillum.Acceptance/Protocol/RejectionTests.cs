using System;
using System.Collections.Generic;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>bases, default bots, "mallory" banned before the server starts.</summary>
    public class BannedFixture : ServerFixture
    {
        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases");
            runtime.SetPlayerList("banned", "mallory");
        }
    }

    /// <summary>
    /// Login rejections (packet 254 with the exact reason, then EOF): banned,
    /// invalid and duplicate names. The server-full case has its own fixture below.
    /// </summary>
    public class RejectionTests : IClassFixture<BannedFixture>
    {
        private readonly BannedFixture fx;

        public RejectionTests(BannedFixture fx)
        {
            this.fx = fx;
        }

        private static void AssertRejected(ScriptedClient c, string reason)
        {
            DisconnectPacket d = c.WaitFor<DisconnectPacket>(null, 10);
            Assert.Equal(reason, d.Reason);
            Assert.True(c.WaitForClose(TimeSpan.FromSeconds(5)), "socket must be closed after 254");
            Assert.Empty(c.Packets<ServerIdPacket>());
            Assert.Equal(0, d.Sequence);
        }

        // PROTO-06
        [Fact]
        public void Banned_name_is_rejected_at_login()
        {
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(3UL, "mallory");
                AssertRejected(c, "You're banned!");
                Assert.NotNull(fx.Server.WaitFor("Disconnecting mallory: You're banned!", 5));
                Assert.Null(fx.Server.Console.ServerPlayer("mallory"));
            }
        }

        // PROTO-07
        [Fact]
        public void Empty_name_is_rejected_as_invalid()
        {
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(4UL, "");
                AssertRejected(c, "Invalid name");
                Assert.NotNull(fx.Server.WaitFor("Disconnecting : Invalid name", 5));
            }
        }

        // PROTO-07: 127 characters is the longest name the wire can carry (see the MiscUtil length bug)
        [Fact]
        public void Name_of_127_characters_is_accepted()
        {
            string name = new string('x', 120) + Names.Unique("").Substring(0, 7);
            Assert.Equal(127, name.Length);
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(5UL, name);
                ServerIdPacket sid = c.WaitFor<ServerIdPacket>(null, 10);
                Assert.Equal(3, sid.ProtocolVersion);
                Assert.Empty(c.Packets<DisconnectPacket>());
                Assert.NotNull(fx.Server.WaitFor("logged in as " + name, 5));
            }
        }

        [Fact(Skip = "Known original bug: MiscUtil Write7BitEncodedInt inserts a zero byte for lengths >= 128, so a 128-character name is read as an empty name plus garbage ticket length, docs/PORTING.md")]
        public void Name_of_128_characters_is_accepted()
        {
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(6UL, new string('y', 128));
                ServerIdPacket sid = c.WaitFor<ServerIdPacket>(null, 10);
                Assert.Equal(3, sid.ProtocolVersion);
            }
        }

        [Fact(Skip = "Known original bug: MiscUtil Write7BitEncodedInt inserts a zero byte for lengths >= 128, so the server never sees the 129-character name it should reject, docs/PORTING.md")]
        public void Name_of_129_characters_is_rejected_as_invalid()
        {
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(7UL, new string('z', 129));
                AssertRejected(c, "Invalid name");
            }
        }

        // PROTO-08
        [Fact]
        public void Duplicate_name_is_rejected_including_a_bots_name()
        {
            string name = Names.Unique("alice");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            {
                a.JoinGame(name);
                using (ScriptedClient b = new ScriptedClient(fx.Server))
                {
                    b.Login(8UL, name);
                    AssertRejected(b, "This name is in use.");
                    Assert.NotNull(fx.Server.WaitFor("Disconnecting " + name + ": This name is in use.", 5));
                }
                // a is still in the game
                a.SendChat("still me");
                a.WaitFor<ChatPacket>(p => p.Text.EndsWith("> still me"), 5);

                // the duplicate check walks Server.players, which holds the AIPlayers too
                ServerSide side = new ServerSide(fx.Server);
                List<string> bots = null;
                Assert.True(Poll.Until(() => (bots = side.BotNames()).Count > 0, TimeSpan.FromSeconds(10)), "no bots joined with the default maxbots");
                string botName = bots[0];
                Assert.NotNull(a.PlayerNamed(botName));    // the bot was announced to a as a player
                using (ScriptedClient c = new ScriptedClient(fx.Server))
                {
                    c.Login(9UL, botName);
                    AssertRejected(c, "This name is in use.");
                }
            }
        }
    }

    /// <summary>
    /// maxplayers 1, maxbots 1 (no bot is ever added, so a single human never
    /// trips the UpdateBots hang) and a banned list containing an empty line,
    /// which makes the empty name banned as well as invalid.
    /// </summary>
    public class TinyServerFixture : ServerFixture
    {
        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases");
            runtime.SetServerSetting("maxplayers", "1");
            runtime.SetServerSetting("maxbots", "1");
            runtime.SetPlayerList("banned", "");
        }
    }

    public class ServerFullTests : IClassFixture<TinyServerFixture>
    {
        private readonly TinyServerFixture fx;

        public ServerFullTests(TinyServerFixture fx)
        {
            this.fx = fx;
        }

        // PROTO-07 (check order): banned is tested before the name's validity
        [Fact]
        public void Banned_check_precedes_the_invalid_name_check()
        {
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(10UL, "");
                DisconnectPacket d = c.WaitFor<DisconnectPacket>(null, 10);
                Assert.Equal("You're banned!", d.Reason);
                Assert.True(c.WaitForClose(TimeSpan.FromSeconds(5)));
            }
        }

        // PROTO-09 (Server.IsFull uses '>' : players.Count counts every accepted connection, this one included)
        //
        // Server.RunServer enqueues players.Add(new ServerPlayer(...)) on the mutator thread while the
        // ServerPlayer constructor already starts the reader thread, so a login that is already buffered
        // can be checked against IsFull before this connection is counted. To make the outcome
        // deterministic each client waits until Server.players.Count includes its own connection
        // before sending packet 1 (ScriptedClient connects in its constructor, Login is separate).
        [Fact]
        public void Server_full_rejects_the_extra_login_and_precedes_every_other_check()
        {
            string name = Names.Unique("only");
            ServerSide side = new ServerSide(fx.Server);
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            {
                a.JoinGame(name);
                Assert.True(Poll.Until(() => side.PlayerCount() == 1, TimeSpan.FromSeconds(10)), "a not counted");

                // B: once counted, players.Count == 2 > maxplayers 1, so the second human is refused
                // although only one player is actually playing (the known off-by-one).
                using (ScriptedClient b = new ScriptedClient(fx.Server))
                {
                    Assert.True(Poll.Until(() => side.PlayerCount() == 2, TimeSpan.FromSeconds(10)), "b not counted");
                    b.Login(11UL, Names.Unique("second"));
                    DisconnectPacket d = b.WaitFor<DisconnectPacket>(null, 10);
                    Assert.Equal("This server is full.", d.Reason);
                    Assert.True(b.WaitForClose(TimeSpan.FromSeconds(5)));
                    Assert.Empty(b.Packets<ServerIdPacket>());
                }
                Assert.NotNull(fx.Server.WaitFor("Disconnecting .*: This server is full.", 5));
                Assert.True(Poll.Until(() => side.PlayerCount() == 1, TimeSpan.FromSeconds(10)), "b not removed");

                // C: "full" is the first check, so it wins over the banned (and invalid) empty name
                using (ScriptedClient c = new ScriptedClient(fx.Server))
                {
                    Assert.True(Poll.Until(() => side.PlayerCount() == 2, TimeSpan.FromSeconds(10)), "c not counted");
                    c.Login(12UL, "");
                    DisconnectPacket d = c.WaitFor<DisconnectPacket>(null, 10);
                    Assert.Equal("This server is full.", d.Reason);
                    Assert.True(c.WaitForClose(TimeSpan.FromSeconds(5)));
                    Assert.Empty(c.Packets<ServerIdPacket>());
                }
                Assert.True(Poll.Until(() => side.PlayerCount() == 1, TimeSpan.FromSeconds(10)), "c not removed");

                // A is unaffected
                a.SendChat("room for one");
                a.WaitFor<ChatPacket>(p => p.Text.EndsWith("> room for one"), 5);
            }
        }
    }
}
