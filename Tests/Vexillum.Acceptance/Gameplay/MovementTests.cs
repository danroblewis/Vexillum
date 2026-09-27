using System;
using System.Collections.Generic;
using System.Threading;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    public class MovementFixture : GameplayFixture
    {
    }

    /// <summary>How the server relays a client's movement data (packet 15) to the other players (packet 30).</summary>
    public class MovementTests : IClassFixture<MovementFixture>
    {
        private readonly MovementFixture fx;

        public MovementTests(MovementFixture fx)
        {
            this.fx = fx;
        }

        /// <summary>The next position relay (30) the observer gets for the mover's entity after <paramref name="mark"/>.</summary>
        private static EntityMotion RelayFor(ScriptedClient observer, short id, int mark, string what)
        {
            PositionsPacket p = observer.WaitFor<PositionsPacket>(x => x.For(id) != null, TimeSpan.FromSeconds(5), mark);
            Assert.True(p != null, "no position relay for " + what);
            return p.For(id);
        }

        /// <summary>Waits until the observer has had no position relay for the entity for a whole second.</summary>
        private static void WaitForNoRelay(ScriptedClient observer, short id)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                int mark = observer.PacketCount;
                if (observer.NoneWithin<PositionsPacket>(x => x.For(id) != null, TimeSpan.FromSeconds(1), mark))
                    return;
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("entity #" + id + " keeps being relayed while it should stand still");
            }
        }

        // SRV-27 (known original bug, preserved): ServerPlayer.SetMovement compares movement[2] against
        // entity.direction and movement[3] against entity.jumping (indices shifted by one) before assigning
        // direction = movement[1] and jumping = movement[2], so an unchanged (moving=false, direction=right)
        // packet 15 looks like a change every time and movementChanged makes the server relay the entity
        // again although neither its position nor its movement data changed.
        [Fact]
        public void Repeating_identical_movement_data_is_relayed_again_because_of_the_shifted_index_compare()
        {
            using (ScriptedClient mover = fx.Join(GameplayFixture.Unique("mover")))
            using (ScriptedClient observer = fx.Join(GameplayFixture.Unique("watcher")))
            {
                // movement data under test: not moving, facing right, not jumping (MovementData 0b010)
                const bool moving = false, direction = true, jumping = false;
                byte expected = Protocol.EncodeMovement(moving, direction, jumping);
                short id = mover.MyEntityId;
                Walk.Park(fx, mover, 2800, 628);
                // bring the server-side state to exactly (moving, direction, jumping) whatever it was before:
                // a "moving" packet always registers (movement[0] is compared correctly), the next one stops
                int prime = observer.PacketCount;
                mover.SendPositionUnchanged(0f, true, direction, jumping);
                mover.SendPositionUnchanged(0f, moving, direction, jumping);
                Assert.NotNull(observer.WaitFor<PositionsPacket>(x => x.For(id) != null && x.For(id).MovementByte == expected, TimeSpan.FromSeconds(5), prime));
                // stand still on the ground (a moving entity would be relayed for its position anyway)
                Vec2 parked = Walk.Park(fx, mover, 2800, 628);
                Vec2 wire = new Vec2((int)parked.X, (int)parked.Y);
                Assert.True(fx.SyncT<bool>("!" + GameplayFixture.P(mover.Name) + ".Entity.moving && " + GameplayFixture.P(mover.Name) + ".Entity.direction && !" + GameplayFixture.P(mover.Name) + ".Entity.jumping"),
                    "the server-side movement state is the sent one");
                WaitForNoRelay(observer, id);

                // control: a standing entity whose packets carry no movement data (14) is not relayed at all
                int quiet = observer.PacketCount;
                mover.SendPositionUnchanged();
                mover.SendPositionUnchanged();
                Assert.True(observer.NoneWithin<PositionsPacket>(x => x.For(id) != null, TimeSpan.FromSeconds(1), quiet),
                    "a standing entity without movement data is not relayed");

                // the same data again: a correct compare (movement[1] vs direction, movement[2] vs jumping)
                // would find nothing changed; the original compares movement[2] (false) with direction (true)
                for (int repeat = 1; repeat <= 3; repeat++)
                {
                    int m = observer.PacketCount;
                    mover.SendPositionUnchanged(0f, moving, direction, jumping);
                    EntityMotion again = RelayFor(observer, id, m, "identical movement packet #" + repeat);
                    Assert.Equal(expected, again.MovementByte);
                    Assert.Equal(wire, again.Vector);
                    WaitForNoRelay(observer, id);
                }
                Assert.Equal(parked, fx.Position(mover.Name));

                // the same compare for not moving, facing left, not jumping (0b000): bit for bit equal to the
                // state once applied, so only the first packet is relayed and its repeat is not
                int m2 = observer.PacketCount;
                mover.SendPositionUnchanged(0f, false, false, false);
                Assert.Equal(0, RelayFor(observer, id, m2, "the facing-left packet").MovementByte);
                WaitForNoRelay(observer, id);
                int m3 = observer.PacketCount;
                mover.SendPositionUnchanged(0f, false, false, false);
                Assert.True(observer.NoneWithin<PositionsPacket>(x => x.For(id) != null, TimeSpan.FromSeconds(1), m3),
                    "an identical all-false movement packet is not relayed");

                fx.Leave(observer);
                fx.Leave(mover);
            }
        }
    }
}
