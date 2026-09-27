using System;
using System.Collections.Generic;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>Both maps in rotation, "alice" listed in Server/ops.txt before the start.</summary>
    public class OpsFixture : ServerFixture
    {
        public const string Op = "alice";

        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases complex");
            runtime.SetPlayerList("ops", Op);
        }
    }

    /// <summary>
    /// /newgame by an op: packet 253 to everyone, sockets closed, the server
    /// reloads on the new map, entity ids restart and bots are re-added.
    /// </summary>
    public class LevelChangeTests : IClassFixture<OpsFixture>
    {
        private readonly OpsFixture fx;

        public LevelChangeTests(OpsFixture fx)
        {
            this.fx = fx;
        }

        // PROTO-22 (and the unobservable ready=false half of PROTO-01)
        [Fact]
        public void Newgame_sends_253_closes_everyone_and_the_server_comes_back_on_the_new_map()
        {
            string bob = Names.Unique("bob");
            string before, target;
            int idBeforeB;
            DateTime changing;
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame(OpsFixture.Op);
                b.JoinGame(bob);
                Assert.True(fx.Server.Console.ServerPlayer(OpsFixture.Op).IsOp);
                Assert.False(fx.Server.Console.ServerPlayer(bob).IsOp);
                before = a.MapName;
                target = before == "bases" ? "complex" : "bases";
                // ids in use: the flags took 2 and 3, the humans and bots everything after
                idBeforeB = b.MyEntityId;
                Assert.True(idBeforeB > 3, "b's entity id " + idBeforeB);

                int ma = a.PacketCount, mb = b.PacketCount;
                a.SendChat("/newgame " + target);
                a.WaitFor<LevelChangingPacket>(null, TimeSpan.FromSeconds(10), ma);
                changing = DateTime.UtcNow;
                b.WaitFor<LevelChangingPacket>(null, TimeSpan.FromSeconds(10), mb);
                Assert.True(a.WaitForClose(TimeSpan.FromSeconds(10)), "op's socket closed after 253");
                Assert.True(b.WaitForClose(TimeSpan.FromSeconds(10)), "other socket closed after 253");
                Assert.Empty(a.Packets<DisconnectPacket>());
                Assert.Empty(b.Packets<DisconnectPacket>());
                Assert.DoesNotContain(a.Packets<ChatPacket>(ma), p => p.Text.Contains("need to be op"));
            }

            // A probe accepted while the level reloads is only answered once the new
            // level is up (the ServerPlayer for it is constructed by the Server Main
            // thread, which is busy in setLevel), so it never reports false.
            bool? probe = ScriptedClient.Probe("127.0.0.1", fx.Server.Port, TimeSpan.FromSeconds(20));
            Assert.Equal(true, probe);
            Assert.True((DateTime.UtcNow - changing).TotalSeconds < 20);
            Assert.Equal(target, fx.Server.Console.ServerMap());
            Assert.True(fx.Server.Console.ServerReady());

            // a new client sees the new map, ids restarted from Entity.ResetID, and the bots re-added by AddBots
            string carol = Names.Unique("carol");
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.JoinGame(carol);
                Assert.Equal(target, c.MapName);
                List<EntityListEntry> ents = c.Packets<EntityListPacket>()[0].Entities;
                int minId = short.MaxValue, maxId = 0;
                foreach (EntityListEntry e in ents)
                {
                    minId = Math.Min(minId, e.Id);
                    maxId = Math.Max(maxId, e.Id);
                }
                // The new level's flags are allocated after Entity.ResetID: without the reset they
                // would carry ids above everything allocated before (b's entity included).
                Assert.InRange(minId, 1, 3);
                EntityListEntry blueFlag = ents.Find(e => e.TypeIndex == 0), greenFlag = ents.Find(e => e.TypeIndex == 3);
                Assert.NotNull(blueFlag);
                Assert.NotNull(greenFlag);
                Assert.True(blueFlag.Id <= 3 && greenFlag.Id <= 3, "flags " + blueFlag + " " + greenFlag);
                Assert.True(blueFlag.Id < idBeforeB && greenFlag.Id < idBeforeB, "flag ids " + blueFlag.Id + "/" + greenFlag.Id + " must precede b's old id " + idBeforeB);
                int idAfter = fx.Server.Console.EvalT<int>("Sync(() => (int)(short)Static(\"Vexillum.Entities.Entity\", \"CurrentID\"))");
                Assert.True(maxId <= idAfter, "every id on the wire was allocated after the reset");
                Assert.Equal(c.MyEntityId, fx.Server.Console.ServerPlayer(carol).EntityId);
                // AddBots filled min(maxBots, maxPlayers) = 6 slots; carol's join trims one (maxBots - 1 human)
                ServerSide side = new ServerSide(fx.Server);
                Assert.True(Poll.Until(() => side.BotNames().Count == 5, TimeSpan.FromSeconds(10)), "bots after the change and one join: " + side.BotNames().Count);
                Assert.True(Poll.Until(() => c.Players.Count >= 6, TimeSpan.FromSeconds(10)), "the bots were announced to carol");
            }
        }
    }
}
