using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
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
        // Keep fractional rows so small trackpad deltas move by pixels, not whole lines.
        private double topLine;
        private int pageRows = 1;
        private Bitmap minimap;
        private bool draggingMap;
        private float dragOffset;
        // Selection endpoints are columns in DisplayText. The anchor stays put while the caret follows the mouse.
        private int anchorLine, anchorColumn, caretLine, caretColumn;
        private bool selecting;
        private readonly Timer dragScrollTimer = new Timer { Interval = 40 };
        private const int TextPadding = 12;
        private const TextFormatFlags CodeFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        private double VisibleRows { get { return EditorHeight / (double)rowHeight; } }
        private double MaxTopLine { get { return Math.Max(0, rows.Count - VisibleRows); } }
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
            dragScrollTimer.Tick += delegate { DragScroll(); };
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
            selecting = false;
            dragScrollTimer.Stop();
            anchorLine = anchorColumn = caretLine = caretColumn = 0;
            Capture = false;
            if (minimap != null) { minimap.Dispose(); minimap = null; }
            horizontal.Value = 0;
            UpdateScrollbars();
            // Center the first changed row after the visible page size is known.
            int firstChange = rows.FindIndex(row => row.Kind == '+' || row.Kind == '-');
            if (firstChange >= 0) ScrollTo(firstChange + 0.5 - VisibleRows / 2);
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
            int firstLine = (int)topLine;
            int pixelOffset = (int)((topLine - firstLine) * rowHeight);
            int visible = (EditorHeight + pixelOffset + rowHeight - 1) / rowHeight;
            int startLine, startColumn, endLine, endColumn;
            bool hasSelection = SelectionRange(out startLine, out startColumn, out endLine, out endColumn);
            using (var selectionBrush = new SolidBrush(Theme.Selection))
            for (int n = 0; n < visible && n + firstLine < rows.Count; n++)
            {
                DiffLine row = rows[n + firstLine];
                int y = n * rowHeight - pixelOffset;
                Color bg = row.Kind == '+' ? Theme.AddBackground : row.Kind == '-' ? Theme.DeleteBackground : row.Kind == '@' ? Theme.HunkBackground : BackColor;
                Color fg = row.Kind == '+' ? Theme.Green : row.Kind == '-' ? Theme.Red : row.Kind == '@' ? Theme.Blue : row.Kind == 'm' ? Theme.Muted : Theme.Text;
                using (var brush = new SolidBrush(bg)) e.Graphics.FillRectangle(brush, 0, y, EditorWidth, rowHeight);
                var state = e.Graphics.Save();
                e.Graphics.SetClip(new Rectangle(gutterWidth, y, Math.Max(1, EditorWidth - gutterWidth), rowHeight), System.Drawing.Drawing2D.CombineMode.Intersect);
                string text = row.DisplayText;
                int textX = gutterWidth + TextPadding - horizontal.Value;
                int line = n + firstLine;
                if (hasSelection && line >= startLine && line <= endLine)
                {
                    int from = line == startLine ? startColumn : MarkerLength(row);
                    int to = line == endLine ? endColumn : text.Length;
                    // Extend past the last character when the line break itself is selected.
                    int left = Math.Max(gutterWidth, textX + ColumnOffset(row, from, glyphWidth));
                    int right = Math.Min(EditorWidth, textX + ColumnOffset(row, to, glyphWidth) + (line < endLine ? glyphWidth : 0));
                    if (right > left) e.Graphics.FillRectangle(selectionBrush, left, y, right - left, rowHeight);
                }
                if (row.FixedWidthText)
                {
                    // Consolas ASCII cells have a fixed advance. Avoid laying out off-screen text.
                    // Keep Unicode runs intact so shaping and fallback fonts retain their spacing.
                    int first = Math.Min(text.Length, Math.Max(0, (horizontal.Value - TextPadding) / glyphWidth - 1));
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
            MinimapViewport.Draw(graphics, MapViewport());
            using (var pen = new Pen(Theme.Border)) graphics.DrawLine(pen, left, 0, left, Height);
        }

        private RectangleF MapViewport()
        {
            float height = (float)Math.Min(MapHeight, Math.Max(8, MapHeight * VisibleRows / Math.Max(1, rows.Count)));
            float y = MaxTopLine == 0 ? 0 : (float)((MapHeight - height) * topLine / MaxTopLine);
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
            if (e.Button != MouseButtons.Left || rows.Count == 0) return;
            if (e.X < Width - MapWidth)
            {
                int line, column;
                PositionAt(e.Location, out line, out column);
                if (e.Clicks == 2) SelectWord(line, column);
                else
                {
                    caretLine = line;
                    caretColumn = column;
                    if ((ModifierKeys & Keys.Shift) == 0) { anchorLine = line; anchorColumn = column; }
                    selecting = true;
                    Capture = true;
                    dragScrollTimer.Start();
                }
                Invalidate();
                return;
            }
            RectangleF viewport = MapViewport();
            dragOffset = viewport.Contains(e.Location) ? e.Y - viewport.Y : viewport.Height / 2;
            draggingMap = true;
            Capture = true;
            ScrollFromMap(e.Y);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (selecting) { Cursor = Cursors.IBeam; MoveCaretTo(e.Location); return; }
            Cursor = rows.Count == 0 || draggingMap ? (draggingMap ? Cursors.Hand : Cursors.Default)
                : e.X >= Width - MapWidth ? Cursors.Hand
                : e.X >= gutterWidth && e.Y < EditorHeight ? Cursors.IBeam : Cursors.Default;
            if (draggingMap) ScrollFromMap(e.Y);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) { draggingMap = false; StopSelecting(); Capture = false; }
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) { draggingMap = false; StopSelecting(); }
        }
        private void StopSelecting()
        {
            selecting = false;
            dragScrollTimer.Stop();
        }
        private void MoveCaretTo(Point point)
        {
            int line, column;
            PositionAt(point, out line, out column);
            if (line == caretLine && column == caretColumn) return;
            caretLine = line;
            caretColumn = column;
            Invalidate();
        }
        // Keep extending the selection while the pointer is held outside the text area.
        private void DragScroll()
        {
            if (!selecting || rows.Count == 0) { dragScrollTimer.Stop(); return; }
            Point point = PointToClient(MousePosition);
            if (point.Y < 0) ScrollTo(topLine - Math.Min(pageRows, 1 + -point.Y / rowHeight));
            else if (point.Y >= EditorHeight) ScrollTo(topLine + Math.Min(pageRows, 1 + (point.Y - EditorHeight) / rowHeight));
            if (horizontal.Visible)
            {
                int last = Math.Max(0, horizontal.Maximum - horizontal.LargeChange + 1);
                if (point.X < gutterWidth) horizontal.Value = Math.Max(0, horizontal.Value - 40);
                else if (point.X >= EditorWidth) horizontal.Value = Math.Min(last, horizontal.Value + 40);
            }
            MoveCaretTo(point);
        }

        // Diff markers (+, -, or the context space) are not selectable, so copied code pastes cleanly.
        private static int MarkerLength(DiffLine row)
        {
            return row.Kind == '+' || row.Kind == '-' || row.Kind == ' ' ? 1 : 0;
        }
        private int ColumnOffset(DiffLine row, int column, int glyphWidth)
        {
            if (row.FixedWidthText) return column * glyphWidth;
            return column <= 0 ? 0 : TextRenderer.MeasureText(row.DisplayText.Substring(0, column), codeFont, Size.Empty, CodeFlags).Width;
        }
        private void PositionAt(Point point, out int line, out int column)
        {
            line = (int)Math.Floor(topLine + point.Y / (double)rowHeight);
            if (line < 0) { line = 0; column = MarkerLength(rows[0]); return; }
            if (line >= rows.Count) { line = rows.Count - 1; column = rows[line].DisplayText.Length; return; }
            DiffLine row = rows[line];
            string text = row.DisplayText;
            int x = point.X - gutterWidth - TextPadding + horizontal.Value;
            if (row.FixedWidthText) column = (int)Math.Round(x / (double)characterWidth);
            else
            {
                int low = 0, high = text.Length;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (ColumnOffset(row, middle, characterWidth) <= x) low = middle; else high = middle - 1;
                }
                column = low;
                if (column < text.Length && x - ColumnOffset(row, column, characterWidth) > ColumnOffset(row, column + 1, characterWidth) - x) column++;
                if (column > 0 && column < text.Length && char.IsLowSurrogate(text[column])) column++;
            }
            column = Math.Max(MarkerLength(row), Math.Min(text.Length, column));
        }
        private static int CharacterClass(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' ? 0 : char.IsWhiteSpace(c) ? 1 : 2;
        }
        private void SelectWord(int line, int column)
        {
            DiffLine row = rows[line];
            string text = row.DisplayText;
            int minimum = MarkerLength(row);
            anchorLine = caretLine = line;
            anchorColumn = caretColumn = column;
            if (text.Length <= minimum) return;
            int start = Math.Min(column, text.Length - 1), end = start + 1;
            int kind = CharacterClass(text[start]);
            while (start > minimum && CharacterClass(text[start - 1]) == kind) start--;
            while (end < text.Length && CharacterClass(text[end]) == kind) end++;
            anchorColumn = start;
            caretColumn = end;
        }
        private bool SelectionRange(out int startLine, out int startColumn, out int endLine, out int endColumn)
        {
            bool forward = anchorLine < caretLine || (anchorLine == caretLine && anchorColumn <= caretColumn);
            startLine = forward ? anchorLine : caretLine;
            startColumn = forward ? anchorColumn : caretColumn;
            endLine = forward ? caretLine : anchorLine;
            endColumn = forward ? caretColumn : anchorColumn;
            return rows.Count > 0 && (startLine != endLine || startColumn != endColumn);
        }
        // DisplayText expands tabs to four columns; map back so copies keep the original characters.
        private static int SourceIndex(DiffLine row, int column)
        {
            int display = 0;
            for (int i = 0; i < row.Text.Length; i++)
            {
                if (display >= column) return i;
                display += row.Text[i] == '\t' ? 4 : 1;
            }
            return row.Text.Length;
        }
        public string SelectedText
        {
            get
            {
                int startLine, startColumn, endLine, endColumn;
                if (!SelectionRange(out startLine, out startColumn, out endLine, out endColumn)) return "";
                var text = new StringBuilder();
                for (int line = startLine; line <= endLine; line++)
                {
                    DiffLine row = rows[line];
                    int from = SourceIndex(row, line == startLine ? startColumn : MarkerLength(row));
                    int to = SourceIndex(row, line == endLine ? endColumn : row.DisplayText.Length);
                    if (to > from) text.Append(row.Text, from, to - from);
                    if (line < endLine) text.Append("\r\n");
                }
                return text.ToString();
            }
        }
        public void SelectAll()
        {
            if (rows.Count == 0) return;
            anchorLine = 0;
            anchorColumn = MarkerLength(rows[0]);
            caretLine = rows.Count - 1;
            caretColumn = rows[caretLine].DisplayText.Length;
            Invalidate();
        }
        public void CopySelection()
        {
            string text = SelectedText;
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); }
            catch (ExternalException) { }
        }
        private void ScrollFromMap(int y)
        {
            float travel = MapHeight - MapViewport().Height;
            if (travel > 0) ScrollTo(Math.Max(0, Math.Min(1, (y - 4 - dragOffset) / travel)) * MaxTopLine);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
            base.OnMouseWheel(e);
            if (!Enabled || rows.Count == 0) return;
            ScrollTo(WheelScroll.GetOffset(topLine * rowHeight, MaxTopLine * rowHeight, e.Delta,
                SystemInformation.MouseWheelScrollLines, rowHeight, EditorHeight) / rowHeight);
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && !e.Alt && (e.KeyCode == Keys.C || e.KeyCode == Keys.Insert)) { e.SuppressKeyPress = true; CopySelection(); }
            if (e.Control && !e.Alt && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; SelectAll(); }
            if (e.KeyCode == Keys.Up) ScrollTo(topLine - 1);
            if (e.KeyCode == Keys.Down) ScrollTo(topLine + 1);
            if (e.KeyCode == Keys.PageUp) ScrollTo(topLine - pageRows);
            if (e.KeyCode == Keys.PageDown) ScrollTo(topLine + pageRows);
            if (e.KeyCode == Keys.Home) ScrollTo(0);
            if (e.KeyCode == Keys.End) ScrollTo(MaxTopLine);
        }
        private void ScrollTo(double value)
        {
            double next = Math.Max(0, Math.Min(value, MaxTopLine));
            if (next == topLine) return;
            topLine = next;
            Invalidate();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { dragScrollTimer.Dispose(); codeFont.Dispose(); if (minimap != null) minimap.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
