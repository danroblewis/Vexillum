using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nuclex.UserInterface.Controls.Desktop;
using Microsoft.Xna.Framework.Input;

namespace Vexillum.ui
{
    class KeySelectorControl : InputControl
    {
        public Keys Key = Keys.None;
        private static System.Windows.Forms.KeysConverter kc = new System.Windows.Forms.KeysConverter();
        protected override bool OnKeyPressed(Keys keyCode)
        {
            if (!HasFocus)
                return false;
            Key = keyCode;
            if (keyCode == Keys.LeftShift || keyCode == Keys.RightShift || keyCode == Keys.LeftControl)
                Text = "Shift";
            else
                Text = keyCode.ToString();
            return true;
        }
        protected override void OnCharacterEntered(char character)
        {
            if(Char.IsLetterOrDigit(character) || Char.IsNumber(character) || Char.IsSeparator(character) || Char.IsPunctuation(character) || Char.IsSymbol(character))
                Text = character.ToString();
        }
    }
}
