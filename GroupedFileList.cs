using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DiffTool
{
    internal sealed class GroupedFileList : ListBox
    {
        private sealed class Row
        {
            public FileChange File;
            public string Label;
            public int Group;
            public bool Gap;
            public override string ToString() { return File == null ? Label ?? "" : File.DisplayPath; }
        }

        private readonly HashSet<int> collapsed = new HashSet<int>();
        private readonly ToolTip tooltip = new ToolTip();
        private readonly Font sectionFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        private List<FileChange> files = new List<FileChange>();
        private string filter = "";
        private string selectedPath;
        private bool rebuilding;
        private int lastSelection = -1;
        private int hovered = -1;
        private int wheelRemainder;
        private Control columnHeader;
        private int sortColumn = -1;
        private bool sortDescending;
        private FileListMinimap minimap;
        public int FileCount { get; private set; }
        public FileChange SelectedChange { get { var row = SelectedItem as Row; return row == null ? null : row.File; } }

        public GroupedFileList()
        {
            DrawMode = DrawMode.OwnerDrawVariable;
            IntegralHeight = false;
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams parameters = base.CreateParams; parameters.Style &= ~0x00200000; return parameters; }
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (minimap != null && (message.Msg == 0x0197 || message.Msg == 0x0186 || message.Msg == 0x0100 || message.Msg == 0x020A || message.Msg == 0x0115))
                minimap.Invalidate();
        }

        public Control CreateMinimap()
        {
            minimap = new FileListMinimap(this) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            tooltip.SetToolTip(minimap, "File overview: red deleted, green added, blue modified. Click or drag to scroll.");
            return minimap;
        }

        public Control CreateColumnHeader()
        {
            columnHeader = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Panel, Cursor = Cursors.Hand };
            tooltip.SetToolTip(columnHeader, "Click to sort within each category; click again to reverse. Changed sorts by additions plus deletions.");
            columnHeader.Paint += delegate(object sender, PaintEventArgs e)
            {
                Rectangle[] columns = ColumnBounds(0, columnHeader.Height);
                string[] labels = { "Filename", "Size", "Lines", "Changed" };
                for (int i = 0; i < columns.Length; i++)
                    DrawCell(e.Graphics, labels[i] + (sortColumn == i ? sortDescending ? " ▼" : " ▲" : ""), columns[i], sortColumn == i ? Theme.Text : Theme.Muted, i != 0 && i != 3);
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, columnHeader.Height - 1, columnHeader.Width, columnHeader.Height - 1);
            };
            columnHeader.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || e.X < 0 || e.X >= ClientSize.Width) return;
                Rectangle[] columns = ColumnBounds(0, columnHeader.Height);
                int column = 0;
                for (int i = 1; i < columns.Length; i++) if (e.X >= columns[i].X) column = i;
                SortByColumn(column);
            };
            return columnHeader;
        }

        private void SortByColumn(int column)
        {
            sortDescending = sortColumn == column && !sortDescending;
            sortColumn = column;
            Rebuild(false);
        }

        private int CompareFiles(FileChange left, FileChange right)
        {
            int comparison;
            if (sortColumn == 0) comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
            else
            {
                long? a = SortValue(left), b = SortValue(right);
                // Unknown values remain last in either direction.
                if (a.HasValue != b.HasValue) return a.HasValue ? -1 : 1;
                comparison = Nullable.Compare(a, b);
            }
            if (comparison != 0) return sortDescending ? -comparison : comparison;
            return StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
        }

        private long? SortValue(FileChange file)
        {
            if (sortColumn == 1) return file.SizeBytes;
            if (sortColumn == 2) return file.LineCount;
            return file.AddedLines + file.DeletedLines;
        }

        private Rectangle[] ColumnBounds(int y, int height)
        {
            int sizeWidth = TextRenderer.MeasureText("999.9 MB", Font).Width + 12;
            int linesWidth = TextRenderer.MeasureText("999,999", Font).Width + 10;
            int changedWidth = TextRenderer.MeasureText("99,999  99,999", Font).Width + 12;
            int nameWidth = Math.Max(40, ClientSize.Width - sizeWidth - linesWidth - changedWidth);
            return new[]
            {
                new Rectangle(17, y, Math.Max(1, nameWidth - 21), height),
                new Rectangle(nameWidth, y, sizeWidth - 8, height),
                new Rectangle(nameWidth + sizeWidth, y, linesWidth - 6, height),
                new Rectangle(nameWidth + sizeWidth + linesWidth, y, changedWidth - 6, height)
            };
        }

        private void DrawCell(Graphics graphics, string text, Rectangle bounds, Color color, bool rightAligned)
        {
            TextRenderer.DrawText(graphics, text, Font, bounds, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                (rightAligned ? TextFormatFlags.Right | TextFormatFlags.EndEllipsis : TextFormatFlags.PathEllipsis));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
            if (columnHeader != null) columnHeader.Invalidate();
            if (minimap != null) minimap.RefreshRows();
        }

        public void SetFiles(List<FileChange> changes, string pathFilter, string path)
        {
            files = changes;
            filter = pathFilter;
            selectedPath = path;
            Rebuild();
        }

        private void Rebuild(bool notifySelection = true)
        {
            FileChange previousSelection = SelectedChange;
            rebuilding = true;
            BeginUpdate();
            Items.Clear();
            wheelRemainder = 0;
            hovered = -1;
            tooltip.SetToolTip(this, null);
            FileCount = 0;
            int selection = -1, firstFile = -1;
            string[] names = { "Deleted", "Added", "Modified" };
            for (int group = 0; group < names.Length; group++)
            {
                var groupFiles = files.Where(file => Group(file) == group && file.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(file => Path.GetDirectoryName(file.Path), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
                if (sortColumn >= 0) groupFiles.Sort(CompareFiles);
                FileCount += groupFiles.Count;
                if (group > 0) Items.Add(new Row { Gap = true });
                bool expanded = !collapsed.Contains(group) || filter.Length > 0;
                Items.Add(new Row { Label = (expanded ? "−  " : "+  ") + names[group] + "  (" + groupFiles.Count + ")", Group = group });
                if (!expanded) continue;
                string previousFolder = null;
                foreach (FileChange file in groupFiles)
                {
                    string folder = Path.GetDirectoryName(file.Path);
                    if (previousFolder != null && !string.Equals(folder, previousFolder, StringComparison.OrdinalIgnoreCase))
                        Items.Add(new Row { Gap = true });
                    previousFolder = folder;
                    int index = Items.Add(new Row { File = file, Group = group });
                    if (firstFile < 0) firstFile = index;
                    if (file.Path == selectedPath) selection = index;
                }
            }
            SelectedIndex = selection >= 0 ? selection : firstFile;
            lastSelection = SelectedIndex;
            EndUpdate();
            if (minimap != null) minimap.RefreshRows();
            if (columnHeader != null) columnHeader.Invalidate();
            rebuilding = false;
            if (notifySelection || !ReferenceEquals(previousSelection, SelectedChange)) OnSelectedIndexChanged(EventArgs.Empty);
        }

        private static int Group(FileChange file) { return file.Status == 'D' ? 0 : file.Status == 'A' || file.Status == 'C' ? 1 : 2; }

        protected override void OnMeasureItem(MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) { e.ItemHeight = Math.Max(20, Font.Height + 2); return; }
            Row row = (Row)Items[e.Index];
            e.ItemHeight = row.Gap ? Math.Max(4, Font.Height / 3) : Math.Max(20, (row.File == null ? sectionFont.Height : Font.Height) + 2);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            Row row = (Row)Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0 && !row.Gap;
            using (var brush = new SolidBrush(selected ? Theme.HunkBackground : BackColor)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (row.Gap) return;
            if (row.File == null)
            {
                Color color = row.Group == 0 ? Theme.Red : row.Group == 1 ? Theme.Green : Theme.Blue;
                Rectangle bounds = e.Bounds;
                bounds.X += 2;
                bounds.Width -= 4;
                TextRenderer.DrawText(e.Graphics, row.Label, sectionFont, bounds, color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
            else
            {
                Rectangle[] columns = ColumnBounds(e.Bounds.Y, e.Bounds.Height);
                DrawCell(e.Graphics, row.File.Path, columns[0], Theme.Text, false);
                DrawCell(e.Graphics, row.File.DisplaySize, columns[1], Theme.Muted, true);
                DrawCell(e.Graphics, row.File.LineCount.HasValue ? row.File.LineCount.Value.ToString("N0") : "—", columns[2], Theme.Muted, true);
                if (row.File.AddedLines.HasValue && row.File.DeletedLines.HasValue)
                {
                    Rectangle added = columns[3];
                    added.Width /= 2;
                    Rectangle deleted = columns[3];
                    deleted.X += added.Width;
                    deleted.Width -= added.Width;
                    DrawCell(e.Graphics, row.File.AddedLines.Value.ToString("N0"), added, Theme.Green, true);
                    DrawCell(e.Graphics, row.File.DeletedLines.Value.ToString("N0"), deleted, Theme.Red, true);
                }
                else DrawCell(e.Graphics, "—", columns[3], Theme.Muted, true);
            }
            if (selected && Focused) e.DrawFocusRectangle();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int index = HitTestRow(e.Location);
            if (index >= 0)
            {
                Row row = (Row)Items[index];
                if (row.Gap) return;
                if (row.File == null && e.Button == MouseButtons.Left)
                {
                    Focus();
                    Toggle(row.Group);
                    return;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            Row row = SelectedItem as Row;
            if (row != null && row.File == null && !row.Gap && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                Toggle(row.Group);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void Toggle(int group)
        {
            if (!collapsed.Remove(group)) collapsed.Add(group);
            Rebuild();
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            if (rebuilding) return;
            Row row = SelectedItem as Row;
            if (row != null && row.Gap)
            {
                int next = SelectedIndex + (SelectedIndex < lastSelection ? -1 : 1);
                if (next >= 0 && next < Items.Count) SelectedIndex = next;
                return;
            }
            lastSelection = SelectedIndex;
            if (row != null && row.File != null) selectedPath = row.File.Path;
            base.OnSelectedIndexChanged(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTestRow(e.Location);
            if (index == hovered) return;
            hovered = index;
            Row row = index >= 0 ? (Row)Items[index] : null;
            tooltip.SetToolTip(this, row != null && row.File != null ? row.File.StatusLabel + ": " + row.File.DisplayPath : null);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
            base.OnMouseWheel(e);
            if (rebuilding || Items.Count == 0) return;
            wheelRemainder += e.Delta;
            int notches = wheelRemainder / 120;
            wheelRemainder %= 120;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (notches == 0 || lines == 0) return;
            if (lines < 0) lines = Math.Max(1, ClientSize.Height / Math.Max(20, Font.Height + 2));
            if (hovered >= 0) { hovered = -1; tooltip.SetToolTip(this, null); }
            // Set the final position once instead of animating native per-line scroll messages.
            TopIndex = (int)Math.Max(0L, Math.Min(Items.Count - 1L, TopIndex - (long)notches * lines));
        }

        private int HitTestRow(Point point)
        {
            if (rebuilding || Items.Count == 0 || !ClientRectangle.Contains(point)) return -1;
            int index = IndexFromPoint(point);
            // Native hit testing can return 65535 for no item or the nearest row in blank space.
            return index >= 0 && index < Items.Count && GetItemRectangle(index).Contains(point) ? index : -1;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = -1;
            tooltip.SetToolTip(this, null);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tooltip.Dispose(); sectionFont.Dispose(); }
            base.Dispose(disposing);
        }

        private sealed class FileListMinimap : Control
        {
            private readonly GroupedFileList owner;
            private int[] offsets = new int[0];
            private Bitmap strokes;
            private bool dragging;
            private float dragOffset;
            private int TotalHeight { get { return offsets.Length == 0 ? 0 : offsets[offsets.Length - 1]; } }
            private int MapTop { get { return MaxOffset == 0 ? 0 : 4; } }
            private int MapHeight { get { return Math.Max(1, MaxOffset == 0 ? Math.Min(Height, TotalHeight) : Height - 8); } }
            private int MaxOffset { get { return Math.Max(0, TotalHeight - owner.ClientSize.Height); } }

            public FileListMinimap(GroupedFileList owner)
            {
                this.owner = owner;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Panel;
                Cursor = Cursors.Hand;
                AccessibleName = "File list minimap";
                AccessibleDescription = "Click or drag to scroll the file list. Red is deleted, green added, blue modified.";
                TabStop = false;
            }

            public void RefreshRows()
            {
                dragging = false;
                Capture = false;
                offsets = new int[owner.Items.Count + 1];
                for (int i = 0; i < owner.Items.Count; i++) offsets[i + 1] = offsets[i] + owner.GetItemHeight(i);
                ClearStrokes();
            }

            private void ClearStrokes()
            {
                if (strokes != null) { strokes.Dispose(); strokes = null; }
                Invalidate();
            }

            protected override void OnResize(EventArgs e) { base.OnResize(e); ClearStrokes(); }

            private RectangleF Viewport()
            {
                int index = owner.Items.Count == 0 ? 0 : Math.Max(0, Math.Min(owner.TopIndex, offsets.Length - 1));
                int offset = offsets.Length == 0 ? 0 : Math.Min(MaxOffset, offsets[index]);
                float height = Math.Min(MapHeight, Math.Max(8, MapHeight * owner.ClientSize.Height / (float)Math.Max(1, TotalHeight)));
                float y = MaxOffset == 0 ? 0 : (MapHeight - height) * offset / MaxOffset;
                return new RectangleF(1, MapTop + y, Math.Max(1, Width - 2), height);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var border = new Pen(Theme.Border)) e.Graphics.DrawLine(border, 0, 0, 0, Height);
                if (owner.Items.Count == 0 || offsets.Length != owner.Items.Count + 1) return;
                if (strokes == null) BuildStrokes();
                e.Graphics.DrawImageUnscaled(strokes, 0, MapTop);
                if (MaxOffset == 0) return;
                RectangleF viewport = Viewport();
                using (var fill = new SolidBrush(Color.FromArgb(35, Theme.Blue))) e.Graphics.FillRectangle(fill, viewport);
                using (var border = new Pen(Color.FromArgb(150, Theme.Blue)))
                    e.Graphics.DrawRectangle(border, viewport.X, viewport.Y, viewport.Width - 1, Math.Max(1, viewport.Height - 1));
            }

            private void BuildStrokes()
            {
                strokes = new Bitmap(Math.Max(1, Width), MapHeight);
                using (Graphics graphics = Graphics.FromImage(strokes))
                using (var deleted = new SolidBrush(Color.FromArgb(102, Theme.Red)))
                using (var added = new SolidBrush(Color.FromArgb(102, Theme.Green)))
                using (var modified = new SolidBrush(Color.FromArgb(102, Theme.Blue)))
                {
                    graphics.Clear(BackColor);
                    float scale = MaxOffset == 0 ? 1 : MapHeight / (float)Math.Max(1, TotalHeight);
                    int previousBottom = -4;
                    for (int i = 0; i < owner.Items.Count; i++)
                    {
                        Row row = (Row)owner.Items[i];
                        if (row.Gap || row.File != null) continue;
                        // Span the files and their folder gaps, leaving the section header clear.
                        int end = i + 1;
                        while (end < owner.Items.Count)
                        {
                            Row next = (Row)owner.Items[end];
                            if (!next.Gap && next.File == null) break;
                            end++;
                        }
                        int contentEnd = end;
                        while (contentEnd > i + 1 && ((Row)owner.Items[contentEnd - 1]).Gap) contentEnd--;
                        int firstFile = i + 1;
                        if (firstFile == contentEnd) continue;
                        int y = Math.Min(MapHeight - 1, Math.Max(previousBottom + 4, (int)(offsets[firstFile] * scale)));
                        int bottom = Math.Min(MapHeight, (int)(offsets[contentEnd] * scale));
                        int blockHeight = Math.Max(1, bottom - y);
                        Brush color = row.Group == 0 ? deleted : row.Group == 1 ? added : modified;
                        int blockWidth = Math.Max(1, Width - 16);
                        graphics.FillRectangle(color, (Width - blockWidth) / 2f, y, blockWidth, blockHeight);
                        previousBottom = y + blockHeight;
                        i = end - 1;
                    }
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || !owner.Enabled || owner.Items.Count == 0 || MaxOffset == 0) return;
                owner.Focus();
                RectangleF viewport = Viewport();
                dragOffset = viewport.Contains(e.Location) ? e.Y - viewport.Y : viewport.Height / 2;
                dragging = true;
                Capture = true;
                ScrollFromMap(e.Y);
            }

            private void ScrollFromMap(int y)
            {
                float travel = MapHeight - Viewport().Height;
                if (travel <= 0 || MaxOffset == 0 || offsets.Length < 2) return;
                double fraction = Math.Max(0, Math.Min(1, (y - MapTop - dragOffset) / travel));
                int target = (int)Math.Round(fraction * MaxOffset);
                int index = Array.BinarySearch(offsets, target);
                if (index < 0) index = ~index - (fraction >= 1 ? 0 : 1);
                owner.TopIndex = Math.Max(0, Math.Min(owner.Items.Count - 1, index));
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) ScrollFromMap(e.Y); }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button == MouseButtons.Left) { dragging = false; Capture = false; }
            }
            protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) dragging = false; }
            protected override void OnMouseWheel(MouseEventArgs e) { owner.OnMouseWheel(e); Invalidate(); }
            protected override void Dispose(bool disposing) { if (disposing && strokes != null) strokes.Dispose(); base.Dispose(disposing); }
        }
    }
}
