// Stand-in for System.Windows.Forms.KeysConverter (docs/PORTING.md step 7).
// ui/KeySelectorControl.cs constructs one but never calls it, so an empty
// class with a public constructor is all that is required.
namespace System.Windows.Forms
{
    public class KeysConverter
    {
        public KeysConverter()
        {
        }
    }
}
