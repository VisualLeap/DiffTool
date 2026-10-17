using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
