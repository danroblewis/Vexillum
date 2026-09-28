using System;
using System.Collections.Generic;
using System.IO;
using Vexillum.Entities;
using Vexillum.Game;
using Vexillum.util;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// A concrete <see cref="Level"/> built from synthetic bitmaps (or from a
    /// shipped map) for terrain and physics tests that need a hand-made world:
    /// a floor, a wall, a ladder column, a uniform destructible block. The
    /// collision bitmap is described in WORLD coordinates (y up) by a colour
    /// function; the class writes it into a <c>System.Drawing.Bitmap</c> with
    /// the Y flip the real <c>Level</c> constructor expects, so the author's
    /// colour switch (Level.cs 90-123) is what classifies every pixel.
    ///
    /// Unlike <see cref="HeadlessLevel"/> the entity collision callback, the
    /// hitscan callback, the death callback and the task queue are observable:
    /// <see cref="OnCollision"/> is invoked by <c>Collision</c> (set it to call
    /// <c>OnCollide</c> to behave like ServerLevel), <see cref="Collisions"/>,
    /// <see cref="HitscanHits"/> and <see cref="Deaths"/> record what the
    /// author's code reported, and <see cref="Tasks"/> is processed after every
    /// frame of <see cref="StepFrames"/> (the grappling hook's conditional task
    /// lives there). Requires <c>Util.IsServer = true</c> (no textures) and,
    /// because of the static entity id counter, the GameState collection.
    /// </summary>
    public sealed class SyntheticLevel : Level
    {
        /// <summary>Magenta: TerrainCollisionType.Empty, no terrain.</summary>
        public const uint Empty = 0xFFFF00FF;
        /// <summary>White: solid, indestructible (nibble 1).</summary>
        public const uint Solid = 0xFFFFFFFF;
        /// <summary>Yellow: ladder.</summary>
        public const uint Ladder = 0xFFFFFF00;
        /// <summary>Blue: empty and the main pixel is cleared.</summary>
        public const uint Clear = 0xFF0000FF;
        /// <summary>The opaque colour every synthetic main bitmap is filled with.</summary>
        public const uint MainFill = 0xFF123456;

        /// <summary>Destructible terrain with the given collision nibble (2..15): red = nibble*16, green 0, blue 0.</summary>
        public static uint Destructible(int nibble)
        {
            if (nibble < 2 || nibble > 15)
                throw new ArgumentOutOfRangeException("nibble");
            return 0xFF000000u | ((uint)(nibble * 16) << 16);
        }

        /// <summary>Delegate for <see cref="OnCollision"/>: (e1, e2 or null for terrain, direction 0 = x, 1 = y).</summary>
        public delegate void CollisionHandler(Entity e1, Entity e2, int direction);

        public sealed class CollisionRecord
        {
            public Entity E1;
            public Entity E2;
            public int Direction;
            public int Frame;
            public override string ToString() { return "f" + Frame + " " + E1 + " vs " + (E2 == null ? "terrain" : E2.ToString()) + " dir=" + Direction; }
        }

        public sealed class HitscanRecord
        {
            public Vec2 Pos;
            public Vec2 Start;
            public Vec2 Unit;
        }

        /// <summary>Called from the author's Collision hook; null (default) records only.</summary>
        public CollisionHandler OnCollision;
        /// <summary>Every Collision(e1, e2, direction) call the physics made, in order.</summary>
        public readonly List<CollisionRecord> Collisions = new List<CollisionRecord>();
        /// <summary>Every OnHitscanHit(pos, start, unit) call.</summary>
        public readonly List<HitscanRecord> HitscanHits = new List<HitscanRecord>();
        /// <summary>Entities passed to OnEntityDeath, in order (repeats allowed).</summary>
        public readonly List<Entity> Deaths = new List<Entity>();
        /// <summary>The queue returned by GetTaskQueue; processed after every stepped frame.</summary>
        public readonly TaskQueue Tasks = new TaskQueue();

        private long time;

        public int Width { get { return width; } }
        public int Height { get { return height; } }
        public TerrainArray Terrain { get { return terrain; } }
        public List<Entity> EntityList { get { return Entities; } }
        public Dictionary<PlayerClass, List<Region>> Spawns { get { return SpawnPositions; } }
        public long TimeMs { get { return time; } }

        public SyntheticLevel(string shortName, string longName, System.Drawing.Bitmap main,
                              System.Drawing.Bitmap background, System.Drawing.Bitmap collision, List<Region> regions)
            : base(shortName, longName, main, background, collision, regions)
        {
        }

        /// <summary>A w x h bitmap filled with one ARGB colour.</summary>
        public static System.Drawing.Bitmap Filled(int w, int h, uint argb)
        {
            System.Drawing.Bitmap b = new System.Drawing.Bitmap(w, h);
            System.Drawing.Color c = System.Drawing.Color.FromArgb(unchecked((int)argb));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    b.SetPixel(x, y, c);
            return b;
        }

        /// <summary>
        /// Builds the collision bitmap from a colour function in world
        /// coordinates (x right, y up; world y = 0 is the bottom bitmap row).
        /// </summary>
        public static System.Drawing.Bitmap CollisionBitmap(int w, int h, Func<int, int, uint> argbAtWorld)
        {
            System.Drawing.Bitmap b = new System.Drawing.Bitmap(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    b.SetPixel(x, h - 1 - y, System.Drawing.Color.FromArgb(unchecked((int)argbAtWorld(x, y))));
            return b;
        }

        /// <summary>
        /// A w x h level whose collision pixels come from <paramref name="argbAtWorld"/>
        /// (world coordinates); main is filled with <see cref="MainFill"/>.
        /// Sets Util.IsServer, loads the server humanoid definitions and resets
        /// the entity id counter first.
        /// </summary>
        public static SyntheticLevel Build(int w, int h, Func<int, int, uint> argbAtWorld)
        {
            Prepare();
            return new SyntheticLevel("synthetic", "Synthetic", Filled(w, h, MainFill), Filled(w, h, MainFill),
                                      CollisionBitmap(w, h, argbAtWorld), new List<Region>());
        }

        /// <summary>A w x h level filled with one collision colour.</summary>
        public static SyntheticLevel Uniform(int w, int h, uint argb)
        {
            return Build(w, h, delegate(int x, int y) { return argb; });
        }

        /// <summary>
        /// A level from a shipped map (Maps/&lt;map&gt;.map under runtimeRoot) with
        /// the raw region list (the base Level never files spawn regions; use
        /// <see cref="HeadlessLevel"/> when the spawn tables are needed).
        /// </summary>
        public static SyntheticLevel FromShippedMap(string runtimeRoot, string mapName, bool withRegions)
        {
            LevelLoader.LevelData d = HeadlessLevel.LoadData(runtimeRoot, mapName);
            if (d == null)
                throw new FileNotFoundException("LevelLoader.LoadData returned null for " + mapName);
            Prepare();
            return new SyntheticLevel(d.shortName, d.longName, d.bitmaps["main"], d.bitmaps["background"], d.bitmaps["collision"],
                                      withRegions ? d.regions : new List<Region>());
        }

        /// <summary>Util.IsServer = true, HumanoidTypes.LoadContentServer(), Entity.ResetID().</summary>
        public static void Prepare()
        {
            Util.IsServer = true;
            HumanoidTypes.LoadContentServer();
            Entity.ResetID();
        }

        /// <summary>
        /// Creates an entity of an internal type of the Game assembly by full
        /// name (e.g. "Vexillum.Entities.Rocket") through its public
        /// parameterless constructor, as StreamHelper does with Activator.
        /// </summary>
        public static T CreateEntity<T>(string fullName) where T : Entity
        {
            Type t = typeof(Entity).Assembly.GetType(fullName, true);
            return (T)Activator.CreateInstance(t);
        }

        protected override void Collision(Entity e1, Entity e2, int direction)
        {
            Collisions.Add(new CollisionRecord { E1 = e1, E2 = e2, Direction = direction, Frame = frame });
            if (OnCollision != null)
                OnCollision(e1, e2, direction);
        }

        protected override void OnHitscanHit(Vec2 pos, Vec2 startPos, Vec2 unitVec)
        {
            HitscanHits.Add(new HitscanRecord { Pos = pos, Start = startPos, Unit = unitVec });
        }

        public override void OnEntityDeath(Entity e)
        {
            Deaths.Add(e);
        }

        public override TaskQueue GetTaskQueue()
        {
            return Tasks;
        }

        /// <summary>Server-style collision: e1.OnCollide(e2, dir) and e2.OnCollide(e1, dir).</summary>
        public void UseServerCollision()
        {
            OnCollision = delegate(Entity e1, Entity e2, int direction)
            {
                e1.OnCollide(e2, direction);
                if (e2 != null)
                    e2.OnCollide(e1, direction);
            };
        }

        /// <summary>A humanoid of that class (8x40, speed 2) added as a player entity at pos.</summary>
        public HumanoidEntity AddHumanoid(PlayerClass cl, Vec2 pos)
        {
            HumanoidEntity e = HumanoidTypes.CreateHumanoid(cl);
            e.Position = pos;
            AddEntity(e, true);
            return e;
        }

        /// <summary>Adds any entity (not a player) at pos.</summary>
        public T Add<T>(T e, Vec2 pos) where T : Entity
        {
            e.Position = pos;
            AddEntity(e);
            return e;
        }

        /// <summary>Runs n frames of Level.Step (16 ms each) and processes the task queue after each.</summary>
        public void StepFrames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                time += VexillumConstants.TIME_PER_FRAME;
                Step(time);
                Tasks.Process((int)time);
            }
        }

        public bool IsSolid(int x, int y)
        {
            return terrain.GetTerrain(x, y);
        }

        public byte CollisionNibble(int x, int y)
        {
            return terrain.GetCollisionData(x, y);
        }

        public bool IsLadder(int x, int y)
        {
            return terrain.GetLadder(x, y);
        }

        public short EntityAt(int x, int y, Entity self)
        {
            return terrain.GetEntity(self, x, y, this);
        }

        public TerrainSnapshot Snapshot()
        {
            return new TerrainSnapshot(GetTerrainState(), width, height);
        }

        /// <summary>Number of pixels with the solid bit set.</summary>
        public int SolidCount()
        {
            int n = 0;
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    if (terrain.GetTerrain(x, y))
                        n++;
            return n;
        }
    }
}
