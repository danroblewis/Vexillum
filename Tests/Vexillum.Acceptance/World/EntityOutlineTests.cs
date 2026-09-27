using System;
using System.Collections.Generic;
using Vexillum;
using Vexillum.Acceptance;
using Vexillum.Entities;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.physicsterrain
{
    /// <summary>
    /// Entity registration in the top 16 bits of the terrain array
    /// (TerrainArray.SetEntityState / GetEntity) and entity id allocation.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class EntityOutlineTests
    {
        private static List<int[]> PixelsWithId(SyntheticLevel level, short id)
        {
            List<int[]> r = new List<int[]>();
            for (int x = 0; x < level.Width; x++)
                for (int y = 0; y < level.Height; y++)
                    if (level.EntityAt(x, y, null) == id)
                        r.Add(new[] { x, y });
            return r;
        }

        // PHYS-08: an 8x40 humanoid at (32,32) writes an 88-pixel outline.
        [Fact]
        public void Humanoid_outline_is_88_pixels_and_GetEntity_excludes_self_and_unknown_ids()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(64, 64, SyntheticLevel.Empty);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 32));
            Assert.Equal(new Vec2(8, 40), e.Size);
            Assert.Equal(new Vec2(32, 32), e.positionInLevel);

            List<int[]> px = PixelsWithId(level, e.ID);
            Assert.Equal(88, px.Count);
            HashSet<string> set = new HashSet<string>();
            foreach (int[] p in px) set.Add(p[0] + "," + p[1]);
            for (int x = 29; x <= 34; x++)
            {
                Assert.Contains(x + ",52", set);   // posY + HalfSize.Y
                Assert.Contains(x + ",13", set);   // posY - HalfSize.Y + 1
            }
            for (int y = 14; y <= 51; y++)
            {
                Assert.Contains("28," + y, set);   // posX - HalfSize.X
                Assert.Contains("35," + y, set);   // posX + HalfSize.X - 1
            }
            Assert.Equal(0, level.EntityAt(32, 32, null));       // interior is not registered
            Assert.Equal(e.ID, level.EntityAt(28, 20, null));
            Assert.Equal(0, level.EntityAt(28, 20, e));          // own id excluded

            // An id that is no longer in EntityIndex is reported as 0 to other entities (but still to null).
            HumanoidEntity other = level.AddHumanoid(PlayerClass.Blue, new Vec2(60, 32));
            Assert.Equal(e.ID, level.EntityAt(28, 20, other));
            level.EntityIndex.Remove(e.ID);
            Assert.Equal(0, level.EntityAt(28, 20, other));
            Assert.Equal(e.ID, level.EntityAt(28, 20, null));
            level.EntityIndex.Add(e.ID, e);
            Assert.Equal(e.ID, level.EntityAt(28, 20, other));
        }

        // PHYS-09: the outline follows Position, RemoveEntity clears it, removed entities cannot re-register.
        [Fact]
        public void Outline_follows_Position_and_is_cleared_by_RemoveEntity()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(64, 64, SyntheticLevel.Empty);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 32));
            short id = e.ID;

            e.Position = new Vec2(40, 32);
            Assert.Equal(0, level.EntityAt(28, 20, null));
            Assert.Equal(id, level.EntityAt(36, 20, null));
            Assert.Equal(88, PixelsWithId(level, id).Count);
            Assert.Equal(new Vec2(40, 32), e.positionInLevel);

            level.RemoveEntity(e);
            Assert.True(e.removed);
            Assert.False(level.EntityIndex.ContainsKey(id));
            Assert.DoesNotContain(e, level.getEntities());
            Assert.Empty(PixelsWithId(level, id));

            level.Terrain.SetEntityState(e, new Vec2(40, 32), true);
            Assert.Empty(PixelsWithId(level, id));
        }

        // PHYS-08/09: SetEntityState clips the outline at the map border without throwing.
        [Fact]
        public void Outline_is_clipped_at_the_map_border()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(64, 64, SyntheticLevel.Empty);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(2, 10));
            List<int[]> px = PixelsWithId(level, e.ID);
            Assert.True(px.Count > 0 && px.Count < 88, "clipped outline has " + px.Count + " pixels");
            foreach (int[] p in px)
                Assert.True(p[0] >= 0 && p[1] >= 0, "in bounds");
            // right column x = 2 + 4 - 1 = 5 is inside, left column x = -2 is dropped
            Assert.Equal(e.ID, level.EntityAt(5, 10, null));
        }

        // Scope: entity ids are allocated by the Level setter from the static counter; ResetID restarts at 2.
        [Fact]
        public void Entity_ids_are_allocated_sequentially_from_the_reset_counter()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(64, 64, SyntheticLevel.Empty);   // Prepare() resets the counter
            HumanoidEntity a = level.AddHumanoid(PlayerClass.Green, new Vec2(10, 30));
            BasicEntity b = new BasicEntity();
            b.Size = new Vec2(4, 4);
            Assert.Equal(-1, b.ID);
            level.Add(b, new Vec2(30, 30));
            HumanoidEntity c = level.AddHumanoid(PlayerClass.Blue, new Vec2(50, 30));
            Assert.Equal(2, a.ID);
            Assert.Equal(3, b.ID);
            Assert.Equal(4, c.ID);
            Assert.Same(b, level.GetEntityByID(3));
            Assert.Null(level.GetEntityByID(99));

            // An entity that already has an id keeps it (the server assigns ids before AddEntity).
            BasicEntity d = new BasicEntity();
            d.Size = new Vec2(4, 4);
            d.ID = 200;
            level.Add(d, new Vec2(40, 40));
            Assert.Equal(200, d.ID);
            Assert.Same(d, level.GetEntityByID(200));
            Assert.Equal(200, level.EntityAt(38, 40, null));

            Entity.ResetID();
            Assert.Equal(1, Entity.CurrentID);
            SyntheticLevel level2 = SyntheticLevel.Uniform(16, 16, SyntheticLevel.Empty);
            BasicEntity f = new BasicEntity();
            f.Size = new Vec2(2, 2);
            level2.Add(f, new Vec2(8, 8));
            Assert.Equal(2, f.ID);
        }

        // Scope: after the short counter wraps past -2, NextID skips -1 and ids still in use.
        [Fact]
        public void NextID_skips_minus_one_and_ids_in_use_after_the_counter_wraps()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(16, 16, SyntheticLevel.Empty);
            BasicEntity a = new BasicEntity();
            a.Size = new Vec2(2, 2);
            level.Add(a, new Vec2(4, 4));               // id 2
            BasicEntity b = new BasicEntity();
            b.Size = new Vec2(2, 2);
            level.Add(b, new Vec2(8, 8));               // id 3
            try
            {
                Entity.CurrentID = -4;
                Assert.Equal(-3, Entity.NextID(level));
                Assert.Equal(-2, Entity.NextID(level));   // enables the in-use check
                List<short> next = new List<short>();
                for (int i = 0; i < 6; i++)
                    next.Add(Entity.NextID(level));
                Assert.DoesNotContain((short)-1, next);
                Assert.DoesNotContain((short)2, next);
                Assert.DoesNotContain((short)3, next);
                Assert.Contains((short)4, next);
                Assert.Contains((short)5, next);
            }
            finally
            {
                Entity.ResetID();
            }
        }

        [Fact(Skip = "Known original bug: Entity.NextID hands out id 0 after the counter wraps past -2, and 0 is the 'no entity' value of the terrain outline, docs/PORTING.md")]
        public void NextID_never_returns_zero()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(16, 16, SyntheticLevel.Empty);
            try
            {
                Entity.CurrentID = -3;
                for (int i = 0; i < 4; i++)
                    Assert.NotEqual(0, Entity.NextID(level));
            }
            finally
            {
                Entity.ResetID();
            }
        }
    }
}
