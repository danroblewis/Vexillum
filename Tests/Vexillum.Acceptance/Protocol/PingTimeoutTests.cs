using System;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// The ping keepalive: a client that never answers packet 0 is dropped at
    /// the next ping tick, without a 254, and the others are told it left.
    /// </summary>
    public class PingTimeoutTests : IClassFixture<DuoBasesFixture>
    {
        private readonly DuoBasesFixture fx;

        public PingTimeoutTests(DuoBasesFixture fx)
        {
            this.fx = fx;
        }

        // PROTO-10 (b)
        [Fact]
        public void Client_that_never_answers_pings_is_disconnected_at_the_next_ping_tick()
        {
            string alice = Names.Unique("alice"), bob = Names.Unique("bob");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                b.AutoPing = false;
                b.JoinGame(bob);
                short idB = b.MyEntityId;
                int ma = a.PacketCount;

                PingPacket first = b.WaitFor<PingPacket>(null, TimeSpan.FromSeconds(7));
                Assert.True(b.WaitForClose(TimeSpan.FromSeconds(11)), "b should have been dropped by the ping check");
                double after = (DateTime.UtcNow - first.ReceivedAt).TotalSeconds;
                Assert.InRange(after, 4.5, 11);
                Assert.Empty(b.Packets<DisconnectPacket>());     // CheckPingTime calls Disconnect, never SendDisconnect
                Assert.Null(b.Disconnect);
                Assert.Single(b.Packets<PingPacket>());         // no second ping: the check comes first

                Assert.NotNull(fx.Server.WaitFor(bob + " disconnected", 5));
                a.WaitFor<EntityRemovePacket>(r => r.EntityId == idB, TimeSpan.FromSeconds(10), ma);
                a.WaitFor<ChatPacket>(p => p.Text == Colour.Orange + bob + " left the game", TimeSpan.FromSeconds(10), ma);
                // the well-behaved client is still connected and pinged
                Assert.False(a.IsClosed);
                Assert.True(a.Packets<PingPacket>().Count >= 2, "a answered every ping and stays");
                Assert.Null(fx.Server.Console.ServerPlayer(bob));
                Assert.NotNull(fx.Server.Console.ServerPlayer(alice));
            }
        }
    }
}
