using System;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// The real game client against a fake server: the two client-side
    /// disconnect paths of Client.ProcessPacket. Needs a display (the game
    /// opens its window), so the class runs in the serial display collection.
    /// </summary>
    [Collection(DisplayCollection.Name)]
    public class ClientSideTests
    {
        private static readonly TimeSpan startup = TimeSpan.FromSeconds(90);

        // PROTO-23
        [Fact]
        public void Client_rejects_a_wrong_protocol_version()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            using (FakeServer fake = new FakeServer(delegate(FakeServer.Connection c)
            {
                c.Writer.Write(Protocol.S2C.ServerId);
                c.Writer.Write((byte)4);          // PROTOCOL_VERSION is 3
                c.Writer.Write("bases");
                c.Writer.Flush();
            }))
            using (ClientProcess game = new ClientProcess(rt, "127.0.0.1", fake.Port))
            {
                FakeServer.Connection login = fake.WaitForLogin(0, startup);
                Assert.NotNull(login);
                Assert.True(fake.Probes >= 1, "ConnectWhenServerReady probes the status before logging in");
                Assert.False(string.IsNullOrEmpty(login.Name));
                Assert.True(login.TicketLength >= 0);

                Assert.NotNull(game.WaitFor(@"Disconnected: Wrong protocol version", TimeSpan.FromSeconds(20)));
                // the client never reports its status on that connection (its writer is closed by Disconnect)
                Assert.False(fake.WaitForPacket(login, Protocol.C2S.Status, TimeSpan.FromSeconds(3)), "no packet 2 after a version mismatch");
                Assert.Empty(login.Status);
                Assert.True(fake.WaitForClose(login, TimeSpan.FromSeconds(10)), "the client closes its socket");
                // back in the menu with the error dialog
                Assert.True(Poll.Until(() => game.Console.Eval("Game.View.GetType().Name").Trim() == "MainMenuView", TimeSpan.FromSeconds(10)));
                Assert.True(Poll.Until(() => game.Console.EvalT<bool>(
                    "((System.Collections.IEnumerable)Game.View.GetScreen().Desktop.Children).Cast<object>().Any(c => c.GetType().Name == \"ErrorDialog\")"),
                    TimeSpan.FromSeconds(10)), "an ErrorDialog is shown for the disconnect reason");
                Assert.True(game.IsRunning);
            }
        }

        // PROTO-24 (a)
        [Fact]
        public void Client_disconnects_on_an_unknown_server_packet_id()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                byte[] md5 = MapFile.Md5(rt.MapPath("bases"));
                using (FakeServer fake = new FakeServer(delegate(FakeServer.Connection c)
                {
                    c.Writer.Write(Protocol.S2C.ServerId);
                    c.Writer.Write((byte)3);
                    c.Writer.Write("bases");
                    c.Writer.Flush();
                }))
                using (ClientProcess game = new ClientProcess(rt, "127.0.0.1", fake.Port))
                {
                    FakeServer.Connection login = fake.WaitForLogin(0, startup);
                    Assert.NotNull(login);
                    // the client has the map: it answers with Status(true, md5)
                    Assert.True(fake.WaitForPacket(login, Protocol.C2S.Status, TimeSpan.FromSeconds(30)), "the client reports its map status");
                    Assert.Single(login.Status);
                    Assert.True(login.Status[0].Key, "ready: the scratch runtime ships bases.map");
                    Assert.Equal(md5, login.Status[0].Value);      // LevelLoader.LevelMd5 = MD5 of the whole file
                    // then feed it an id no server ever sends
                    login.Writer.Write((byte)99);
                    login.Writer.Flush();
                    Assert.NotNull(game.WaitFor(@"Disconnected: Invalid command: 99", TimeSpan.FromSeconds(20)));
                    Assert.True(fake.WaitForClose(login, TimeSpan.FromSeconds(10)), "the client closes its socket");
                    Assert.True(Poll.Until(() => game.Console.Eval("Game.View.GetType().Name").Trim() == "MainMenuView", TimeSpan.FromSeconds(10)));
                    Assert.True(game.IsRunning);
                }
            }
        }
    }
}
