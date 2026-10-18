using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DiffTool;

internal static class Tests
{
    private static int assertions;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "DiffTool-tests-" + Guid.NewGuid().ToString("N"));

    [STAThread]
    private static int Main()
    {
        try
        {
            Directory.CreateDirectory(Root);
            string repoPath = Path.Combine(Root, "repository with spaces");
            Directory.CreateDirectory(repoPath);
            Git(repoPath, "init", "-b", "main");
            Git(repoPath, "config", "user.name", "Diff Viewer Tests");
            Git(repoPath, "config", "user.email", "tests@example.invalid");
            Git(repoPath, "config", "core.autocrlf", "false");
            var repo = new GitRepository(repoPath);
            Write(repoPath, "first.txt", "first\n");
            Check(repo.Changes(ChangeScope.WorkingTree).Single().Untracked, "unborn repository: untracked file");
            Git(repoPath, "add", "first.txt");
            Check(repo.Changes(ChangeScope.Staged).Single().Status == 'A', "unborn repository: staged file");
            Check(repo.Diff(repo.Changes(ChangeScope.Staged).Single(), ChangeScope.Staged).Contains("+first"), "unborn diff uses empty tree");
            Check(repo.Changes(ChangeScope.Unstaged).Count == 0, "unborn staged file has no unstaged diff");
            Write(repoPath, "src/review.cs", "using System;\n\nclass Review\n{\n    public string Title = \"Old title\";\n    public int Count = 1;\n}\n");
            Write(repoPath, "deleted.txt", "removed content\n");
            Write(repoPath, "rename.txt", "line one\nline two\nline three\nline four\n");
            Write(repoPath, "literal[1].txt", "before\n");
            Write(repoPath, "literal1.txt", "unrelated\n");
            Write(repoPath, "café space.txt", "old unicode\n");
            Write(repoPath, "-option.txt", "old option\n");
            Write(repoPath, ".gitignore", "ignored.log\n");
            Git(repoPath, "add", ".");
            Git(repoPath, "commit", "-m", "Initial fixture");
            Check(repo.Changes(ChangeScope.WorkingTree).Count == 0, "clean working tree");
            Check(repo.Branch() == "main", "branch name");
            Check(new GitRepository(Path.Combine(repoPath, "src")).Root == repo.Root, "resolve repository from child directory");

            Write(repoPath, "src/review.cs", "using System;\n\nclass Review\n{\n    public string Title = \"Review changes\";\n    public int Count = 2;\n    public bool ShowLineNumbers = true;\n}\n");
            Git(repoPath, "add", "src/review.cs");
            File.AppendAllText(Path.Combine(repoPath, "src/review.cs"), "// unstaged addition\n");
            File.Delete(Path.Combine(repoPath, "deleted.txt"));
            Git(repoPath, "mv", "rename.txt", "renamed.txt");
            Write(repoPath, "literal[1].txt", "after\n");
            Write(repoPath, "café space.txt", "new unicode\n");
            Write(repoPath, "-option.txt", "new option\n");
            Write(repoPath, "new-file.txt", "new content\nsecond line\n");
            Write(repoPath, "no-newline.txt", "no newline");
            Write(repoPath, "empty.txt", "");
            Write(repoPath, "ignored.log", "ignored\n");
            File.WriteAllBytes(Path.Combine(repoPath, "image.bin"), new byte[] { 0, 1, 2, 3 });
            var all = repo.Changes(ChangeScope.WorkingTree);
            Check(all.Single(c => c.Path == "deleted.txt").Status == 'D', "deleted file is listed");
            Check(all.Single(c => c.Path == "src/review.cs").Status == 'M', "modified file is listed");
            Check(all.Single(c => c.Path == "new-file.txt").Status == 'A', "untracked file is listed as added");
            Check(!all.Any(c => c.Path == "ignored.log"), "ignored files excluded");
            FileChange renamed = all.Single(c => c.Path == "renamed.txt");
            Check(renamed.Status == 'R' && renamed.OldPath == "rename.txt", "rename detection");
            Check(repo.Diff(renamed, ChangeScope.WorkingTree).Contains("rename from"), "rename metadata shown");
            var staged = repo.Changes(ChangeScope.Staged);
            var unstaged = repo.Changes(ChangeScope.Unstaged);
            Check(staged.Count == 2, "staged view contains only staged files");
            Check(!unstaged.Any(c => c.Path == "renamed.txt"), "staged rename excluded from unstaged view");
            string stagedDiff = repo.Diff(staged.Single(c => c.Path == "src/review.cs"), ChangeScope.Staged);
            string unstagedDiff = repo.Diff(unstaged.Single(c => c.Path == "src/review.cs"), ChangeScope.Unstaged);
            Check(stagedDiff.Contains("-    public string Title") && !stagedDiff.Contains("unstaged addition"), "staged diff reads index");
            Check(unstagedDiff.Contains("+// unstaged addition") && !unstagedDiff.Contains("Old title"), "unstaged diff compares index");
            string literalDiff = repo.Diff(all.Single(c => c.Path == "literal[1].txt"), ChangeScope.WorkingTree);
            Check(literalDiff.Contains("+after") && !literalDiff.Contains("unrelated"), "literal pathspec handles brackets");
            Check(repo.Diff(all.Single(c => c.Path == "café space.txt"), ChangeScope.WorkingTree).Contains("+new unicode"), "UTF-8 paths and spaces");
            Check(repo.Diff(all.Single(c => c.Path == "-option.txt"), ChangeScope.WorkingTree).Contains("+new option"), "leading dash in filename");
            Check(repo.Diff(all.Single(c => c.Path == "image.bin"), ChangeScope.WorkingTree).StartsWith("Binary"), "binary preview");
            Check(repo.Diff(all.Single(c => c.Path == "empty.txt"), ChangeScope.WorkingTree).StartsWith("Empty"), "empty preview");
            Check(repo.Diff(all.Single(c => c.Path == "no-newline.txt"), ChangeScope.WorkingTree).Contains("No newline at end of file"), "missing newline marker");
            var addedRows = DiffParser.Parse(repo.Diff(all.Single(c => c.Path == "new-file.txt"), ChangeScope.WorkingTree));
            Check(addedRows.Count(r => r.Kind == '+') == 2, "new-file headers not counted as additions");
            Check(addedRows.First(r => r.Kind == '+').NewNumber == 1, "new file line numbers start at one");
            var rows = DiffParser.Parse("--- a/x\n+++ b/x\n@@ -10,2 +20,3 @@\n context\n-old\n+new\n+++code\n\\ No newline at end of file\n");
            Check(rows[3].OldNumber == 10 && rows[3].NewNumber == 20, "context has old and new line numbers");
            Check(rows[4].OldNumber == 11 && rows[4].NewNumber == null, "deletion has only old line number");
            Check(rows[5].NewNumber == 21 && rows[5].OldNumber == null, "addition has only new line number");
            Check(rows[6].Kind == '+' && rows[6].NewNumber == 22, "code starting with pluses stays an addition");
            Git(repoPath, "checkout", "--detach");
            Check(repo.Branch().StartsWith("Detached at"), "detached HEAD is supported");
            Git(repoPath, "checkout", "main");
            SmokeTest(repoPath);
            Console.WriteLine("PASS: " + assertions + " assertions, including GUI smoke test.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            // Only remove the uniquely named test fixture rooted in the system temp directory.
            if (Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(Root))
            {
                foreach (string file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
        }
    }

    private static void SmokeTest(string repoPath)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        DiffWheelTest();
        FileListScrollTest();
        FileListDragTest();
        SharedWheelTest();
        MinimapRenderingTest();
        MinimapPaintSchedulingTest();
        using (var form = new DiffForm(repoPath, new RecentRepositories(Path.Combine(Root, "recent-repositories.txt"))))
        using (var timer = new Timer { Interval = 150 })
        {
            Exception failure = null;
            Stopwatch timeout = Stopwatch.StartNew();
            bool started = false;
            form.Shown += delegate { started = true; timer.Start(); };
            timer.Tick += delegate
            {
                if (!started) return;
                if (!form.IsIdle && timeout.Elapsed.TotalSeconds < 40) return;
                timer.Stop();
                try
                {
                    Check(form.IsIdle, "GUI finishes loading without blocking");
                    Check(form.VisibleFileCount >= 8, "GUI displays changed files");
                    Check(form.DisplayedDiff.Contains("-removed content"), "GUI loads selected file diff");
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke-test.png"));
                    }
                }
                catch (Exception error) { failure = error; }
                form.Close();
            };
            Application.Run(form);
            if (failure != null) throw failure;
        }
    }

    private static void DiffWheelTest()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo wheel = typeof(DiffView).GetMethod("OnMouseWheel", hidden);
        FieldInfo topLine = typeof(DiffView).GetField("topLine", hidden);
        FieldInfo rowHeight = typeof(DiffView).GetField("rowHeight", hidden);
        MethodInfo keyDown = typeof(DiffView).GetMethod("OnKeyDown", hidden);
        string diff = "@@ -1,200 +1,200 @@\n" + string.Concat(Enumerable.Repeat(" context\n", 200));
        using (var view = new DiffView { Size = new Size(600, 300) })
        {
            view.SetDiff(diff);
            int height = (int)rowHeight.GetValue(view);
            double lines = SystemInformation.MouseWheelScrollLines;
            if (lines < 0) lines = view.Height / (double)height;
            double maxTop = DiffParser.Parse(diff).Count - view.Height / (double)height;
            double step = Math.Min(maxTop, lines);
            Func<double, bool> at = delegate(double expected) { return Math.Abs((double)topLine.GetValue(view) - expected) < 0.000001; };
            Action<int, bool> scroll = delegate(int delta, bool overMap)
            {
                var args = new HandledMouseEventArgs(MouseButtons.None, 0, overMap ? view.Width - 10 : 150, 50, delta);
                wheel.Invoke(view, new object[] { args });
                if (!args.Handled) throw new Exception("Wheel input must be consumed by the diff pane.");
            };

            using (var before = new Bitmap(view.Width, view.Height))
            using (var after = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(before, view.ClientRectangle);
                scroll(-30, true);
                Check(at(Math.Min(maxTop, lines / 4.0)), "minimap responds to the first partial wheel notch");
                view.DrawToBitmap(after, view.ClientRectangle);
                int offset = (int)((double)topLine.GetValue(view) * height);
                bool shifted = true;
                // Compare a strip through the hunk background and line-number gutter.
                for (int y = 0; y < view.Height - offset; y++)
                    for (int x = 5; x < 110; x++)
                        shifted &= after.GetPixel(x, y) == before.GetPixel(x, y + offset);
                Check(shifted, "fractional scrolling paints the text and gutter at the pixel offset");
            }
            scroll(-30, true);
            scroll(-30, true);
            Check(at(Math.Min(maxTop, lines * 0.75)), "successive trackpad events move proportionally");
            scroll(-30, true);
            Check(at(step), "partial trackpad deltas preserve Windows scroll distance");
            for (int i = 0; i < 4; i++) scroll(30, true);
            Check(at(0), "small upward trackpad deltas scroll back");

            scroll(-150, false);
            Check(at(Math.Min(maxTop, lines * 1.25)), "diff text applies full and partial notches immediately");
            scroll(-90, false);
            Check(at(Math.Min(maxTop, 2.0 * lines)), "diff text preserves fractional movement between wheel events");

            view.SetDiff(diff);
            for (int i = 0; i < 120; i++) scroll(-1, true);
            Check(at(step), "subpixel input accumulates without losing scroll distance");
            view.SetDiff(diff);
            scroll(-60, true);
            scroll(60, true);
            Check(at(0), "opposite partial wheel deltas cancel");
            scroll(-30, true);
            view.SetDiff(diff);
            Check(at(0), "switching files clears fractional scroll position");
            scroll(-60, true);
            Check(at(Math.Min(maxTop, lines / 2.0)), "new file starts with its own scroll movement");

            scroll(-120 * 1000, true);
            Check(at(lines == 0 ? 0 : maxTop), "wheel scrolling clamps at end of diff");
            scroll(30, true);
            Check(at(lines == 0 ? 0 : Math.Max(0, maxTop - lines / 4.0)), "scrolling reverses immediately at bottom boundary");
            scroll(120 * 1000, true);
            Check(at(0), "wheel scrolling clamps at start of diff");
            keyDown.Invoke(view, new object[] { new KeyEventArgs(Keys.Down) });
            Check(at(1), "arrow keys still scroll one line");
            keyDown.Invoke(view, new object[] { new KeyEventArgs(Keys.End) });
            Check(at(maxTop), "End aligns the final row with the bottom of the viewport");
            view.Height += height;
            maxTop -= 1;
            Check(at(maxTop), "resizing clamps fractional scroll position");
            view.SetDiff("");
            scroll(-60, true);
            Check(at(0), "empty diff ignores wheel movement");
        }
    }

    private static void FileListScrollTest()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(DiffView).Assembly.GetType("DiffTool.GroupedFileList");
        var files = Enumerable.Range(0, 90).Select(i => new FileChange
        {
            Path = "folder" + (i / 10) + "/file" + i.ToString("D2") + ".txt",
            Status = i < 30 ? 'D' : i < 60 ? 'A' : 'M'
        }).ToList();
        using (var list = (Control)Activator.CreateInstance(type, true))
        using (var map = (Control)type.GetMethod("CreateMinimap").Invoke(list, null))
        {
            list.Size = new Size(550, 240);
            map.Size = new Size(50, list.Height);
            Action<string, string> setFiles = delegate(string filter, string selected)
            {
                type.GetMethod("SetFiles").Invoke(list, new object[] { files, filter, selected });
            };
            Func<double> offset = delegate { return (double)type.GetField("scrollOffset", hidden).GetValue(list); };
            Func<FileChange> selectedFile = delegate { return (FileChange)type.GetProperty("SelectedChange").GetValue(list, null); };
            Func<int[]> offsets = delegate { return (int[])type.GetField("rowOffsets", hidden).GetValue(list); };
            Action<double> scrollTo = delegate(double value) { type.GetMethod("ScrollTo", hidden).Invoke(list, new object[] { value }); };
            Action<Keys> key = delegate(Keys value) { type.GetMethod("OnKeyDown", hidden).Invoke(list, new object[] { new KeyEventArgs(value) }); };
            Action<Control, int> wheel = delegate(Control target, int delta)
            {
                var args = new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, delta);
                target.GetType().GetMethod("OnMouseWheel", hidden).Invoke(target, new object[] { args });
                if (!args.Handled) throw new Exception("File list must consume wheel input.");
            };
            Action<Control, string, int> mouse = delegate(Control target, string method, int y)
            {
                target.GetType().GetMethod(method, hidden).Invoke(target, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 10, y, 0) });
            };
            int selections = 0;
            type.GetEvent("SelectedIndexChanged").AddEventHandler(list, new EventHandler(delegate { selections++; }));
            setFiles("", files[0].Path);
            int lines = SystemInformation.MouseWheelScrollLines;
            double distance = lines < 0 ? list.Height : lines * Math.Max(20, list.Font.Height + 2);
            double max = offsets().Last() - list.Height;
            using (var diffView = new DiffView { Size = list.Size })
            {
                diffView.SetDiff("@@ -1,200 +1,200 @@\n" + string.Concat(Enumerable.Repeat(" context\n", 200)));
                int diffRowHeight = (int)typeof(DiffView).GetField("rowHeight", hidden).GetValue(diffView);
                double listUnit = lines < 0 ? list.Height : Math.Max(20, list.Font.Height + 2);
                double diffUnit = lines < 0 ? diffView.Height : diffRowHeight;
                bool same = true;
                double expectedList = 0, expectedDiff = 0;
                double maxDiff = 201 * diffRowHeight - diffView.Height;
                foreach (int delta in new[] { -1, -15, -30, -120, 30, 15, 1, 120 })
                {
                    wheel(map, delta);
                    typeof(DiffView).GetMethod("OnMouseWheel", hidden).Invoke(diffView,
                        new object[] { new HandledMouseEventArgs(MouseButtons.None, 0, diffView.Width - 10, 50, delta) });
                    double movement = -delta / 120.0 * (lines < 0 ? 1 : lines);
                    expectedList = Math.Max(0, Math.Min(max, expectedList + movement * listUnit));
                    expectedDiff = Math.Max(0, Math.Min(maxDiff, expectedDiff + movement * diffUnit));
                    double diffPixels = (double)typeof(DiffView).GetField("topLine", hidden).GetValue(diffView) * diffRowHeight;
                    same &= Math.Abs(offset() - expectedList) < 0.000001 && Math.Abs(diffPixels - expectedDiff) < 0.000001;
                }
                Check(same, "both minimaps apply tiny, coarse, and reversed input immediately with the same scroll behavior");
            }
            setFiles("", files[0].Path);
            selections = 1;
            using (var before = new Bitmap(list.Width, list.Height))
            using (var after = new Bitmap(list.Width, list.Height))
            {
                list.DrawToBitmap(before, list.ClientRectangle);
                wheel(list, -30);
                Check(Math.Abs(offset() - Math.Min(max, distance / 4)) < 0.000001, "file list preserves partial trackpad scroll distance");
                list.DrawToBitmap(after, list.ClientRectangle);
                int shift = (int)offset();
                bool shifted = true;
                for (int y = 0; y < list.Height - shift; y++)
                    for (int x = 3; x < list.Width - 3; x++)
                        shifted &= after.GetPixel(x, y) == before.GetPixel(x, y + shift);
                Check(shifted, "file-list text, columns, and selection paint at the pixel offset");
            }
            wheel(map, -30);
            Check(Math.Abs(offset() - Math.Min(max, distance / 2)) < 0.000001, "file-list minimap shares proportional wheel scrolling");
            Check(selectedFile() == files[0] && selections == 1, "wheel scrolling does not change file selection");
            scrollTo(0);
            for (int i = 0; i < 120; i++) wheel(list, -1);
            Check(Math.Abs(offset() - Math.Min(max, distance)) < 0.000001, "file list retains subpixel movement across tiny events");

            scrollTo(0);
            MethodInfo viewportMethod = map.GetType().GetMethod("Viewport", hidden);
            RectangleF viewport = (RectangleF)viewportMethod.Invoke(map, null);
            int grabY = (int)(viewport.Top + viewport.Height / 2);
            mouse(map, "OnMouseDown", grabY);
            double previousOffset = offset();
            bool continuousDrag = true;
            for (int pixel = 1; pixel <= 10; pixel++)
            {
                mouse(map, "OnMouseMove", grabY + pixel);
                RectangleF moved = (RectangleF)viewportMethod.Invoke(map, null);
                continuousDrag &= offset() > previousOffset && Math.Abs(moved.Top - viewport.Top - pixel) < 0.001;
                previousOffset = offset();
            }
            mouse(map, "OnMouseUp", grabY + 10);
            Check(continuousDrag, "file-list minimap viewport follows every pixel of a drag without row snapping");

            mouse(map, "OnMouseDown", map.Height - 1);
            mouse(map, "OnMouseUp", map.Height - 1);
            Check(Math.Abs(offset() - max) < 0.000001, "dragging file-list minimap reaches exact bottom");
            wheel(map, 30);
            Check(Math.Abs(offset() - Math.Max(0, max - distance / 4)) < 0.000001, "file list reverses immediately from bottom boundary");
            key(Keys.Down);
            Check(selectedFile() == files[1] && offset() <= offsets()[2], "keyboard selection returns scrolled-away file to view");

            scrollTo(offsets()[3] + 7.5);
            mouse(list, "OnMouseDown", 3);
            Check(selectedFile() == files[2], "clicking a partially visible row selects the correct file");
            AccessibleObject accessible = list.AccessibilityObject;
            Check(accessible.GetSelected().Name == files[2].DisplayPath, "file-list accessibility exposes selected file");
            int gap = Enumerable.Range(0, accessible.GetChildCount()).First(i => accessible.GetChild(i).Role == AccessibleRole.Separator);
            scrollTo(offsets()[gap] - 20);
            int selectionCount = selections;
            mouse(list, "OnMouseDown", 21);
            Check(selections == selectionCount, "clicking a folder gap leaves selection unchanged");
            accessible.GetChild(gap - 1).Select(AccessibleSelection.TakeSelection);
            key(Keys.Down);
            Check(accessible.GetSelected().Name == accessible.GetChild(gap + 1).Name, "Down skips folder gaps");
            key(Keys.Up);
            Check(accessible.GetSelected().Name == accessible.GetChild(gap - 1).Name, "Up skips folder gaps");
            key(Keys.End);
            Check(selectedFile() == files.Last() && Math.Abs(offset() - max) < 0.000001, "End selects and reveals last file");
            key(Keys.PageUp);
            Check(selectedFile() != files.Last() && offset() < max, "Page Up navigates by visible page height");
            key(Keys.Home);
            int expandedRows = accessible.GetChildCount();
            key(Keys.Enter);
            Check(accessible.GetChildCount() < expandedRows && (accessible.GetChild(0).State & AccessibleStates.Collapsed) != 0, "keyboard collapses file section");
            key(Keys.Home);
            key(Keys.Space);
            Check(accessible.GetChildCount() == expandedRows, "keyboard expands file section");

            setFiles("", files[50].Path);
            type.GetMethod("SortByColumn", hidden).Invoke(list, new object[] { 0 });
            type.GetMethod("SortByColumn", hidden).Invoke(list, new object[] { 0 });
            Check(selectedFile() == files[50], "sorting preserves selected file");
            setFiles(files[50].Path, files[50].Path);
            Check((int)type.GetProperty("FileCount").GetValue(list, null) == 1 && selectedFile() == files[50] && offset() == 0, "filtering preserves matching selection and clamps scrolling");
            setFiles("", files[0].Path);
            scrollTo(double.MaxValue);
            list.Height = offsets().Last() + 100;
            Check(offset() == 0, "resizing file list clamps pixel scroll position");
            selectionCount = selections;
            mouse(list, "OnMouseDown", list.Height - 1);
            Check(selections == selectionCount, "clicking below final row leaves selection unchanged");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static void FileListDragTest()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(DiffView).Assembly.GetType("DiffTool.GroupedFileList");
        var files = new[] {
            new FileChange { Path = "a/alpha.txt", Status = 'D' },
            new FileChange { Path = "a/beta.txt", Status = 'D' },
            new FileChange { Path = "b/gamma.txt", Status = 'D' }
        }.ToList();
        using (var form = new Form { ClientSize = new Size(550, 300), ShowInTaskbar = false })
        using (var list = (Control)Activator.CreateInstance(type, true))
        {
            list.Dock = DockStyle.Fill;
            form.Controls.Add(list);
            Action reset = delegate { type.GetMethod("SetFiles").Invoke(list, new object[] { files, "", files[0].Path }); };
            Func<FileChange> selected = delegate { return (FileChange)type.GetProperty("SelectedChange").GetValue(list, null); };
            Func<int, int> rowY = delegate(int index)
            {
                return ((Rectangle)type.GetMethod("GetItemRectangle", hidden).Invoke(list, new object[] { index })).Top + 2;
            };
            Action<int, int, int> mouse = delegate(int message, int buttons, int y)
            {
                SendMessage(list.Handle, message, new IntPtr(buttons), new IntPtr(unchecked((y << 16) | 10)));
            };
            reset();
            form.Show();
            Application.DoEvents();
            int changes = 0;
            type.GetEvent("SelectedIndexChanged").AddEventHandler(list, new EventHandler(delegate { changes++; }));
            mouse(0x200, 1, rowY(2)); // A held button entering the list is not a list drag.
            Check(selected() == files[0], "drag selection requires a press on a file row");
            mouse(0x201, 1, rowY(1));
            Check(list.Capture, "file-row drag captures the mouse");
            mouse(0x200, 1, rowY(2));
            Check(selected() == files[1] && changes == 1, "holding left button and dragging selects the next file");
            mouse(0x200, 1, rowY(2));
            Check(changes == 1, "dragging within the same row does not reload the diff");
            mouse(0x200, 1, rowY(3)); // Folder gap.
            mouse(0x200, 1, rowY(0)); // Section header.
            Check(selected() == files[1] && changes == 1, "dragging over gaps and headers preserves file selection");
            mouse(0x200, 1, rowY(4));
            Check(selected() == files[2], "drag selection continues across folder gaps");
            mouse(0x200, 1, -10);
            mouse(0x200, 1, rowY(2));
            Check(selected() == files[1], "captured drag resumes when the pointer returns to the list");
            mouse(0x202, 0, -10);
            mouse(0x200, 1, rowY(4));
            Check(!list.Capture && selected() == files[1], "releasing outside the list ends drag selection");
            mouse(0x204, 2, rowY(1));
            mouse(0x200, 2, rowY(4));
            mouse(0x205, 0, rowY(4));
            Check(selected() == files[1], "right-button dragging does not change selection");
            mouse(0x201, 1, rowY(1));
            list.Capture = false;
            mouse(0x200, 1, rowY(2));
            Check(selected() == files[0], "losing mouse capture cancels drag selection");
            mouse(0x201, 1, rowY(1));
            reset();
            mouse(0x200, 1, rowY(2));
            Check(!list.Capture && selected() == files[0], "rebuilding the list cancels drag selection");
            mouse(0x201, 1, rowY(1));
            list.Enabled = false;
            list.Enabled = true;
            mouse(0x200, 1, rowY(2));
            Check(!list.Capture && selected() == files[0], "disabling the list cancels drag selection");
            form.Close();
        }
    }

    private static void SharedWheelTest()
    {
        MethodInfo method = typeof(DiffView).Assembly.GetType("DiffTool.WheelScroll").GetMethod("GetOffset");
        Func<double, int, int, double> scroll = delegate(double offset, int delta, int lines)
        {
            return (double)method.Invoke(null, new object[] { offset, 1000.0, delta, lines, 20.0, 241.0 });
        };
        Check(scroll(0, -1, 3) == 0.5, "shared scrolling retains subpixel input immediately");
        Check(scroll(0, -120, 3) == 60, "shared scrolling follows Windows line count");
        Check(scroll(0, -120, -1) == 241, "shared page scrolling uses exact viewport height");
        Check(scroll(0, -30, -1) == 60.25, "shared page scrolling preserves partial wheel input");
        Check(scroll(12.5, -120, 0) == 12.5, "disabled wheel scrolling preserves position");
        Check(scroll(999, -120, 3) == 1000 && scroll(1, 120, 3) == 0, "shared scrolling clamps at both boundaries");
        Check(scroll(1000, 1, 3) == 999.5, "shared scrolling reverses immediately at a boundary");
    }

    private static void MinimapRenderingTest()
    {
        Type listType = typeof(DiffView).Assembly.GetType("DiffTool.GroupedFileList");
        using (var list = (Control)Activator.CreateInstance(listType, true))
        using (var map = (Control)listType.GetMethod("CreateMinimap").Invoke(list, null))
        using (var diff = new DiffView())
        {
            list.Size = diff.Size = new Size(550, 240);
            map.Size = new Size(50, 240);
            var files = Enumerable.Range(0, 90).Select(i => new FileChange { Path = "file" + i.ToString("D2"), Status = 'M' }).ToList();
            listType.GetMethod("SetFiles").Invoke(list, new object[] { files, "", files[0].Path });
            diff.SetDiff("@@ -1,200 +1,200 @@\n" + string.Concat(Enumerable.Repeat(" context\n", 200)));
            CheckMinimapFractionalRendering(map, list, "Viewport", 0, "left");
            CheckMinimapFractionalRendering(diff, diff, "MapViewport", diff.Width - 82, "right");
        }
        using (var bitmap = new Bitmap(50, 50))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.SetClip(new Rectangle(2, 2, 40, 40));
            RectangleF clip = graphics.ClipBounds;
            typeof(DiffView).Assembly.GetType("DiffTool.MinimapViewport").GetMethod("Draw").Invoke(null,
                new object[] { graphics, new RectangleF(1, 4.2f, 48, 15.5f) });
            Check(graphics.SmoothingMode == System.Drawing.Drawing2D.SmoothingMode.AntiAlias &&
                graphics.PixelOffsetMode == System.Drawing.Drawing2D.PixelOffsetMode.HighQuality && graphics.ClipBounds == clip,
                "minimap outline painting preserves surrounding graphics settings");
        }
    }

    private static void CheckMinimapFractionalRendering(Control surface, Control owner, string viewportName, int left, string label)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo scroll = owner.GetType().GetMethod("ScrollTo", hidden);
        MethodInfo viewport = surface.GetType().GetMethod(viewportName, hidden);
        scroll.Invoke(owner, new object[] { 0.0 });
        float start = ((RectangleF)viewport.Invoke(surface, null)).Top;
        scroll.Invoke(owner, new object[] { 100.0 });
        double scale = (((RectangleF)viewport.Invoke(surface, null)).Top - start) / 100;
        int[] previous = null;
        bool positionsMatch = true, everyFrameChanges = true;
        for (int i = 0; i <= 10; i++)
        {
            scroll.Invoke(owner, new object[] { i / 10.0 / scale });
            positionsMatch &= Math.Abs(((RectangleF)viewport.Invoke(surface, null)).Top - start - i / 10.0) < 0.001;
            using (var bitmap = new Bitmap(surface.Width, surface.Height))
            {
                surface.DrawToBitmap(bitmap, surface.ClientRectangle);
                // Compare actual pixels along the upper edge, not just the stored rectangle.
                var pixels = new int[8 * 40];
                int index = 0;
                for (int y = 2; y < 10; y++)
                    for (int x = left + 4; x < left + 44; x++)
                        pixels[index++] = bitmap.GetPixel(x, y).ToArgb();
                if (previous != null) everyFrameChanges &= !pixels.SequenceEqual(previous);
                previous = pixels;
            }
        }
        Check(positionsMatch, label + " minimap rectangle retains tenth-pixel positions");
        Check(everyFrameChanges, label + " minimap outline visibly changes at every tenth-pixel step");
    }

    private static void MinimapPaintSchedulingTest()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        Type listType = typeof(DiffView).Assembly.GetType("DiffTool.GroupedFileList");
        using (var form = new Form { ClientSize = new Size(1100, 300), ShowInTaskbar = false })
        using (var list = (Control)Activator.CreateInstance(listType, true))
        using (var map = (Control)listType.GetMethod("CreateMinimap").Invoke(list, null))
        using (var diff = new DiffView())
        {
            list.SetBounds(0, 0, 500, 300);
            map.Dock = DockStyle.None;
            map.SetBounds(500, 0, 50, 300);
            diff.SetBounds(550, 0, 550, 300);
            form.Controls.AddRange(new Control[] { list, map, diff });
            var files = Enumerable.Range(0, 300).Select(i => new FileChange { Path = "file" + i.ToString("D3"), Status = 'M' }).ToList();
            listType.GetMethod("SetFiles").Invoke(list, new object[] { files, "", files[0].Path });
            diff.SetDiff("@@ -1,300 +1,300 @@\n" + string.Concat(Enumerable.Repeat(" context\n", 300)));
            form.Show();
            Application.DoEvents();
            int listPaints = 0, mapPaints = 0, diffPaints = 0;
            list.Paint += delegate { listPaints++; };
            map.Paint += delegate { mapPaints++; };
            diff.Paint += delegate { diffPaints++; };
            MethodInfo listScroll = listType.GetMethod("ScrollTo", hidden);
            MethodInfo diffScroll = typeof(DiffView).GetMethod("ScrollTo", hidden);
            for (int i = 1; i <= 16; i++)
            {
                listScroll.Invoke(list, new object[] { i * 5.5 });
                diffScroll.Invoke(diff, new object[] { i * 0.25 });
                // Simulate the list's paint winning dispatch priority repeatedly.
                // Do not pump queued messages or explicitly paint the left minimap.
                list.Update();
                diff.Update();
            }
            Check(listPaints == 16 && mapPaints == listPaints,
                "left minimap paints with every list frame even when its queued paint cannot run");
            Check(diffPaints == listPaints, "both panes repaint their minimap once per content frame");
            form.Close();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        assertions++;
        Console.WriteLine("PASS: " + message);
    }

    private static void Write(string repo, string path, string text)
    {
        string full = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static void Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git", string.Join(" ", args.Select(GitRepository.QuoteArgument)))
        { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Fixture git failed: " + output.Result + error.Result);
        }
    }
}
