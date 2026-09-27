using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Vexillum.util;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// Shared support for the protocol acceptance tests (Tests/Vexillum.Acceptance/Protocol):
    /// server fixtures with the configurations the tests need, unique player
    /// names, polling helpers, the chat colour prefixes as the wire carries
    /// them, and terrain helpers to find a spot where a humanoid stands still.
    /// Nothing here touches Test/ or a historical file.
    /// </summary>
    internal static class Names
    {
        private static int counter;

        /// <summary>A player name unique in this process ("alice-3-9f1c"); the server refuses duplicates.</summary>
        public static string Unique(string prefix)
        {
            return prefix + "-" + Interlocked.Increment(ref counter) + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }
    }

    /// <summary>The chat colour prefixes (TextUtil.colorChar '§' + digit) as literals, so a test never trusts the code it checks.</summary>
    internal static class Colour
    {
        public const string White = "§1";
        public const string Gray = "§4";
        public const string Blue = "§6";
        public const string Green = "§8";
        public const string Orange = "§9";

        /// <summary>The display-name prefix SurvivalGameModeShared.GetDisplayName gives a class.</summary>
        public static string ForClass(global::Vexillum.Game.PlayerClass c)
        {
            switch (c)
            {
                case global::Vexillum.Game.PlayerClass.Green: return Green;
                case global::Vexillum.Game.PlayerClass.Blue: return Blue;
                default: return Gray;
            }
        }
    }

    internal static class Poll
    {
        /// <summary>Polls a condition until it holds or the timeout passes; the last evaluation is the result.</summary>
        public static bool Until(Func<bool> condition, TimeSpan timeout)
        {
            return Until(condition, timeout, 25);
        }

        public static bool Until(Func<bool> condition, TimeSpan timeout, int intervalMs)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;
                Thread.Sleep(intervalMs);
            }
            return condition();
        }
    }

    /// <summary>Reads server-side state of one player through the debug console (every read runs on the Server Main thread).</summary>
    internal sealed class ServerSide
    {
        private readonly ServerProcess server;

        public ServerSide(ServerProcess server)
        {
            this.server = server;
        }

        private static string PlayerExpr(string name)
        {
            return "((System.Collections.IEnumerable)Server.players).Cast<Player>().First(p => p.name == \"" + name + "\")";
        }

        private static string Inv(string floatExpr)
        {
            return "(" + floatExpr + ").ToString(System.Globalization.CultureInfo.InvariantCulture)";
        }

        private string EvalPlayer(string name, string body)
        {
            return server.Console.Eval("Sync(() => { var pl = " + PlayerExpr(name) + "; return " + body + "; })").Trim();
        }

        private static Vec2 ParseVec(string s)
        {
            string[] xy = s.Split(',');
            return new Vec2(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture));
        }

        /// <summary>Player.Entity.Position.</summary>
        public Vec2 Position(string name)
        {
            return ParseVec(EvalPlayer(name, Inv("pl.Entity.Position.X") + " + \",\" + " + Inv("pl.Entity.Position.Y")));
        }

        /// <summary>Player.Entity.Velocity.</summary>
        public Vec2 Velocity(string name)
        {
            return ParseVec(EvalPlayer(name, Inv("pl.Entity.Velocity.X") + " + \",\" + " + Inv("pl.Entity.Velocity.Y")));
        }

        /// <summary>Player.Entity.Weapon.GetPivot() (the hitscan origin the real client sends).</summary>
        public Vec2 WeaponPivot(string name)
        {
            return ParseVec(EvalPlayer(name, Inv("pl.Entity.Weapon.GetPivot().X") + " + \",\" + " + Inv("pl.Entity.Weapon.GetPivot().Y")));
        }

        /// <summary>Player.Entity.Size (the collision box).</summary>
        public Vec2 EntitySize(string name)
        {
            return ParseVec(EvalPlayer(name, Inv("pl.Entity.Size.X") + " + \",\" + " + Inv("pl.Entity.Size.Y")));
        }

        public int WeaponIndex(string name)
        {
            return int.Parse(EvalPlayer(name, "pl.WeaponIndex.ToString()"), CultureInfo.InvariantCulture);
        }

        public string WeaponTypeName(string name)
        {
            return EvalPlayer(name, "pl.Entity.Weapon.GetType().Name");
        }

        /// <summary>clipAmmo of the current (reloadable) weapon.</summary>
        public int ClipAmmo(string name)
        {
            return int.Parse(EvalPlayer(name, "((ReloadableWeapon)pl.Entity.Weapon).clipAmmo.ToString()"), CultureInfo.InvariantCulture);
        }

        public int TotalAmmo(string name)
        {
            return int.Parse(EvalPlayer(name, "((ReloadableWeapon)pl.Entity.Weapon).totalAmmo.ToString()"), CultureInfo.InvariantCulture);
        }

        public bool HasHook(string name)
        {
            return EvalPlayer(name, "(pl.Entity.hook != null).ToString()") == "True";
        }

        /// <summary>moving, direction, jumping of the humanoid.</summary>
        public bool[] MovementFlags(string name)
        {
            string s = EvalPlayer(name, "pl.Entity.moving + \",\" + pl.Entity.direction + \",\" + pl.Entity.jumping");
            string[] f = s.Split(',');
            return new bool[] { bool.Parse(f[0]), bool.Parse(f[1]), bool.Parse(f[2]) };
        }

        public float ArmAngle(string name)
        {
            return float.Parse(EvalPlayer(name, Inv("pl.Entity.ArmAngle")), CultureInfo.InvariantCulture);
        }

        public string CurrentClass(string name)
        {
            return EvalPlayer(name, "pl.CurrentClass.ToString()");
        }

        public float Health(string name)
        {
            return float.Parse(EvalPlayer(name, Inv("pl.Entity.Health")), CultureInfo.InvariantCulture);
        }

        /// <summary>Server.players.Count (humans, bots and connections that have not logged in yet).</summary>
        public int PlayerCount()
        {
            return server.Console.EvalT<int>("Sync(() => Server.players.Count)");
        }

        /// <summary>Names of the AIPlayers in Server.players.</summary>
        public List<string> BotNames()
        {
            List<string> r = new List<string>();
            foreach (DebugConsole.PlayerInfo p in server.Console.ServerPlayers())
                if (p.IsBot)
                    r.Add(p.Name);
            return r;
        }

        /// <summary>Server.level.terrain.GetTerrain(x, y).</summary>
        public bool IsSolid(int x, int y)
        {
            return server.Console.EvalT<bool>("Sync(() => Server.level.terrain.GetTerrain(" + x + ", " + y + "))");
        }

        /// <summary>
        /// Waits until the player's entity has come to rest (same position in
        /// two reads 150 ms apart) and returns that position. A freshly spawned
        /// humanoid falls from the spawn box onto the ground within a second.
        /// </summary>
        public Vec2 WaitForRest(string name, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            Vec2 last = Position(name);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(150);
                Vec2 now = Position(name);
                if (now.X == last.X && now.Y == last.Y)
                    return now;
                last = now;
            }
            throw new TimeoutException(name + " did not come to rest within " + timeout.TotalSeconds + " s; last position " + last);
        }
    }

    /// <summary>Terrain lookups on the snapshot a client received (world y up).</summary>
    internal static class Ground
    {
        /// <summary>Highest solid pixel at column x at or below y, or -1.</summary>
        public static int Below(TerrainSnapshot t, int x, int y)
        {
            for (int yy = y; yy >= 0; yy--)
                if (t.IsSolid(x, yy))
                    return yy;
            return -1;
        }

        /// <summary>True when the ground level is the same for every column in [x - halfWidth, x + halfWidth].</summary>
        public static bool IsFlat(TerrainSnapshot t, int x, int y, int halfWidth)
        {
            int g = Below(t, x, y);
            if (g < 0)
                return false;
            for (int xx = x - halfWidth; xx <= x + halfWidth; xx++)
                if (Below(t, xx, y) != g)
                    return false;
            return true;
        }

        /// <summary>The first x in [xFrom, xTo] whose ground is flat over +/- halfWidth and has clear air up to y, or -1.</summary>
        public static int FindFlat(TerrainSnapshot t, int xFrom, int xTo, int y, int halfWidth)
        {
            for (int x = xFrom; x <= xTo; x++)
                if (IsFlat(t, x, y, halfWidth) && Below(t, x, y) < y - 4)
                    return x;
            return -1;
        }

        /// <summary>
        /// The first x in [xFrom, xTo] at least <paramref name="minDistance"/> from <paramref name="avoidX"/>
        /// whose ground (from y downward) is exactly <paramref name="groundLevel"/> over +/- halfWidth, or -1.
        /// Keeps a spot on the same platform as a reference position.
        /// </summary>
        public static int FindFlatAt(TerrainSnapshot t, int xFrom, int xTo, int y, int halfWidth, int groundLevel, int avoidX, int minDistance)
        {
            for (int x = xFrom; x <= xTo; x++)
                if (Math.Abs(x - avoidX) >= minDistance && Below(t, x, y) == groundLevel && IsFlat(t, x, y, halfWidth))
                    return x;
            return -1;
        }

        /// <summary>
        /// Like <see cref="FindFlatAt"/> but returns the candidate farthest from the
        /// other entities the client knows about near that height (bots roam the
        /// spawn platforms and dropped flags lie on them), requiring at least
        /// <paramref name="clearance"/> px; -1 when none qualifies. <paramref name="span"/>
        /// extra pixels to the right must be on the same platform too (0 for a single spot).
        /// </summary>
        public static int FindClearSpot(TerrainSnapshot t, ScriptedClient c, short selfId, int xFrom, int xTo, int y, int halfWidth,
                                        int groundLevel, int avoidX, int minDistance, int span, int clearance)
        {
            List<float> others = new List<float>();
            lock (c.Entities)
            {
                foreach (ScriptedClient.EntityState e in c.Entities.Values)
                    if (e.Id != selfId && !e.Removed && Math.Abs(e.Position.Y - (groundLevel + 20)) < 80)
                        others.Add(e.Position.X);
            }
            int best = -1;
            float bestScore = -1;
            for (int x = xFrom; x <= xTo; x += 2)
            {
                if (Math.Abs(x - avoidX) < minDistance || Below(t, x, y) != groundLevel || !IsFlat(t, x, y, halfWidth))
                    continue;
                if (span > 0 && (Below(t, x + span, y) != groundLevel || !IsFlat(t, x + span, y, halfWidth)))
                    continue;
                float score = float.MaxValue;
                foreach (float ox in others)
                {
                    float d = ox < x ? x - ox : (ox > x + span ? ox - (x + span) : 0);
                    if (d < score)
                        score = d;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = x;
                }
            }
            return bestScore >= clearance ? best : -1;
        }

        /// <summary>
        /// Two standing spots <paramref name="gap"/> px apart anywhere in [xFrom, xTo]: same
        /// ground level under both (found from probeY downward), flat over +/- halfWidth,
        /// 45 px of air above the ground in both columns and along the segment at the
        /// humanoid's centre height (ground + k) and 12 px higher, as far as possible
        /// from the other entities the client knows about (at least clearance px).
        /// Returns the left x, or -1.
        /// </summary>
        public static int FindClearPair(TerrainSnapshot t, ScriptedClient c, short selfId, int xFrom, int xTo, int probeY, int gap, int halfWidth, float k, int clearance)
        {
            return FindClearPair(t, c, selfId, xFrom, xTo, probeY, gap, halfWidth, k, clearance, 45);
        }

        /// <summary>As above with the height of free air required above the ground in both columns (a single spot when gap is 0).</summary>
        public static int FindClearPair(TerrainSnapshot t, ScriptedClient c, short selfId, int xFrom, int xTo, int probeY, int gap, int halfWidth, float k, int clearance, int airAbove)
        {
            List<Vec2> others = new List<Vec2>();
            lock (c.Entities)
            {
                foreach (ScriptedClient.EntityState e in c.Entities.Values)
                    if (e.Id != selfId && !e.Removed)
                        others.Add(e.Position);
            }
            int best = -1;
            float bestScore = -1;
            for (int x = Math.Max(xFrom, halfWidth + 1); x + gap + halfWidth + 1 < Math.Min(xTo, t.Width); x += 2)
            {
                int g = Below(t, x, probeY);
                if (g < 0 || g == probeY || Below(t, x + gap, probeY) != g || !IsFlat(t, x, probeY, halfWidth) || !IsFlat(t, x + gap, probeY, halfWidth))
                    continue;
                bool air = true;
                for (int yy = g + 1; yy <= g + airAbove && air; yy++)
                    air = yy < t.Height && !t.IsSolid(x, yy) && !t.IsSolid(x + gap, yy);
                if (!air || !ClearBetween(t, x, x + gap, g + (int)k) || !ClearBetween(t, x, x + gap, g + (int)k + 12))
                    continue;
                float score = float.MaxValue;
                float cy = g + k;
                foreach (Vec2 o in others)
                {
                    float dx = o.X < x ? x - o.X : (o.X > x + gap ? o.X - (x + gap) : 0);
                    float dy = Math.Abs(o.Y - cy);
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (d < score)
                        score = d;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = x;
                }
            }
            return bestScore >= clearance ? best : -1;
        }

        /// <summary>True when no pixel of the horizontal segment from x1 to x2 at height y is solid.</summary>
        public static bool ClearBetween(TerrainSnapshot t, int x1, int x2, int y)
        {
            int a = Math.Min(x1, x2), b = Math.Max(x1, x2);
            for (int x = a; x <= b; x++)
                if (t.IsSolid(x, y))
                    return false;
            return true;
        }

        /// <summary>
        /// Distance along the ray from (x, y) at <paramref name="angle"/> (game
        /// convention: unit vector (cos, -sin), y up) to the first solid pixel,
        /// or -1 when the ray leaves the map first.
        /// </summary>
        public static int RayToTerrain(TerrainSnapshot t, float x, float y, float angle, int maxDistance)
        {
            float ux = (float)Math.Cos(angle), uy = -(float)Math.Sin(angle);
            for (int d = 0; d < maxDistance; d++)
            {
                int px = (int)(x + ux * d), py = (int)(y + uy * d);
                if (px < 0 || py < 0 || px >= t.Width || py >= t.Height)
                    return -1;
                if (t.IsSolid(px, py))
                    return d;
            }
            return -1;
        }
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>bases only; maxbots 1 (never a bot): for classes whose tests connect one client at a time.</summary>
    public class SoloBasesFixture : ServerFixture
    {
        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases");
            runtime.SetServerSetting("maxbots", "1");
            // Differs from the compiled default (SurvivalGameModeShared.maxCaptures = 4)
            // so a test can tell the settings file was read.
            runtime.SetServerSetting("maxcaptures", "5");
        }
    }

    /// <summary>complex only; maxbots 1.</summary>
    public class SoloComplexFixture : ServerFixture
    {
        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "complex");
            runtime.SetServerSetting("maxbots", "1");
        }
    }

    /// <summary>bases; default maxbots 6 (safe for up to three humans, see docs/TESTING.md): for two-client tests.</summary>
    public class DuoBasesFixture : ServerFixture
    {
        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases");
        }
    }
}
