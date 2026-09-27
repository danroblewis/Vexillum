using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.GamerServices;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Media;
using Vexillum.physics;
using Nuclex.Input;
using Vexillum.util;
using Vexillum.Entities.stance;

namespace  Vexillum.Entities.Weapons
{
    public class RocketLauncher : ReloadableWeapon
    {
        private static Texture2D rocket;
        private Entity owner;
        static RocketLauncher() {
            rocket = AssetManager.loadTexture("rocket.png");
        }
        public RocketLauncher() : base(20, 4, 500, 250)
        {
            Size = new Vec2(21, 6);
        }

        /*public override void mouseDownClient(Level l, double angle, int button, int x, int y)
        {

        }

        public override void mouseDraggedClient(Level l, double angle, int button, int x, int y)
        {

        }

        public override void mouseMovedClient(Level l, double angle, int button, int x, int y)
        {

        }

        public override void mouseUpClient(Level l, double angle, int button, int x, int y)
        {

        }*/

        public override void FireServer(Level l, float angle)
        {
            Rocket r = new Rocket();
            r.Position = GetEnd(angle);
            l.AddProjectile(r, (float)angle, entity);
            stance.Fire();
        }
        public override void FireMenu(Level l, float angle)
        {
            Rocket r = new Rocket();
            r.Position = GetEnd(angle);
            l.AddProjectile(r, (float)angle, entity);
            stance.Fire();
        }

        public override Texture2D GetImage()
        {
            return rocket;
        }

        public override Stance GetStanceInstance(HumanoidEntity e)
        {
            return new RocketLauncherStance(this, e);
        }
    }
}
