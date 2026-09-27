using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace Vexillum.Tests
{
    /// <summary>
    /// Shims/WinForms: the System.Windows.Forms stand-ins the historical
    /// sources compile against (docs/PORTING.md step 7).
    /// </summary>
    public class WinFormsShimTests
    {
        private sealed class CountingFilter : IMessageFilter
        {
            public int Calls;

            public bool PreFilterMessage(ref Message m)
            {
                Calls++;
                return false;
            }
        }

        [Fact]
        public void MessageBoxShowReturnsOkWithoutBlocking()
        {
            var task = Task.Run(() => MessageBox.Show("shim test message"));
            Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "MessageBox.Show blocked");
            Assert.Equal(DialogResult.OK, task.Result);
            Assert.Equal("shim test message", MessageBox.LastText);
        }

        [Fact]
        public void MessageBoxShowWithCaptionAndButtonsReturnsOk()
        {
            Assert.Equal(DialogResult.OK, MessageBox.Show("text", "Error"));
            Assert.Equal("Error", MessageBox.LastCaption);
            Assert.Equal(DialogResult.OK, MessageBox.Show("text", "Error", MessageBoxButtons.YesNo));
        }

        [Fact]
        public void ApplicationAddMessageFilterAcceptsFilterAndNeverInvokesIt()
        {
            var filter = new CountingFilter();
            Application.AddMessageFilter(filter);
            Assert.Contains(filter, Application.MessageFilters);
            Assert.Equal(0, filter.Calls);
            Application.RemoveMessageFilter(filter);
            Assert.DoesNotContain(filter, Application.MessageFilters);
        }

        [Fact]
        public void KeyboardMessageFilterFromGameCanBeRegistered()
        {
            // The historical Vexillum.util.KeyboardMessageFilter (with its
            // user32 TranslateMessage DllImport) must compile and register.
            var filter = new Vexillum.util.KeyboardMessageFilter();
            Application.AddMessageFilter(filter);
            Assert.Contains(filter, Application.MessageFilters);
            Application.RemoveMessageFilter(filter);
        }

        [Fact]
        public void KeysConverterConstructs()
        {
            Assert.NotNull(new KeysConverter());
        }

        [Fact]
        public void ApplicationRunIsNotSupported()
        {
            Assert.Throws<NotSupportedException>(() => Application.Run(new Form()));
        }
    }
}
