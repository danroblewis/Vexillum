// Cross-platform stand-in for System.Windows.Forms.MessageBox (docs/PORTING.md
// step 7). The game calls MessageBox.Show from Program.cs and Vexillum.cs to
// report fatal errors; there is no native dialog on DesktopGL, so the text goes
// to stderr and the debug trace and the call returns at once. It never blocks.
using System.Diagnostics;

namespace System.Windows.Forms
{
    public enum DialogResult
    {
        None = 0,
        OK = 1,
        Cancel = 2,
        Abort = 3,
        Retry = 4,
        Ignore = 5,
        Yes = 6,
        No = 7,
    }

    public enum MessageBoxButtons
    {
        OK = 0,
        OKCancel = 1,
        AbortRetryIgnore = 2,
        YesNoCancel = 3,
        YesNo = 4,
        RetryCancel = 5,
    }

    public static class MessageBox
    {
        /// <summary>Text of the last message shown; lets tests observe the call.</summary>
        public static string LastText { get; private set; }
        public static string LastCaption { get; private set; }

        public static DialogResult Show(string text)
        {
            return Show(text, "");
        }

        public static DialogResult Show(string text, string caption)
        {
            return Show(text, caption, MessageBoxButtons.OK);
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons)
        {
            LastText = text;
            LastCaption = caption;
            string line = string.IsNullOrEmpty(caption)
                ? "[MessageBox] " + text
                : "[MessageBox] " + caption + ": " + text;
            try
            {
                Console.Error.WriteLine(line);
            }
            catch (Exception)
            {
                // stderr may be closed or redirected to nothing; a message box
                // must never take the process down.
            }
            Debug.WriteLine(line);
            return DialogResult.OK;
        }
    }
}
