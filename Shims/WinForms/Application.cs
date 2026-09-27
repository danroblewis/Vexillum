// Cross-platform stand-in for System.Windows.Forms.Application and the Win32
// message-filter plumbing (docs/PORTING.md step 7).
//
// Vexillum.Initialize registers a KeyboardMessageFilter through
// Application.AddMessageFilter to receive WM_CHAR. MonoGame DesktopGL has no
// Win32 message pump, so filters are accepted and kept but PreFilterMessage is
// never invoked; text input comes from MonoGame's Window.TextInput instead.
using System.Collections.Generic;

namespace System.Windows.Forms
{
    /// <summary>Mirror of the Win32 MSG structure as WinForms exposes it.</summary>
    public struct Message
    {
        public IntPtr HWnd { get; set; }
        public int Msg { get; set; }
        public IntPtr WParam { get; set; }
        public IntPtr LParam { get; set; }
        public IntPtr Result { get; set; }

        public static Message Create(IntPtr hWnd, int msg, IntPtr wparam, IntPtr lparam)
        {
            return new Message { HWnd = hWnd, Msg = msg, WParam = wparam, LParam = lparam };
        }
    }

    public interface IMessageFilter
    {
        bool PreFilterMessage(ref Message m);
    }

    public static class Application
    {
        private static readonly List<IMessageFilter> filters = new List<IMessageFilter>();

        /// <summary>Filters registered so far (never called; kept for inspection).</summary>
        public static IReadOnlyList<IMessageFilter> MessageFilters
        {
            get { lock (filters) return filters.ToArray(); }
        }

        public static void AddMessageFilter(IMessageFilter value)
        {
            if (value == null) return;
            lock (filters) filters.Add(value);
        }

        public static void RemoveMessageFilter(IMessageFilter value)
        {
            if (value == null) return;
            lock (filters) filters.Remove(value);
        }

        public static void EnableVisualStyles()
        {
        }

        public static void SetCompatibleTextRenderingDefault(bool defaultValue)
        {
        }

        public static void DoEvents()
        {
        }

        public static void Exit()
        {
        }

        /// <summary>
        /// There is no WinForms message loop on this platform. ServerStart's
        /// HostServerForm is the only caller; it is not shipped in the port.
        /// </summary>
        public static void Run(Form mainForm)
        {
            throw new NotSupportedException("ServerStart GUI is not available on this platform");
        }
    }
}
