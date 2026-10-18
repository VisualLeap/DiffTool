using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DiffTool
{
    internal sealed class GroupedFileList : Control
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
        private int hovered = -1;
        private bool draggingSelection;
        private readonly List<Row> Items = new List<Row>();
        private int[] rowOffsets = new int[1];
        private int selectedIndex = -1;
        private double scrollOffset;
        private string searchPrefix = "";
        private int searchTime;
        private int TotalHeight { get { return rowOffsets[rowOffsets.Length - 1]; } }
        private int MaxOffset { get { return Math.Max(0, TotalHeight - ClientSize.Height); } }
        private int FileRowHeight { get { return Math.Max(20, Font.Height + 2); } }
        private Row SelectedItem { get { return selectedIndex < 0 ? null : Items[selectedIndex]; } }
        private Control columnHeader;
        private int sortColumn = -1;
        private bool sortDescending;
        private FileListMinimap minimap;
        public event EventHandler SelectedIndexChanged;
        public int FileCount { get; private set; }
        public FileChange SelectedChange { get { var row = SelectedItem as Row; return row == null ? null : row.File; } }

        public GroupedFileList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
        }

        private int GetItemHeight(int index)
        {
            Row row = Items[index];
            return row.Gap ? Math.Max(4, Font.Height / 3) : row.File == null ? Math.Max(20, sectionFont.Height + 2) : FileRowHeight;
        }

        private Rectangle GetItemRectangle(int index)
        {
            return new Rectangle(0, rowOffsets[index] - (int)scrollOffset, ClientSize.Width, rowOffsets[index + 1] - rowOffsets[index]);
        }

        private void RefreshRows()
        {
            rowOffsets = new int[Items.Count + 1];
            for (int i = 0; i < Items.Count; i++) rowOffsets[i + 1] = rowOffsets[i] + GetItemHeight(i);
            scrollOffset = Math.Min(scrollOffset, MaxOffset);
            if (minimap != null) minimap.RefreshRows();
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (Items != null && sectionFont != null) RefreshRows();
            if (columnHeader != null) columnHeader.Invalidate();
        }

        private void ScrollTo(double value)
        {
            double next = Math.Max(0, Math.Min(MaxOffset, value));
            if (scrollOffset == next) return;
            scrollOffset = next;
            hovered = -1;
            tooltip.SetToolTip(this, null);
            Invalidate();
            if (minimap != null) minimap.Invalidate();
        }

        private void EnsureSelectionVisible()
        {
            if (selectedIndex < 0) return;
            if (rowOffsets[selectedIndex] < scrollOffset) ScrollTo(rowOffsets[selectedIndex]);
            else if (rowOffsets[selectedIndex + 1] > scrollOffset + ClientSize.Height)
                ScrollTo(rowOffsets[selectedIndex + 1] - ClientSize.Height);
        }

        private void SelectRow(int index)
        {
            if (index < 0 || index >= Items.Count || Items[index].Gap) return;
            bool changed = selectedIndex != index;
            selectedIndex = index;
            EnsureSelectionVisible();
            Invalidate();
            if (changed) OnSelectedIndexChanged(EventArgs.Empty);
        }

        public Control CreateMinimap()
        {
            minimap = new FileListMinimap(this) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            minimap.RefreshRows();
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
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping |
                (rightAligned ? TextFormatFlags.Right | TextFormatFlags.EndEllipsis : TextFormatFlags.PathEllipsis));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (rowOffsets != null) ScrollTo(scrollOffset);
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
            EndSelectionDrag();
            FileChange previousSelection = SelectedChange;
            rebuilding = true;
            Items.Clear();
            selectedIndex = -1;
            scrollOffset = 0;
            searchPrefix = "";
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
                    int index = Items.Count;
                    Items.Add(new Row { File = file, Group = group });
                    if (firstFile < 0) firstFile = index;
                    if (file.Path == selectedPath) selection = index;
                }
            }
            selectedIndex = selection >= 0 ? selection : firstFile;
            RefreshRows();
            EnsureSelectionVisible();
            if (columnHeader != null) columnHeader.Invalidate();
            rebuilding = false;
            if (notifySelection || !ReferenceEquals(previousSelection, SelectedChange)) OnSelectedIndexChanged(EventArgs.Empty);
        }

        private static int Group(FileChange file) { return file.Status == 'D' ? 0 : file.Status == 'A' || file.Status == 'C' ? 1 : 2; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (rebuilding || Items.Count == 0) return;
            int first = RowAtOffset((int)scrollOffset);
            for (int i = first; i < Items.Count; i++)
            {
                Rectangle bounds = GetItemRectangle(i);
                if (bounds.Top >= ClientSize.Height) break;
                if (!bounds.IntersectsWith(e.ClipRectangle)) continue;
                var state = e.Graphics.Save();
                e.Graphics.SetClip(bounds, System.Drawing.Drawing2D.CombineMode.Intersect);
                DrawItem(e.Graphics, i, bounds);
                e.Graphics.Restore(state);
            }
            // These are separate windows. During rapid input, another list paint
            // can win priority before the minimap's queued paint is dispatched.
            // Finish its pending paint with this frame, as DiffView does inline.
            if (minimap != null) minimap.Update();
        }

        private void DrawItem(Graphics graphics, int index, Rectangle rowBounds)
        {
            Row row = Items[index];
            bool selected = index == selectedIndex && !row.Gap;
            using (var brush = new SolidBrush(selected ? Theme.HunkBackground : BackColor)) graphics.FillRectangle(brush, rowBounds);
            if (row.Gap) return;
            if (row.File == null)
            {
                Color color = row.Group == 0 ? Theme.Red : row.Group == 1 ? Theme.Green : Theme.Blue;
                Rectangle bounds = rowBounds;
                bounds.X += 2;
                bounds.Width -= 4;
                TextRenderer.DrawText(graphics, row.Label, sectionFont, bounds, color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
            }
            else
            {
                Rectangle[] columns = ColumnBounds(rowBounds.Y, rowBounds.Height);
                DrawCell(graphics, row.File.Path, columns[0], Theme.Text, false);
                DrawCell(graphics, row.File.DisplaySize, columns[1], Theme.Muted, true);
                DrawCell(graphics, row.File.LineCount.HasValue ? row.File.LineCount.Value.ToString("N0") : "—", columns[2], Theme.Muted, true);
                if (row.File.AddedLines.HasValue && row.File.DeletedLines.HasValue)
                {
                    Rectangle added = columns[3];
                    added.Width /= 2;
                    Rectangle deleted = columns[3];
                    deleted.X += added.Width;
                    deleted.Width -= added.Width;
                    DrawCell(graphics, row.File.AddedLines.Value.ToString("N0"), added, Theme.Green, true);
                    DrawCell(graphics, row.File.DeletedLines.Value.ToString("N0"), deleted, Theme.Red, true);
                }
                else DrawCell(graphics, "—", columns[3], Theme.Muted, true);
            }
            if (selected && Focused) ControlPaint.DrawFocusRectangle(graphics, rowBounds, ForeColor, BackColor);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
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
                if (e.Button == MouseButtons.Left)
                {
                    draggingSelection = true;
                    Capture = true;
                    SelectRow(index);
                }
            }
            base.OnMouseDown(e);
        }

        private void EndSelectionDrag()
        {
            draggingSelection = false;
            Capture = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) EndSelectionDrag();
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) draggingSelection = false;
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) EndSelectionDrag();
            base.OnEnabledChanged(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            Row row = SelectedItem as Row;
            if (row != null && row.File == null && !row.Gap && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                Toggle(row.Group);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            int index = selectedIndex;
            int direction = 1;
            switch (e.KeyCode)
            {
                case Keys.Up: index--; direction = -1; break;
                case Keys.Down: index++; break;
                case Keys.Home: index = 0; break;
                case Keys.End: index = Items.Count - 1; direction = -1; break;
                case Keys.PageUp:
                case Keys.PageDown:
                    direction = e.KeyCode == Keys.PageUp ? -1 : 1;
                    int origin = selectedIndex < 0 ? 0 : rowOffsets[selectedIndex];
                    index = RowAtOffset(Math.Max(0, origin + direction * ClientSize.Height));
                    break;
                default: return;
            }
            while (index >= 0 && index < Items.Count && Items[index].Gap) index += direction;
            SelectRow(index);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled || char.IsControl(e.KeyChar) || Items.Count == 0) return;
            int now = Environment.TickCount;
            string letter = e.KeyChar.ToString();
            searchPrefix = unchecked(now - searchTime) > 1000 || searchPrefix == letter ? letter : searchPrefix + letter;
            searchTime = now;
            int start = Math.Max(0, selectedIndex + (searchPrefix.Length == 1 ? 1 : 0));
            for (int n = 0; n < Items.Count; n++)
            {
                int index = (start + n) % Items.Count;
                if (!Items[index].Gap && Items[index].ToString().StartsWith(searchPrefix, StringComparison.CurrentCultureIgnoreCase))
                {
                    SelectRow(index);
                    break;
                }
            }
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override AccessibleObject CreateAccessibilityInstance() { return new FileListAccessibleObject(this); }

        private sealed class FileListAccessibleObject : ControlAccessibleObject
        {
            private readonly GroupedFileList owner;
            public FileListAccessibleObject(GroupedFileList owner) : base(owner) { this.owner = owner; }
            public override int GetChildCount() { return owner.Items.Count; }
            public override AccessibleObject GetChild(int index)
            {
                return index < 0 || index >= owner.Items.Count ? null : new RowAccessibleObject(owner, owner.Items[index]);
            }
            public override AccessibleObject GetSelected() { return GetChild(owner.selectedIndex); }
            public override AccessibleObject GetFocused() { return owner.Focused ? GetSelected() : null; }
            public override AccessibleObject HitTest(int x, int y)
            {
                return GetChild(owner.HitTestRow(owner.PointToClient(new Point(x, y)))) ?? base.HitTest(x, y);
            }
        }

        private sealed class RowAccessibleObject : AccessibleObject
        {
            private readonly GroupedFileList owner;
            private readonly Row row;
            private int Index { get { return owner.Items.IndexOf(row); } }
            public RowAccessibleObject(GroupedFileList owner, Row row) { this.owner = owner; this.row = row; }
            public override string Name { get { return row.ToString(); } set { } }
            public override AccessibleRole Role { get { return row.Gap ? AccessibleRole.Separator : AccessibleRole.ListItem; } }
            public override AccessibleObject Parent { get { return owner.AccessibilityObject; } }
            public override Rectangle Bounds
            {
                get
                {
                    int index = Index;
                    if (index < 0 || !owner.Visible) return Rectangle.Empty;
                    Rectangle bounds = Rectangle.Intersect(owner.ClientRectangle, owner.GetItemRectangle(index));
                    return bounds.Width <= 0 || bounds.Height <= 0 ? Rectangle.Empty : owner.RectangleToScreen(bounds);
                }
            }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state = row.Gap ? AccessibleStates.None : AccessibleStates.Selectable | AccessibleStates.Focusable;
                    if (!owner.Enabled) state |= AccessibleStates.Unavailable;
                    if (Bounds.IsEmpty) state |= AccessibleStates.Offscreen;
                    if (Index >= 0 && Index == owner.selectedIndex)
                    {
                        state |= AccessibleStates.Selected;
                        if (owner.Focused) state |= AccessibleStates.Focused;
                    }
                    if (!row.Gap && row.File == null) state |= owner.collapsed.Contains(row.Group) && owner.filter.Length == 0 ? AccessibleStates.Collapsed : AccessibleStates.Expanded;
                    return state;
                }
            }
            public override string DefaultAction { get { return row.Gap ? "" : row.File == null ? "Toggle section" : "Select"; } }
            public override void DoDefaultAction()
            {
                if (!owner.Enabled || Index < 0 || row.Gap) return;
                owner.Focus();
                if (row.File == null) owner.Toggle(row.Group);
                else owner.SelectRow(Index);
            }
            public override void Select(AccessibleSelection flags)
            {
                if (!owner.Enabled || Index < 0 || row.Gap) return;
                if ((flags & AccessibleSelection.TakeFocus) != 0) owner.Focus();
                if ((flags & (AccessibleSelection.TakeSelection | AccessibleSelection.AddSelection)) != 0) owner.SelectRow(Index);
            }
        }

        private void Toggle(int group)
        {
            if (!collapsed.Remove(group)) collapsed.Add(group);
            Rebuild();
        }

        private void OnSelectedIndexChanged(EventArgs e)
        {
            if (rebuilding) return;
            Row row = SelectedItem as Row;
            if (row != null && row.File != null) selectedPath = row.File.Path;
            AccessibilityNotifyClients(AccessibleEvents.Selection, selectedIndex);
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTestRow(e.Location);
            if (draggingSelection && Capture &&
                index >= 0 && index != selectedIndex && Items[index].File != null)
            {
                SelectRow(index);
                // Selection can scroll the view or cause an event handler to rebuild it.
                index = HitTestRow(e.Location);
            }
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
            if (!Enabled || rebuilding || Items.Count == 0) return;
            ScrollTo(WheelScroll.GetOffset(scrollOffset, MaxOffset, e.Delta,
                SystemInformation.MouseWheelScrollLines, FileRowHeight, ClientSize.Height));
        }

        private int RowAtOffset(int offset)
        {
            int index = Array.BinarySearch(rowOffsets, offset);
            if (index < 0) index = ~index - 1;
            return Math.Max(0, Math.Min(Items.Count - 1, index));
        }

        private int HitTestRow(Point point)
        {
            if (rebuilding || Items.Count == 0 || !ClientRectangle.Contains(point)) return -1;
            int index = RowAtOffset(point.Y + (int)scrollOffset);
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
                offsets = owner.rowOffsets;
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
                double offset = owner.scrollOffset;
                float height = Math.Min(MapHeight, Math.Max(8, MapHeight * owner.ClientSize.Height / (float)Math.Max(1, TotalHeight)));
                float y = MaxOffset == 0 ? 0 : (float)((MapHeight - height) * offset / MaxOffset);
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
                MinimapViewport.Draw(e.Graphics, Viewport());
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
                owner.ScrollTo(fraction * MaxOffset);
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
