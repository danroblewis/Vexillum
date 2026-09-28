// Inert stand-ins for the handful of System.Windows.Forms control types that
// ServerStart/HostServerForm(.Designer).cs uses (docs/PORTING.md step 7/9).
// They hold the properties the Designer code assigns and raise nothing: no
// window is ever created, and Application.Run refuses to start a message loop.
// Point, Size and SizeF come from System.Drawing.Primitives, which ships with
// .NET. Form.Icon is typed object until the System.Drawing shim (step 4)
// provides Icon; the Designer's cast to System.Drawing.Icon is what still
// keeps ServerStart out of the solution.
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms
{
    public enum AutoScaleMode
    {
        None = 0,
        Font = 1,
        Dpi = 2,
        Inherit = 3,
    }

    public enum SizeGripStyle
    {
        Auto = 0,
        Show = 1,
        Hide = 2,
    }

    public class Control : Component
    {
        private readonly ControlCollection controls;

        public Control()
        {
            controls = new ControlCollection(this);
        }

        public string Name { get; set; }
        public virtual string Text { get; set; }
        public Point Location { get; set; }
        public Size Size { get; set; }
        public Size ClientSize { get; set; }
        public int TabIndex { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Visible { get; set; } = true;
        public Control Parent { get; internal set; }
        public ControlCollection Controls { get { return controls; } }

        public event EventHandler Click;
        public event EventHandler TextChanged;

        protected virtual void OnClick(EventArgs e)
        {
            var h = Click;
            if (h != null) h(this, e);
        }

        protected virtual void OnTextChanged(EventArgs e)
        {
            var h = TextChanged;
            if (h != null) h(this, e);
        }

        public void SuspendLayout() { }
        public void ResumeLayout() { }
        public void ResumeLayout(bool performLayout) { }
        public void PerformLayout() { }
        public void Show() { Visible = true; }
        public void Hide() { Visible = false; }

        public class ControlCollection : List<Control>
        {
            private readonly Control owner;

            public ControlCollection(Control owner)
            {
                this.owner = owner;
            }

            public Control Owner { get { return owner; } }

            public new void Add(Control value)
            {
                if (value == null) return;
                value.Parent = owner;
                base.Add(value);
            }
        }
    }

    public class ScrollableControl : Control
    {
    }

    public class ContainerControl : ScrollableControl
    {
        public SizeF AutoScaleDimensions { get; set; }
        public AutoScaleMode AutoScaleMode { get; set; }
    }

    public class Form : ContainerControl
    {
        /// <summary>System.Drawing.Icon once the Drawing shim defines it.</summary>
        public object Icon { get; set; }
        public SizeGripStyle SizeGripStyle { get; set; }
        public DialogResult DialogResult { get; set; }

        public event EventHandler Load;
        public event FormClosedEventHandler FormClosed;

        protected virtual void OnLoad(EventArgs e)
        {
            var h = Load;
            if (h != null) h(this, e);
        }

        protected virtual void OnFormClosed(FormClosedEventArgs e)
        {
            var h = FormClosed;
            if (h != null) h(this, e);
        }

        public void Close() { Dispose(); }
        public DialogResult ShowDialog() { return DialogResult.Cancel; }
    }

    public delegate void FormClosedEventHandler(object sender, FormClosedEventArgs e);

    public class FormClosedEventArgs : EventArgs
    {
    }

    public class Label : Control
    {
        public bool AutoSize { get; set; }
    }

    public class TextBoxBase : Control
    {
        public bool Multiline { get; set; }
        public bool ReadOnly { get; set; }
    }

    public class TextBox : TextBoxBase
    {
    }

    public class ButtonBase : Control
    {
        public bool UseVisualStyleBackColor { get; set; }
    }

    public class Button : ButtonBase
    {
        public void PerformClick() { OnClick(EventArgs.Empty); }
    }
}
