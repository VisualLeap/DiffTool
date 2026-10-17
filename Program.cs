using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("DiffTool")]
[assembly: System.Reflection.AssemblyProduct("DiffTool")]

namespace DiffTool
{
    public static class Theme
    {
        public static readonly Color Canvas = Color.FromArgb(18, 22, 30);
        public static readonly Color Panel = Color.FromArgb(25, 30, 40);
        public static readonly Color Border = Color.FromArgb(44, 52, 66);
        public static readonly Color Text = Color.FromArgb(225, 231, 240);
        public static readonly Color Muted = Color.FromArgb(141, 155, 177);
        public static readonly Color Blue = Color.FromArgb(136, 181, 255);
        public static readonly Color Green = Color.FromArgb(132, 225, 166);
        public static readonly Color Red = Color.FromArgb(255, 155, 162);
        public static readonly Color AddBackground = Color.FromArgb(23, 54, 40);
        public static readonly Color DeleteBackground = Color.FromArgb(63, 31, 39);
        public static readonly Color HunkBackground = Color.FromArgb(29, 42, 63);
    }

    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [STAThread]
        private static void Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DiffForm(args.Length > 0 ? args[0] : null));
        }
    }

    public sealed class DiffForm : Form
    {
        private readonly Icon appIcon;
        private readonly Label repositoryLabel = new Label();
        private readonly ComboBox branchBox = new ComboBox();
        private bool updatingBranches;
        private readonly Label statusLabel = new Label();
        private readonly Label countLabel = new Label();
        private readonly Label fileLabel = new Label();
        private readonly Label lineStats = new Label();
        private readonly ComboBox scopeBox = new ComboBox();
        private readonly Button olderButton;
        private bool updatingCommits;
        private int historyVersion;
        private string loadedCommit;
        private readonly TextBox filterBox = new TextBox();
        private readonly GroupedFileList fileList = new GroupedFileList();
        private readonly DiffView diffView = new DiffView();
        private readonly Button refreshButton;
        private readonly Button copyButton;
        private readonly CheckBox fullFileToggle = new CheckBox();
        private readonly ToolTip tooltip = new ToolTip();
        private readonly RecentRepositories recentRepositories;
        private readonly ContextMenuStrip recentMenu = new ContextMenuStrip();
        private readonly Button recentButton;
        private GitRepository repository;
        private List<FileChange> changes = new List<FileChange>();
        private int refreshVersion;
        private int diffVersion;
        private int openVersion;
        private bool updatingList;
        private bool refreshing;
        private ChangeScope loadedScope;
        public bool IsIdle { get; private set; }
        public int VisibleFileCount { get { return fileList.FileCount; } }
        private FileChange SelectedChange { get { return fileList.SelectedChange; } }
        public string DisplayedDiff { get { return diffView.RawDiff; } }

        public DiffForm(string initialPath) : this(initialPath, new RecentRepositories()) { }

        public DiffForm(string initialPath, RecentRepositories recentRepositories)
        {
            this.recentRepositories = recentRepositories;
            using (var stream = typeof(DiffForm).Assembly.GetManifestResourceStream("DiffTool.AppIcon"))
            using (var icon = new Icon(stream, SystemInformation.IconSize))
                appIcon = (Icon)icon.Clone();
            Icon = appIcon;
            Text = "DiffTool — Git diff viewer";
            Size = new Size(1280, 820);
            MinimumSize = new Size(860, 560);
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            BackColor = Theme.Canvas;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20, 12, 20, 8) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 83));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            Controls.Add(layout);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            heading.Controls.Add(new Label { Text = "DiffTool", Font = new Font("Segoe UI", 19f, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty });
            repositoryLabel.Text = "Open a Git repository to get started";
            repositoryLabel.ForeColor = Theme.Muted;
            repositoryLabel.Dock = DockStyle.Fill;
            repositoryLabel.AutoEllipsis = true;
            heading.Controls.Add(repositoryLabel);
            var branchRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            branchRow.Controls.Add(new Label { Text = "Branch", ForeColor = Theme.Muted, AutoSize = false, Width = 64, Margin = new Padding(0, 3, 12, 0) });
            branchBox.DropDownStyle = ComboBoxStyle.DropDownList;
            branchBox.Width = 300;
            branchBox.DropDownWidth = 650;
            branchBox.Font = new Font("Segoe UI", 9f);
            branchBox.BackColor = Theme.Panel;
            branchBox.ForeColor = Theme.Blue;
            branchBox.FlatStyle = FlatStyle.Flat;
            branchBox.Margin = Padding.Empty;
            branchBox.AccessibleName = "Branch history";
            tooltip.SetToolTip(branchBox, "Choose a local or remote branch to view its commits. Working-change comparisons always use the current checkout.");
            branchBox.SelectedIndexChanged += async delegate { if (!updatingBranches) await LoadCommits(false, true); };
            branchRow.Controls.Add(branchBox);
            heading.Controls.Add(branchRow);
            header.Controls.Add(heading, 0, 0);
            Button openButton = MakeButton("Open repository…");
            openButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            openButton.Width = 155;
            openButton.Margin = Padding.Empty;
            openButton.Click += delegate { BrowseRepository(); };
            var repositoryActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 5, 0, 0) };
            repositoryActions.Controls.Add(openButton);
            recentButton = MakeButton("Recent repositories ▾");
            recentButton.Width = 155;
            recentButton.Margin = new Padding(0, 5, 0, 0);
            recentButton.Click += delegate { recentMenu.Show(recentButton, new Point(0, recentButton.Height)); };
            repositoryActions.Controls.Add(recentButton);
            header.Controls.Add(repositoryActions, 1, 0);
            UpdateRecentMenu();
            layout.Controls.Add(header, 0, 0);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
            toolbar.Controls.Add(new Label { Text = "Compare", AutoSize = false, Width = 64, Margin = new Padding(0, 5, 12, 0), ForeColor = Theme.Muted });
            scopeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            scopeBox.Items.AddRange(new object[] { "All working changes", "Staged changes", "Unstaged changes" });
            scopeBox.Width = 300;
            scopeBox.DropDownWidth = 950;
            scopeBox.Margin = Padding.Empty;
            scopeBox.MaxDropDownItems = 20;
            scopeBox.AccessibleName = "Working changes or previous commit";
            scopeBox.BackColor = Theme.Panel;
            scopeBox.ForeColor = Theme.Text;
            scopeBox.FlatStyle = FlatStyle.Flat;
            scopeBox.SelectedIndex = 0;
            toolbar.Controls.Add(scopeBox);
            refreshButton = MakeButton("Refresh  ·  F5");
            refreshButton.Margin = new Padding(12, 0, 0, 0);
            refreshButton.Click += async delegate { await RefreshView(); };
            toolbar.Controls.Add(refreshButton);
            olderButton = MakeButton("Load older commits");
            olderButton.Width = 160;
            olderButton.Margin = new Padding(12, 0, 0, 0);
            olderButton.Visible = false;
            olderButton.Click += async delegate { await LoadCommits(true); };
            toolbar.Controls.Add(olderButton);
            EventHandler alignComparisonButtons = delegate
            {
                refreshButton.Height = scopeBox.Height;
                olderButton.Height = scopeBox.Height;
            };
            scopeBox.SizeChanged += alignComparisonButtons;
            alignComparisonButtons(null, EventArgs.Empty);
            layout.Controls.Add(toolbar, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Border, SplitterWidth = 5, Margin = Padding.Empty, Size = new Size(1200, 580) };
            split.SplitterDistance = 540;
            split.Panel1MinSize = 380;
            split.Panel2MinSize = 400;
            split.Panel1.BackColor = Theme.Panel;
            split.Panel2.BackColor = Theme.Canvas;
            layout.Controls.Add(split, 0, 2);

            var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(6), BackColor = Theme.Panel };
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            countLabel.Text = "CHANGED FILES";
            countLabel.ForeColor = Theme.Muted;
            countLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            countLabel.Dock = DockStyle.Fill;
            sidebar.Controls.Add(countLabel, 0, 0);
            filterBox.Dock = DockStyle.Top;
            filterBox.BackColor = Theme.Canvas;
            filterBox.ForeColor = Theme.Text;
            filterBox.BorderStyle = BorderStyle.FixedSingle;
            filterBox.AccessibleName = "Filter changed files";
            tooltip.SetToolTip(filterBox, "Filter files by path (Ctrl+F)");
            sidebar.Controls.Add(filterBox, 0, 1);
            fileList.Dock = DockStyle.Fill;
            fileList.BorderStyle = BorderStyle.None;
            fileList.BackColor = Theme.Panel;
            fileList.ForeColor = Theme.Text;
            fileList.Margin = Padding.Empty;
            fileList.AccessibleName = "Changed files";
            fileList.SelectedIndexChanged += async delegate { if (!updatingList && SelectedChange != null) await LoadDiff(); };
            sidebar.Controls.Add(fileList.CreateColumnHeader(), 0, 2);
            var fileBrowser = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            fileBrowser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fileBrowser.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            fileBrowser.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            fileBrowser.Controls.Add(fileList, 0, 0);
            fileBrowser.Controls.Add(fileList.CreateMinimap(), 1, 0);
            sidebar.Controls.Add(fileBrowser, 0, 3);
            split.Panel1.Controls.Add(sidebar);

            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var detailHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(12, 5, 8, 5), BackColor = Theme.Panel, Margin = Padding.Empty };
            detailHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
            var titleStack = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            fileLabel.Text = "File preview";
            fileLabel.Dock = DockStyle.None;
            fileLabel.AutoEllipsis = true;
            fileLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            var fileHeading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            fileHeading.Controls.Add(fileLabel);
            titleStack.Controls.Add(fileHeading);
            lineStats.Text = "Unified diff · old and new line numbers";
            lineStats.Dock = DockStyle.Fill;
            lineStats.ForeColor = Theme.Muted;
            lineStats.Font = new Font("Segoe UI", 9f);
            titleStack.Controls.Add(lineStats);
            detailHeader.Controls.Add(titleStack, 0, 0);
            fullFileToggle.Text = "Full file";
            fullFileToggle.Checked = true;
            fullFileToggle.Appearance = Appearance.Button;
            fullFileToggle.TextAlign = ContentAlignment.MiddleCenter;
            fullFileToggle.FlatStyle = FlatStyle.Flat;
            fullFileToggle.FlatAppearance.BorderColor = Theme.Border;
            fullFileToggle.FlatAppearance.CheckedBackColor = Theme.HunkBackground;
            fullFileToggle.Size = new Size(96, 32);
            fullFileToggle.Anchor = AnchorStyles.Right;
            fullFileToggle.Cursor = Cursors.Hand;
            tooltip.SetToolTip(fullFileToggle, "Show the entire file with added and removed lines. Turn off to show changes with nearby context.");
            fullFileToggle.CheckedChanged += async delegate { if (!refreshing && SelectedChange != null) await LoadDiff(); };
            detailHeader.Controls.Add(fullFileToggle, 1, 0);
            copyButton = MakeButton("Copy diff");
            copyButton.Width = 96;
            copyButton.Height = 26;
            copyButton.Enabled = false;
            copyButton.Click += delegate
            {
                try { if (diffView.RawDiff.Length > 0) { Clipboard.SetText(diffView.RawDiff); statusLabel.Text = "Diff copied to clipboard."; } }
                catch (ExternalException) { statusLabel.Text = "Clipboard is busy. Please try again."; }
            };
            fileHeading.Controls.Add(copyButton);
            EventHandler arrangeFileHeading = delegate
            {
                int availableWidth = Math.Max(1, fileHeading.ClientSize.Width - copyButton.Width - 8);
                int textWidth = TextRenderer.MeasureText(fileLabel.Text, fileLabel.Font, new Size(int.MaxValue, fileHeading.Height), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
                fileLabel.SetBounds(0, 0, Math.Min(availableWidth, textWidth), fileHeading.ClientSize.Height);
                copyButton.Location = new Point(fileLabel.Right + 8, Math.Max(0, (fileHeading.ClientSize.Height - copyButton.Height) / 2));
            };
            fileHeading.Resize += arrangeFileHeading;
            fileLabel.TextChanged += arrangeFileHeading;
            arrangeFileHeading(null, EventArgs.Empty);
            detail.Controls.Add(detailHeader, 0, 0);
            diffView.Dock = DockStyle.Fill;
            diffView.Margin = Padding.Empty;
            detail.Controls.Add(diffView, 0, 1);
            split.Panel2.Controls.Add(detail);

            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.ForeColor = Theme.Muted;
            statusLabel.AutoEllipsis = true;
            statusLabel.Text = "Read-only viewer  ·  Ctrl+O open repository  ·  Ctrl+F filter files  ·  F5 refresh";
            layout.Controls.Add(statusLabel, 0, 3);
            diffView.ShowMessage("Your changes, in focus", "Open a Git repository to browse added, modified, and deleted files.");

            filterBox.TextChanged += delegate { ApplyFilter(null); };
            scopeBox.SelectedIndexChanged += async delegate { if (!updatingCommits) await RefreshChanges(); };
            KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; await RefreshView(); }
                else if (e.Control && e.KeyCode == Keys.O) { e.SuppressKeyPress = true; BrowseRepository(); }
                else if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; filterBox.Focus(); filterBox.SelectAll(); }
            };
            Shown += async delegate
            {
                // Start at 31.05% of the available width.
                split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min(split.Width - split.Panel2MinSize - split.SplitterWidth, (int)Math.Round(split.Width * 0.3105)));
                if (!string.IsNullOrWhiteSpace(initialPath)) await OpenRepository(initialPath);
                else if (recentRepositories.LastPath != null) await OpenRepository(recentRepositories.LastPath, false);
                else
                {
                    try { var candidate = await Task.Run(() => new GitRepository(Environment.CurrentDirectory)); await OpenRepository(candidate.Root); }
                    catch { IsIdle = true; }
                }
            };
            IsIdle = true;
        }

        private static Button MakeButton(string text)
        {
            var button = new Button { Text = text, Height = 32, Width = 128, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Text, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = Theme.Border;
            button.FlatAppearance.MouseOverBackColor = Theme.HunkBackground;
            return button;
        }

        private void BrowseRepository()
        {
            try
            {
                string path = RepositoryPicker.Show(Handle, repository == null ? recentRepositories.LastPath : repository.Root);
                if (path != null) OpenSelectedRepository(path);
            }
            catch (COMException error) { MessageBox.Show(this, error.Message, "Could not open repository picker", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private async void OpenSelectedRepository(string path) { await OpenRepository(path); }

        private void UpdateRecentMenu()
        {
            foreach (ToolStripItem item in recentMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
            int index = 1;
            foreach (string path in recentRepositories.Paths)
            {
                string recentPath = path;
                var item = new ToolStripMenuItem("&" + index++ + "  " + path.Replace("&", "&&")) { ToolTipText = path };
                item.Click += async delegate { await OpenRepository(recentPath); };
                recentMenu.Items.Add(item);
            }
            recentButton.Enabled = recentRepositories.Paths.Count > 0;
        }

        public Task OpenRepository(string path) { return OpenRepository(path, true); }

        private async Task OpenRepository(string path, bool showError)
        {
            int version = ++openVersion;
            IsIdle = false;
            statusLabel.Text = "Opening repository…";
            try
            {
                GitRepository opened = await Task.Run(() => new GitRepository(path));
                if (IsDisposed || version != openVersion) return;
                repository = opened;
                updatingBranches = true;
                branchBox.Items.Clear();
                updatingBranches = false;
                ++historyVersion;
                updatingCommits = true;
                while (scopeBox.Items.Count > 3) scopeBox.Items.RemoveAt(scopeBox.Items.Count - 1);
                if (scopeBox.SelectedIndex < 0) scopeBox.SelectedIndex = 0;
                olderButton.Visible = false;
                updatingCommits = false;
                repositoryLabel.Text = opened.Root;
                tooltip.SetToolTip(repositoryLabel, opened.Root);
                Text = Path.GetFileName(opened.Root) + " — DiffTool";
                bool historySaved = recentRepositories.Remember(opened.Root);
                UpdateRecentMenu();
                // Refresh the new repository before filtering; never load an old path from it.
                refreshing = true;
                filterBox.Clear();
                await RefreshView();
                if (!IsDisposed && version == openVersion && !historySaved) statusLabel.Text += "  |  Could not save recent repositories.";
            }
            catch (Exception error)
            {
                if (IsDisposed || version != openVersion) return;
                statusLabel.Text = "Could not open repository: " + error.Message;
                if (showError)
                    MessageBox.Show(this, error.Message + "\n\nChoose a local Git repository. Git must be installed and available on PATH.", "Could not open repository", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else diffView.ShowMessage("Last repository is unavailable", "Use Open repository or Recent repositories to choose another folder.");
            }
            finally { if (!IsDisposed && version == openVersion && repository == null) IsIdle = true; }
        }

        private async Task RefreshView()
        {
            if (repository != null) await LoadCommits(false);
        }

        private async Task LoadCommits(bool append, bool branchChanged = false)
        {
            if (repository == null) return;
            int version = ++historyVersion;
            ++refreshVersion;
            ++diffVersion;
            GitRepository current = repository;
            GitCommit selected = branchChanged ? null : scopeBox.SelectedItem as GitCommit;
            GitBranch selectedBranch = branchBox.SelectedItem as GitBranch;
            string reference = selectedBranch == null ? "HEAD" : selectedBranch.Reference;
            int workingIndex = !branchChanged && selected == null && scopeBox.SelectedIndex < 3 ? Math.Max(0, scopeBox.SelectedIndex) : 0;
            int existingCount = scopeBox.Items.OfType<GitCommit>().Count();
            int skip = append ? existingCount : 0;
            int count = append ? 200 : branchChanged ? 100 : Math.Max(100, existingCount);
            IsIdle = false;
            refreshing = true;
            olderButton.Enabled = false;
            scopeBox.Enabled = false;
            branchBox.Enabled = false;
            fileList.Enabled = false;
            refreshButton.Enabled = false;
            copyButton.Enabled = false;
            statusLabel.Text = "Reading commit history…";
            string historyError = null;
            try
            {
                var result = await Task.Run(() =>
                {
                    var branches = append ? null : current.Branches();
                    string target = branches == null || branches.Any(b => b.Reference == reference) ? reference : "HEAD";
                    return new { Branches = branches, Reference = target, Commits = current.Commits(count, skip, target) };
                });
                if (IsDisposed || version != historyVersion || current != repository) return;
                if (result.Branches != null)
                {
                    updatingBranches = true;
                    branchBox.BeginUpdate();
                    try
                    {
                        branchBox.Items.Clear();
                        branchBox.Items.AddRange(result.Branches.Cast<object>().ToArray());
                        branchBox.SelectedIndex = result.Branches.FindIndex(b => b.Reference == result.Reference);
                    }
                    finally { branchBox.EndUpdate(); updatingBranches = false; }
                }
                var commits = result.Commits;
                updatingCommits = true;
                scopeBox.BeginUpdate();
                try
                {
                    if (!append)
                        while (scopeBox.Items.Count > 3) scopeBox.Items.RemoveAt(scopeBox.Items.Count - 1);
                    scopeBox.Items.AddRange(commits.Cast<object>().ToArray());
                    int index = -1;
                    if (selected != null)
                        for (int i = 3; i < scopeBox.Items.Count; i++)
                            if (((GitCommit)scopeBox.Items[i]).Id == selected.Id) { index = i; break; }
                    scopeBox.SelectedIndex = branchChanged && scopeBox.Items.Count > 3 ? 3 : index >= 0 ? index : Math.Min(2, workingIndex);
                }
                finally { scopeBox.EndUpdate(); updatingCommits = false; }
                olderButton.Visible = commits.Count == count;
                olderButton.Enabled = olderButton.Visible;
            }
            catch (Exception error)
            {
                if (IsDisposed || version != historyVersion || current != repository) return;
                historyError = error.Message;
            }
            finally
            {
                if (!IsDisposed && version == historyVersion)
                {
                    updatingCommits = false;
                    refreshing = false;
                    scopeBox.Enabled = true;
                    branchBox.Enabled = true;
                    fileList.Enabled = true;
                    refreshButton.Enabled = true;
                    olderButton.Enabled = olderButton.Visible;
                }
            }
            if (IsDisposed || version != historyVersion || current != repository) return;
            await RefreshChanges();
            if (!IsDisposed && version == historyVersion && historyError != null)
                statusLabel.Text += "  |  Could not read commit history: " + historyError;
        }
        private async Task RefreshChanges()
        {
            if (repository == null) return;
            int version = ++refreshVersion;
            ++diffVersion;
            GitRepository current = repository;
            GitCommit selectedCommit = scopeBox.SelectedItem as GitCommit;
            ChangeScope scope = selectedCommit != null ? ChangeScope.Commit : (ChangeScope)Math.Max(0, scopeBox.SelectedIndex);
            string commit = scope == ChangeScope.Commit && selectedCommit != null ? selectedCommit.Id : null;
            FileChange selected = SelectedChange;
            IsIdle = false;
            refreshing = true;
            refreshButton.Enabled = false;
            fileList.Enabled = false;
            copyButton.Enabled = false;
            statusLabel.Text = "Reading Git changes…";
            diffView.ShowMessage("Reading changes…", "");
            try
            {
                var result = await Task.Run(() => new { Files = scope == ChangeScope.Commit && commit == null ? new List<FileChange>() : current.Changes(scope, commit), Branch = current.Branch() });
                if (IsDisposed || version != refreshVersion) return;
                changes = result.Files;
                loadedScope = scope;
                loadedCommit = commit;
                tooltip.SetToolTip(repositoryLabel, current.Root + "\nWorking checkout: " + result.Branch);
                refreshing = false;
                ApplyFilter(selected == null ? null : selected.Path);
                int added = changes.Count(c => c.Status == 'A');
                int deleted = changes.Count(c => c.Status == 'D');
                statusLabel.Text = string.Format("{0} files  ·  {1} added  ·  {2} modified / other  ·  {3} deleted  |  {4}", changes.Count, added, changes.Count - added - deleted, deleted,
                    scope == ChangeScope.Commit ? commit == null ? "No commits in this repository" : "Commit " + commit.Substring(0, 8) + " compared with its first parent (empty tree for initial commit)" : scope == ChangeScope.WorkingTree ? "HEAD → working folder (includes staged and untracked)" : scope == ChangeScope.Staged ? "HEAD → index" : "Index → working folder (includes untracked)");
            }
            catch (Exception error)
            {
                if (IsDisposed || version != refreshVersion) return;
                changes.Clear();
                refreshing = false;
                ApplyFilter(null);
                diffView.ShowMessage("Could not read changes", error.Message);
                statusLabel.Text = "Git error: " + error.Message;
            }
            finally
            {
                if (!IsDisposed && version == refreshVersion)
                {
                    refreshing = false;
                    refreshButton.Enabled = true;
                    fileList.Enabled = true;
                    if (SelectedChange == null) IsIdle = true;
                }
            }
        }

        private void ApplyFilter(string selectedPath)
        {
            if (refreshing) return;
            FileChange current = SelectedChange;
            if (selectedPath == null && current != null) selectedPath = current.Path;
            updatingList = true;
            fileList.SetFiles(changes, filterBox.Text, selectedPath);
            countLabel.Text = "CHANGED FILES  /  " + VisibleFileCount + (VisibleFileCount == changes.Count ? "" : " of " + changes.Count);
            updatingList = false;
            LoadSelectedDiff();
        }

        private async void LoadSelectedDiff() { await LoadDiff(); }

        private async Task LoadDiff()
        {
            int version = ++diffVersion;
            FileChange selected = SelectedChange;
            copyButton.Enabled = false;
            lineStats.Text = "";
            if (selected == null || repository == null)
            {
                fileLabel.Text = "File preview";
                diffView.ShowMessage(changes.Count == 0 ? "No changes in this view" : "No matching files", changes.Count == 0 ? "Choose another comparison or refresh after making changes." : "Try a different file filter.");
                IsIdle = true;
                return;
            }
            IsIdle = false;
            fileLabel.Text = selected.OldPath == null ? selected.Path : selected.OldPath + " → " + selected.Path;
            tooltip.SetToolTip(fileLabel, fileLabel.Text);
            diffView.ShowMessage("Loading diff…", selected.Path);
            GitRepository current = repository;
            ChangeScope scope = loadedScope;
            string commit = loadedCommit;
            bool fullFile = fullFileToggle.Checked;
            try
            {
                string diff = await Task.Run(() => current.Diff(selected, scope, fullFile, commit));
                if (IsDisposed || version != diffVersion) return;
                diffView.SetDiff(diff);
                lineStats.Text = string.Format("{0}  ·  +{1} added   −{2} deleted", selected.StatusLabel, diffView.Added, diffView.Deleted);
                copyButton.Enabled = diff.Length > 0;
            }
            catch (Exception error)
            {
                if (!IsDisposed && version == diffVersion) diffView.ShowMessage("Could not load this diff", error.Message);
            }
            finally { if (!IsDisposed && version == diffVersion) IsIdle = true; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tooltip.Dispose(); recentMenu.Dispose(); if (appIcon != null) appIcon.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
