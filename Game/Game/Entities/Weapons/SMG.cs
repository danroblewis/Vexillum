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
using Vexillum.net;

namespace  Vexillum.Entities.Weapons
{
    public class SMG : ReloadableWeapon
    {
        private static Texture2D smg;
        static SMG() {
            smg = AssetManager.loadTexture("smg.png");
        }
        public SMG() : base(200, 50, 100, 75)
        {
            Size = new Vec2(21, 6);
            baseDamage = 3;
            minimumDamage = 1;
            minRange = 50;
            maxRange = 450;
        }

        public override void FireServer(Level l, float angle)
        {
            stance.Fire();
            l.PlaySound(entity.player, Sounds.SMG, entity);
        }
        public override void FireMenu(Level l, float angle)
        {
            l.AddHitscan(GetPivot(), entity.ArmAngle, entity);
            stance.Fire();
            l.PlaySound(entity.player, Sounds.SMG, entity);
        }

        public override void FireClient(Client cl, Level l, float angle)
        {
            cl.SendHitscan();
            l.AddHitscan(GetPivot(), entity.ArmAngle, entity);
            l.PlaySound(null, Sounds.SMG, entity);
        }

        public override Texture2D GetImage()
        {
            return smg;
        }

        public override Stance GetStanceInstance(HumanoidEntity e)
        {
            return new SMGStance(this, e);
        }
    }
}
