using System;
using System.Drawing;
using System.Windows.Forms;

namespace DiffTool
{
    internal sealed class DarkScrollBar : Control
    {
        private int value;
        private int maximum;
        private int largeChange = 1;
        private bool dragging;
        private bool hovering;
        private int dragOffset;
        private int pointerX;
        private readonly Timer repeat = new Timer();
        public event EventHandler ValueChanged;
        public int SmallChange { get; set; }
        public int Maximum { get { return maximum; } set { maximum = Math.Max(0, value); ClampValue(); } }
        public int LargeChange { get { return largeChange; } set { largeChange = Math.Max(1, value); ClampValue(); } }
        private int LastValue { get { return Math.Max(0, maximum - largeChange + 1); } }
        public int Value
        {
            get { return value; }
            set
            {
                int next = Math.Max(0, Math.Min(LastValue, value));
                if (this.value == next) return;
                this.value = next;
                Invalidate();
                if (dragging) Update();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public DarkScrollBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Panel;
            TabStop = true;
            SmallChange = 40;
            AccessibleRole = AccessibleRole.ScrollBar;
            AccessibleName = "Horizontal source scroll";
            AccessibleDescription = "Drag to scroll horizontally. Arrow keys scroll; Home and End move to the start and end.";
            repeat.Tick += delegate { repeat.Interval = 60; PageTowardPointer(); };
        }

        private void ClampValue() { Value = value; Invalidate(); }

        private Rectangle Thumb()
        {
            int trackWidth = Math.Max(1, ClientSize.Width - 4);
            int thumbWidth = Math.Min(trackWidth, Math.Max(24, (int)(trackWidth * (largeChange / (double)Math.Max(1L, (long)maximum + 1)))));
            int x = 2 + (LastValue == 0 ? 0 : (int)Math.Round((trackWidth - thumbWidth) * (value / (double)LastValue)));
            return new Rectangle(x, 3, thumbWidth, Math.Max(1, Height - 6));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var border = new Pen(Theme.Border)) e.Graphics.DrawLine(border, 0, 0, Width, 0);
            using (var brush = new SolidBrush(dragging ? Theme.Blue : hovering ? Color.FromArgb(95, 111, 135) : Color.FromArgb(65, 78, 98)))
                e.Graphics.FillRectangle(brush, Thumb());
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Thumb(), Theme.Muted, BackColor);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            pointerX = e.X;
            Rectangle thumb = Thumb();
            dragging = thumb.Contains(e.Location);
            Capture = true;
            if (dragging) dragOffset = e.X - thumb.X;
            else { PageTowardPointer(); repeat.Interval = 400; repeat.Start(); }
            Invalidate();
        }

        private void PageTowardPointer()
        {
            Rectangle thumb = Thumb();
            if (pointerX < thumb.Left) Value -= largeChange;
            else if (pointerX >= thumb.Right) Value = (int)Math.Min(LastValue, (long)value + largeChange);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            pointerX = e.X;
            if (dragging)
            {
                int travel = Math.Max(0, ClientSize.Width - 4 - Thumb().Width);
                if (travel > 0) Value = (int)Math.Round(Math.Max(0, Math.Min(1, (e.X - 2 - dragOffset) / (double)travel)) * LastValue);
            }
            bool over = Thumb().Contains(e.Location);
            if (hovering != over) { hovering = over; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) { dragging = false; repeat.Stop(); Capture = false; Invalidate(); }
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) { dragging = false; repeat.Stop(); Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left: Value -= SmallChange; break;
                case Keys.Right: Value = (int)Math.Min(LastValue, (long)value + SmallChange); break;
                case Keys.PageUp: Value -= largeChange; break;
                case Keys.PageDown: Value = (int)Math.Min(LastValue, (long)value + largeChange); break;
                case Keys.Home: Value = 0; break;
                case Keys.End: Value = LastValue; break;
                default: return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        protected override void Dispose(bool disposing) { if (disposing) repeat.Dispose(); base.Dispose(disposing); }
    }
}