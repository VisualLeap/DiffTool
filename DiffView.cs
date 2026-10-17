using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DiffTool
{
    public sealed class DiffLine
    {
        public string Text;
        internal string DisplayText;
        internal bool FixedWidthText;
        public char Kind;
        public int? OldNumber;
        public int? NewNumber;
    }

    public static class DiffParser
    {
        private static readonly Regex Hunk = new Regex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
        public static List<DiffLine> Parse(string diff)
        {
            var rows = new List<DiffLine>();
            int oldNumber = 0, newNumber = 0;
            bool inHunk = false;
            string[] lines = diff.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string text = lines[i].TrimEnd('\r');
                if (i == lines.Length - 1 && text.Length == 0) break;
                var row = new DiffLine { Text = text, Kind = 'm' };
                Match match = Hunk.Match(text);
                if (match.Success)
                {
                    oldNumber = int.Parse(match.Groups[1].Value);
                    newNumber = int.Parse(match.Groups[2].Value);
                    inHunk = true;
                    row.Kind = '@';
                }
                else if (text.StartsWith("diff ", StringComparison.Ordinal)) inHunk = false;
                else if (inHunk && text.Length > 0)
                {
                    row.Kind = text[0];
                    if (row.Kind == '-') row.OldNumber = oldNumber++;
                    else if (row.Kind == '+') row.NewNumber = newNumber++;
                    else if (row.Kind == ' ') { row.OldNumber = oldNumber++; row.NewNumber = newNumber++; }
                }
                rows.Add(row);
            }
            return rows;
        }
    }

    // Paint only visible rows so large diffs stay responsive. Line-number gutters stay fixed.
    public sealed class DiffView : Control
    {
        private readonly DarkScrollBar horizontal = new DarkScrollBar();
        private readonly Font codeFont = new Font("Consolas", 10.5f);
        private List<DiffLine> rows = new List<DiffLine>();
        private int rowHeight = 23;
        private int contentWidth;
        private int characterWidth;
        private int gutterWidth = 122;
        private const int MapWidth = 82;
        private int topLine;
        private int pageRows = 1;
        private Bitmap minimap;
        private bool draggingMap;
        private float dragOffset;
        private int MaxTopLine { get { return Math.Max(0, rows.Count - pageRows); } }
        private int EditorWidth { get { return Math.Max(1, Width - (rows.Count > 0 ? MapWidth : 0)); } }
        private int EditorHeight { get { return Math.Max(1, Height - (horizontal.Visible ? horizontal.Height : 0)); } }
        private int MapHeight { get { return Math.Max(1, Math.Min(Math.Max(1, Height - 8), rows.Count * 3)); } }
        public string EmptyTitle { get; set; }
        public string EmptyDetail { get; set; }
        public string RawDiff { get; private set; }
        public int Added { get; private set; }
        public int Deleted { get; private set; }

        public DiffView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Canvas;
            ForeColor = Theme.Text;
            TabStop = true;
            Controls.Add(horizontal);
            horizontal.ValueChanged += delegate
            {
                Invalidate(new Rectangle(gutterWidth, 0, Math.Max(1, EditorWidth - gutterWidth), EditorHeight));
                if (horizontal.Capture) Update();
            };
            EmptyTitle = "Select a file to review";
            EmptyDetail = "Deleted lines appear in red. Added lines appear in green.";
            RawDiff = "";
            AccessibleName = "Diff with file minimap";
            AccessibleDescription = "Click or drag the overview on the right to scroll. Red marks deletions and green marks additions.";
        }

        public void ShowMessage(string title, string detail)
        {
            EmptyTitle = title;
            EmptyDetail = detail;
            SetDiff("");
        }

        public void SetDiff(string diff)
        {
            RawDiff = diff;
            // Keep pathological generated files from overwhelming the renderer.
            bool truncated = diff.Length > 4 * 1024 * 1024;
            string preview = truncated ? diff.Substring(0, 4 * 1024 * 1024) : diff;
            if (truncated) preview += "\nPreview truncated at 4 MB. Copy diff includes the complete output.";
            rows = DiffParser.Parse(preview);
            Added = 0;
            Deleted = 0;
            int maxLength = 0;
            foreach (DiffLine row in rows)
            {
                if (row.Kind == '+') Added++;
                if (row.Kind == '-') Deleted++;
                row.DisplayText = row.Text.Replace("\t", "    ");
                row.FixedWidthText = true;
                foreach (char c in row.DisplayText)
                    if (c < ' ' || c > '~') { row.FixedWidthText = false; break; }
                maxLength = Math.Max(maxLength, row.DisplayText.Length);
            }
            rowHeight = Math.Max(23, codeFont.Height + 7);
            using (var graphics = CreateGraphics())
                characterWidth = Math.Max(1, TextRenderer.MeasureText(graphics, "M", codeFont, Size.Empty, TextFormatFlags.NoPadding).Width);
            contentWidth = (int)Math.Min(int.MaxValue / 2, (long)maxLength * characterWidth + 32);
            topLine = 0;
            draggingMap = false;
            Capture = false;
            if (minimap != null) { minimap.Dispose(); minimap = null; }
            horizontal.Value = 0;
            UpdateScrollbars();
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScrollbars(); }

        private void UpdateScrollbars()
        {
            int bar = SystemInformation.HorizontalScrollBarHeight;
            horizontal.SetBounds(gutterWidth, Math.Max(0, Height - bar), Math.Max(0, EditorWidth - gutterWidth), bar);
            horizontal.LargeChange = Math.Max(1, EditorWidth - gutterWidth);
            horizontal.SmallChange = 40;
            horizontal.Maximum = Math.Max(0, contentWidth);
            horizontal.Visible = rows.Count > 0 && contentWidth > horizontal.LargeChange;
            horizontal.Value = Math.Min(horizontal.Value, Math.Max(0, horizontal.Maximum - horizontal.LargeChange + 1));
            pageRows = Math.Max(1, EditorHeight / rowHeight);
            topLine = Math.Min(topLine, MaxTopLine);
            if (minimap != null && minimap.Height != MapHeight) { minimap.Dispose(); minimap = null; }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (rows.Count == 0)
            {
                using (var titleFont = new Font("Segoe UI", 17f))
                    TextRenderer.DrawText(e.Graphics, EmptyTitle, titleFont, new Rectangle(30, Math.Max(25, Height / 2 - 45), Math.Max(1, Width - 60), 42), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(e.Graphics, EmptyDetail, Font, new Rectangle(35, Math.Max(70, Height / 2 + 4), Math.Max(1, Width - 70), 100), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int glyphWidth = Math.Max(1, TextRenderer.MeasureText(e.Graphics, "M", codeFont, Size.Empty, TextFormatFlags.NoPadding).Width);
            int visible = EditorHeight / rowHeight + 1;
            for (int n = 0; n < visible && n + topLine < rows.Count; n++)
            {
                DiffLine row = rows[n + topLine];
                int y = n * rowHeight;
                Color bg = row.Kind == '+' ? Theme.AddBackground : row.Kind == '-' ? Theme.DeleteBackground : row.Kind == '@' ? Theme.HunkBackground : BackColor;
                Color fg = row.Kind == '+' ? Theme.Green : row.Kind == '-' ? Theme.Red : row.Kind == '@' ? Theme.Blue : row.Kind == 'm' ? Theme.Muted : Theme.Text;
                using (var brush = new SolidBrush(bg)) e.Graphics.FillRectangle(brush, 0, y, EditorWidth, rowHeight);
                var state = e.Graphics.Save();
                e.Graphics.SetClip(new Rectangle(gutterWidth, y, Math.Max(1, EditorWidth - gutterWidth), rowHeight), System.Drawing.Drawing2D.CombineMode.Intersect);
                string text = row.DisplayText;
                int textX = gutterWidth + 12 - horizontal.Value;
                if (row.FixedWidthText)
                {
                    // Consolas ASCII cells have a fixed advance. Avoid laying out off-screen text.
                    // Keep Unicode runs intact so shaping and fallback fonts retain their spacing.
                    int first = Math.Min(text.Length, Math.Max(0, (horizontal.Value - 12) / glyphWidth - 1));
                    int count = Math.Min(text.Length - first, (Math.Max(1, EditorWidth - gutterWidth) / glyphWidth) + 4);
                    text = text.Substring(first, count);
                    textX += first * glyphWidth;
                }
                TextRenderer.DrawText(e.Graphics, text, codeFont, new Point(textX, y + 3), fg, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
                e.Graphics.Restore(state);
                if (e.ClipRectangle.Left < gutterWidth)
                {
                    DrawNumber(e.Graphics, row.OldNumber, 0, y);
                    DrawNumber(e.Graphics, row.NewNumber, 57, y);
                }
            }
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, gutterWidth, 0, gutterWidth, Height);
            if (e.ClipRectangle.Right > EditorWidth) DrawMinimap(e.Graphics);
        }

        private void DrawMinimap(Graphics graphics)
        {
            if (minimap == null) BuildMinimap();
            int left = Width - MapWidth;
            using (var brush = new SolidBrush(Theme.Panel)) graphics.FillRectangle(brush, left, 0, MapWidth, Height);
            graphics.DrawImageUnscaled(minimap, left, 4);
            RectangleF viewport = MapViewport();
            using (var brush = new SolidBrush(Color.FromArgb(35, Theme.Blue))) graphics.FillRectangle(brush, viewport);
            using (var pen = new Pen(Color.FromArgb(150, Theme.Blue)))
                graphics.DrawRectangle(pen, viewport.X, viewport.Y, viewport.Width - 1, Math.Max(1, viewport.Height - 1));
            using (var pen = new Pen(Theme.Border)) graphics.DrawLine(pen, left, 0, left, Height);
        }

        private RectangleF MapViewport()
        {
            float height = Math.Min(MapHeight, Math.Max(8, MapHeight * pageRows / (float)Math.Max(1, rows.Count)));
            float y = MaxTopLine == 0 ? 0 : (MapHeight - height) * topLine / MaxTopLine;
            return new RectangleF(Width - MapWidth + 1, 4 + y, MapWidth - 2, height);
        }

        private void BuildMinimap()
        {
            minimap = new Bitmap(MapWidth, MapHeight);
            using (Graphics graphics = Graphics.FromImage(minimap))
            using (var neutral = new SolidBrush(Color.FromArgb(100, 114, 135)))
            using (var added = new SolidBrush(Theme.Green))
            using (var deleted = new SolidBrush(Theme.Red))
            using (var hunk = new SolidBrush(Theme.Blue))
            {
                graphics.Clear(Theme.Panel);
                // Aggregate each pixel band so dense files retain both red and green changes.
                float scale = MapHeight / (float)Math.Max(1, rows.Count);
                for (int y = 0; y < MapHeight; y++)
                {
                    int first = Math.Min(rows.Count - 1, (int)Math.Ceiling(y / scale));
                    int end = Math.Min(rows.Count, (int)Math.Ceiling((y + 1) / scale));
                    if (end <= first) continue;
                    bool hasAdded = false, hasDeleted = false;
                    DiffLine sample = rows[first];
                    for (int i = first; i < end; i++)
                    {
                        DiffLine row = rows[i];
                        hasAdded |= row.Kind == '+';
                        hasDeleted |= row.Kind == '-';
                        if (row.Text.Length > sample.Text.Length) sample = row;
                    }
                    if (hasDeleted) graphics.FillRectangle(deleted, 2, y, 3, 3);
                    if (hasAdded) graphics.FillRectangle(added, 6, y, 3, 3);
                    Brush stroke = hasAdded && !hasDeleted ? added : hasDeleted && !hasAdded ? deleted : sample.Kind == '@' ? hunk : neutral;
                    int column = 0, start = -1;
                    int prefix = sample.Kind == '+' || sample.Kind == '-' || sample.Kind == ' ' ? 1 : 0;
                    for (int i = prefix; i <= sample.Text.Length && column <= 112; i++)
                    {
                        bool space = i == sample.Text.Length || char.IsWhiteSpace(sample.Text[i]);
                        if (!space && start < 0) start = column;
                        if ((space || column == 112) && start >= 0)
                        {
                            graphics.FillRectangle(stroke, 12 + start * 0.58f, y, Math.Max(1, (column - start) * 0.58f), 1);
                            start = -1;
                        }
                        column += i < sample.Text.Length && sample.Text[i] == '\t' ? 4 : 1;
                    }
                }
            }
        }

        private void DrawNumber(Graphics graphics, int? number, int x, int y)
        {
            if (number.HasValue)
                TextRenderer.DrawText(graphics, number.Value.ToString(), codeFont, new Rectangle(x, y + 3, 52, rowHeight), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left || rows.Count == 0 || e.X < Width - MapWidth) return;
            RectangleF viewport = MapViewport();
            dragOffset = viewport.Contains(e.Location) ? e.Y - viewport.Y : viewport.Height / 2;
            draggingMap = true;
            Capture = true;
            ScrollFromMap(e.Y);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = rows.Count > 0 && e.X >= Width - MapWidth ? Cursors.Hand : Cursors.Default;
            if (draggingMap) ScrollFromMap(e.Y);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) { draggingMap = false; Capture = false; }
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) draggingMap = false;
        }
        private void ScrollFromMap(int y)
        {
            float travel = MapHeight - MapViewport().Height;
            if (travel > 0) ScrollTo((int)Math.Round(Math.Max(0, Math.Min(1, (y - 4 - dragOffset) / travel)) * MaxTopLine));
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(topLine - e.Delta / 120 * 3);
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Up) ScrollTo(topLine - 1);
            if (e.KeyCode == Keys.Down) ScrollTo(topLine + 1);
            if (e.KeyCode == Keys.PageUp) ScrollTo(topLine - pageRows);
            if (e.KeyCode == Keys.PageDown) ScrollTo(topLine + pageRows);
            if (e.KeyCode == Keys.Home) ScrollTo(0);
            if (e.KeyCode == Keys.End) ScrollTo(MaxTopLine);
        }
        private void ScrollTo(int value) { topLine = Math.Max(0, Math.Min(value, MaxTopLine)); Invalidate(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { codeFont.Dispose(); if (minimap != null) minimap.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
