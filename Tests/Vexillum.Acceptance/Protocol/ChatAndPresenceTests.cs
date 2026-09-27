using System;
using System.Collections.Generic;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// Chat formatting and commands, join/leave announcements, the spectator
    /// guards and the weapon-select echo, with two scripted clients on one
    /// server (bases, default bots).
    /// </summary>
    public class ChatAndPresenceTests : IClassFixture<DuoBasesFixture>
    {
        private readonly DuoBasesFixture fx;

        public ChatAndPresenceTests(DuoBasesFixture fx)
        {
            this.fx = fx;
        }

        // PROTO-19
        [Fact]
        public void Chat_is_broadcast_with_the_coloured_display_name_and_commands_answer_only_the_sender()
        {
            string alice = Names.Unique("alice"), bob = Names.Unique("bob");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                b.JoinGame(bob);
                int ma = a.PacketCount, mb = b.PacketCount;
                string expected = Colour.ForClass(a.Me.Class) + alice + Colour.White + "> hello";

                a.SendChat("hello");
                ChatPacket toA = a.WaitFor<ChatPacket>(p => p.Text.EndsWith("> hello"), TimeSpan.FromSeconds(5), ma);
                ChatPacket toB = b.WaitFor<ChatPacket>(p => p.Text.EndsWith("> hello"), TimeSpan.FromSeconds(5), mb);
                Assert.Equal(expected, toA.Text);
                Assert.Equal(expected, toB.Text);
                Assert.NotNull(fx.Server.WaitFor(alice + ": hello", 5));

                ma = a.PacketCount; mb = b.PacketCount;
                a.SendChat("/foo");
                ChatPacket unknown = a.WaitFor<ChatPacket>(p => p.Text.Contains("Unknown command"), TimeSpan.FromSeconds(5), ma);
                Assert.Equal(Colour.Orange + "Unknown command: /foo", unknown.Text);
                Assert.True(b.NoneWithin<ChatPacket>(p => p.Text.Contains("Unknown command") || p.Text.Contains("/foo"), TimeSpan.FromSeconds(1), mb));
                Assert.NotNull(fx.Server.WaitFor(alice + ": /foo", 5));

                ma = a.PacketCount; mb = b.PacketCount;
                string mapBefore = fx.Server.Console.ServerMap();
                a.SendChat("/newgame");
                ChatPacket denied = a.WaitFor<ChatPacket>(p => p.Text.Contains("need to be op"), TimeSpan.FromSeconds(5), ma);
                Assert.Equal(Colour.Orange + "You need to be op to do that!", denied.Text);
                Assert.True(b.NoneWithin<ChatPacket>(p => p.Text.Contains("need to be op"), TimeSpan.FromSeconds(1), mb));
                Assert.Empty(a.Packets<LevelChangingPacket>());
                Assert.Equal(mapBefore, fx.Server.Console.ServerMap());
                Assert.False(a.IsClosed);
            }
        }

        // PROTO-20
        [Fact]
        public void Join_and_leave_are_announced_to_the_other_clients()
        {
            string alice = Names.Unique("alice"), bob = Names.Unique("bob");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                int ma = a.PacketCount;
                short idB;
                PlayerClass classB;
                using (ScriptedClient b = new ScriptedClient(fx.Server))
                {
                    b.JoinGame(bob);
                    idB = b.MyEntityId;
                    classB = b.Me.Class;

                    MessagePacket joinMsg = a.WaitFor<MessagePacket>(m => m.MessageId == 1 && m.Args.Length == 1 && m.Args[0].EndsWith(bob), TimeSpan.FromSeconds(10), ma);
                    Assert.Equal(Colour.ForClass(classB) + bob, joinMsg.Args[0]);
                    Assert.Equal(2000, joinMsg.DurationMs);
                    PlayerSpawnPacket spawn = a.WaitFor<PlayerSpawnPacket>(p => p.Name == bob, TimeSpan.FromSeconds(10), ma);
                    Assert.Equal(idB, spawn.EntityId);
                    Assert.Equal(b.SteamId, spawn.SteamId);
                    Assert.Equal(classB, spawn.Class);
                    ChatPacket joinedChat = a.WaitFor<ChatPacket>(p => p.Text == Colour.Orange + bob + " joined the game", TimeSpan.FromSeconds(10), ma);
                    Assert.True(joinMsg.Sequence < joinedChat.Sequence, "61 is written before the chat line");
                    // b never sees its own PLAYER_JOIN: ready is set only after SendFinish
                    Assert.DoesNotContain(b.Packets<MessagePacket>(), m => m.MessageId == 1 && m.Args.Length == 1 && m.Args[0].EndsWith(bob));
                    ma = a.PacketCount;
                }
                // b's socket is closed: a is told to remove the entity and gets the chat line
                EntityRemovePacket removed = a.WaitFor<EntityRemovePacket>(r => r.EntityId == idB, TimeSpan.FromSeconds(10), ma);
                Assert.True(removed.Frame > 0);
                a.WaitFor<ChatPacket>(p => p.Text == Colour.Orange + bob + " left the game", TimeSpan.FromSeconds(10), ma);
                Assert.NotNull(fx.Server.WaitFor(bob + " disconnected", 5));
                Assert.Null(a.PlayerNamed(bob));
                Assert.Empty(a.Packets<DisconnectPacket>());
            }
        }

        // PROTO-14
        [Fact]
        public void Weapon_select_is_echoed_to_others_with_the_raw_index_while_the_server_clamps()
        {
            string alice = Names.Unique("alice"), bob = Names.Unique("bob");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                b.JoinGame(bob);
                ServerSide side = new ServerSide(fx.Server);
                short idA = a.MyEntityId;
                Assert.Equal(0, side.WeaponIndex(alice));

                int ma = a.PacketCount, mb = b.PacketCount;
                a.SendWeaponSelect(1);
                WeaponSelectPacket s1 = b.WaitFor<WeaponSelectPacket>(p => p.EntityId == idA, TimeSpan.FromSeconds(5), mb);
                Assert.Equal(1, s1.Index);
                Assert.True(Poll.Until(() => side.WeaponIndex(alice) == 1, TimeSpan.FromSeconds(5)));
                Assert.Equal("SMG", side.WeaponTypeName(alice));

                mb = b.PacketCount;
                a.SendWeaponSelect(7);
                WeaponSelectPacket s2 = b.WaitFor<WeaponSelectPacket>(p => p.EntityId == idA, TimeSpan.FromSeconds(5), mb);
                Assert.Equal(7, s2.Index);                              // the raw index goes out unclamped
                Assert.True(Poll.Until(() => side.WeaponIndex(alice) == 2, TimeSpan.FromSeconds(5)));   // clamped to inventory.Length - 1
                Assert.Equal("Sword", side.WeaponTypeName(alice));
                Assert.True(a.NoneWithin<WeaponSelectPacket>(p => p.EntityId == idA, TimeSpan.FromSeconds(1), ma));
                Assert.Equal(7, b.PlayerNamed(alice).WeaponIndex);   // the other client is left with the raw 7 while the server keeps 2
            }
        }

        // PROTO-17
        [Fact]
        public void Spectator_cannot_fire_act_or_hitscan_but_still_chats_and_moves()
        {
            string alice = Names.Unique("alice"), bob = Names.Unique("bob");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                b.JoinGame(bob);
                ServerSide side = new ServerSide(fx.Server);
                short idA = a.MyEntityId;
                int clipBefore = side.ClipAmmo(alice);
                Vec2 rest = side.WaitForRest(alice, TimeSpan.FromSeconds(10));

                int ma = a.PacketCount, mb = b.PacketCount;
                a.SendChat("/spec");
                ClassChangePacket cc = a.WaitFor<ClassChangePacket>(p => p.EntityId == -1, TimeSpan.FromSeconds(5), ma);
                Assert.Equal(PlayerClass.Spectator, cc.Class);
                Assert.Empty(cc.Weapons);
                ClassChangePacket ccB = b.WaitFor<ClassChangePacket>(p => p.EntityId == idA, TimeSpan.FromSeconds(5), mb);
                Assert.Equal(PlayerClass.Spectator, ccB.Class);
                Assert.Equal("Spectator", side.CurrentClass(alice));

                ma = a.PacketCount; mb = b.PacketCount;
                a.SendWeaponActivate(0, true, 0f);
                a.SendWeaponAction(KeyAction.Reload);
                Vec2 pivot = side.WeaponPivot(alice);
                a.SendHitscan(0f, (int)pivot.X, (int)pivot.Y);
                a.SendChat("hi");
                ChatPacket hi = a.WaitFor<ChatPacket>(p => p.Text.EndsWith("> hi"), TimeSpan.FromSeconds(5), ma);
                Assert.Equal(Colour.Gray + alice + Colour.White + "> hi", hi.Text);
                b.WaitFor<ChatPacket>(p => p.Text == hi.Text, TimeSpan.FromSeconds(5), mb);
                // nothing was fired, reloaded or traced
                Assert.True(a.NoneWithin<AmmoPacket>(null, TimeSpan.FromMilliseconds(700), ma));
                Assert.Empty(b.Packets<WeaponFirePacket>(mb).FindAll(p => p.EntityId == idA));
                Assert.Empty(b.Packets<ProjectileCreatePacket>(mb).FindAll(p => p.OwnerId == idA));
                Assert.Empty(b.Packets<HitscanPacket>(mb).FindAll(p => p.EntityId == idA));
                Assert.Empty(a.Packets<ExplodePacket>(ma));
                Assert.Equal(clipBefore, side.ClipAmmo(alice));

                // position packets have no class guard
                int tx = (int)rest.X + 40, ty = (int)rest.Y + 30;
                a.SendPositionAbsolute(tx, ty);
                System.Threading.Thread.Sleep(100);   // a duration: the repeat zeroes the velocity the first packet derived
                a.SendPositionAbsolute(tx, ty);
                Assert.True(Poll.Until(delegate()
                {
                    Vec2 p = side.Position(alice);
                    return Math.Abs(p.X - tx) <= 3 && Math.Abs(p.Y - ty) <= 8;
                }, TimeSpan.FromSeconds(3)), "spectator position not applied; now at " + side.Position(alice));
            }
        }
    }
}
