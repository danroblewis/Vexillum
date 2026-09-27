using System;
using System.Collections.Generic;
using System.IO;
using Vexillum.Entities;
using Vexillum.Game;
using Vexillum.util;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// A concrete <see cref="Level"/> for physics and terrain tests, built from
    /// a shipped map through the real <c>LevelLoader.LoadData</c> and the real
    /// <c>Level</c> constructor with <c>Util.IsServer = true</c> (no textures).
    /// Entity collisions are not resolved (<c>Collision</c> is a no-op, as the
    /// server subclass would need a Server); everything else (gravity,
    /// friction, terrain, explosions, hitscans, spawn regions) is the author's
    /// code. Load through <see cref="Load(ScratchRuntime, string)"/>; the loader
    /// reads <c>Maps/&lt;name&gt;.map</c> relative to the current directory and the
    /// entity id counter is static, so tests using this class belong to the
    /// <see cref="GameStateCollection"/> (see Collections.cs).
    /// </summary>
    public sealed class HeadlessLevel : Level
    {
        /// <summary>Guards the process-wide state the loader touches (cwd, Util.IsServer, Entity.CurrentID).</summary>
        public static readonly object GameStateLock = new object();

        private long time;
        private readonly List<Entity> humanoids = new List<Entity>();

        public int Width { get { return width; } }
        public int Height { get { return height; } }
        /// <summary>The regions parsed from data.txt (the constructor also filed the spawn_/flag_ ones).</summary>
        public List<Region> Regions { get; private set; }
        /// <summary>Spawn regions per class as the Level constructor filed them.</summary>
        public Dictionary<PlayerClass, List<Region>> Spawns { get { return SpawnPositions; } }
        /// <summary>Flag regions per class.</summary>
        public Dictionary<PlayerClass, Region> Flags { get { return flagPositions; } }
        /// <summary>The level's entity list (the same object the Level steps).</summary>
        public List<Entity> EntityList { get { return Entities; } }
        /// <summary>Simulated time in ms (16 per frame) handed to Step.</summary>
        public long TimeMs { get { return time; } }

        private HeadlessLevel(string shortName, string longName, System.Drawing.Bitmap main,
                              System.Drawing.Bitmap background, System.Drawing.Bitmap collision, List<Region> regions)
            : base(shortName, longName, main, background, collision, regions)
        {
            Regions = regions;
            foreach (Region r in regions)
            {
                if (r.name.StartsWith("spawn_"))
                {
                    PlayerClass cl = (PlayerClass)Enum.Parse(typeof(PlayerClass), r.name.Split('_')[1], true);
                    if (!SpawnPositions.ContainsKey(cl))
                        SpawnPositions[cl] = new List<Region>();
                    SpawnPositions[cl].Add(r);
                }
                else if (r.name.StartsWith("flag_"))
                {
                    PlayerClass cl = (PlayerClass)Enum.Parse(typeof(PlayerClass), r.name.Split('_')[1], true);
                    flagPositions[cl] = r;
                }
            }
        }

        protected override void Collision(Entity e1, Entity e2, int direction)
        {
        }

        /// <summary>
        /// Loads Maps/&lt;mapName&gt;.map from the runtime, resets the entity id
        /// counter (as the server does per map) and loads the server humanoid
        /// definitions. Call inside <c>lock (HeadlessLevel.GameStateLock)</c>
        /// or from a GameState-collection test.
        /// </summary>
        public static HeadlessLevel Load(ScratchRuntime runtime, string mapName)
        {
            return Load(runtime.Root, mapName);
        }

        public static HeadlessLevel Load(string runtimeRoot, string mapName)
        {
            string prev = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(runtimeRoot);
            try
            {
                Util.IsServer = true;
                HumanoidTypes.LoadContentServer();
                Entity.ResetID();
                LevelLoader.LevelData d = LevelLoader.LoadData(mapName);
                if (d == null)
                    throw new FileNotFoundException("LevelLoader.LoadData returned null for " + mapName + " in " + runtimeRoot);
                return new HeadlessLevel(d.shortName, d.longName, d.bitmaps["main"], d.bitmaps["background"], d.bitmaps["collision"], d.regions);
            }
            finally
            {
                Directory.SetCurrentDirectory(prev);
            }
        }

        /// <summary>The raw LevelData (bitmaps, regions, compressed bytes) without constructing a level.</summary>
        public static LevelLoader.LevelData LoadData(string runtimeRoot, string mapName)
        {
            string prev = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(runtimeRoot);
            try
            {
                Util.IsServer = true;
                return LevelLoader.LoadData(mapName);
            }
            finally
            {
                Directory.SetCurrentDirectory(prev);
            }
        }

        /// <summary>Creates a humanoid of that class (server definition: 8x40, speed 2) and adds it as a player entity at pos.</summary>
        public HumanoidEntity AddHumanoid(PlayerClass cl, Vec2 pos)
        {
            HumanoidEntity e = HumanoidTypes.CreateHumanoid(cl);
            e.Position = pos;
            AddEntity(e, true);
            humanoids.Add(e);
            return e;
        }

        /// <summary>Creates a humanoid at a random spawn position of its class (GetSpawnPosition).</summary>
        public HumanoidEntity AddHumanoidAtSpawn(PlayerClass cl)
        {
            HumanoidEntity e = HumanoidTypes.CreateHumanoid(cl);
            e.Position = GetSpawnPosition(cl, e.Size);
            AddEntity(e, true);
            humanoids.Add(e);
            return e;
        }

        /// <summary>Adds any entity (id allocated by the Level setter) and returns it.</summary>
        public T Add<T>(T e, Vec2 pos) where T : Entity
        {
            e.Position = pos;
            AddEntity(e);
            return e;
        }

        /// <summary>Runs n frames of Level.Step with 16 ms per frame.</summary>
        public void StepFrames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                time += VexillumConstants.TIME_PER_FRAME;
                Step(time);
            }
        }

        /// <summary>Solid bit of the terrain pixel (world coordinates, y up).</summary>
        public bool IsSolid(int x, int y)
        {
            return terrain.GetTerrain(x, y);
        }

        /// <summary>Collision nibble of the pixel (0 empty, 1 solid, 2..15 hardness).</summary>
        public byte CollisionNibble(int x, int y)
        {
            return terrain.GetCollisionData(x, y);
        }

        public bool IsLadder(int x, int y)
        {
            return terrain.GetLadder(x, y);
        }

        public bool IsTransparentWhenDestroyed(int x, int y)
        {
            return terrain.GetTransparent(x, y);
        }

        /// <summary>Entity id written into the outline bits at the pixel (0 = none), ignoring <paramref name="self"/>.</summary>
        public short EntityAt(int x, int y, Entity self)
        {
            return terrain.GetEntity(self, x, y, this);
        }

        /// <summary>The current terrain as a snapshot (ToBytes).</summary>
        public TerrainSnapshot Snapshot()
        {
            return new TerrainSnapshot(GetTerrainState(), width, height);
        }

        /// <summary>Highest solid pixel in column x at or below y (scanning down), or -1.</summary>
        public int GroundBelow(int x, int y)
        {
            for (int yy = y; yy >= 0; yy--)
                if (terrain.GetTerrain(x, yy))
                    return yy;
            return -1;
        }
    }
}
