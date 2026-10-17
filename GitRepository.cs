using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DiffTool
{
    public enum ChangeScope { WorkingTree, Staged, Unstaged, Commit }

    public sealed class GitBranch
    {
        public string Reference { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name; }
    }
    public sealed class GitCommit
    {
        public string Id { get; set; }
        public string Date { get; set; }
        public string Subject { get; set; }
        public override string ToString() { return Id.Substring(0, Math.Min(8, Id.Length)) + "  ·  " + Date + "  ·  " + Subject; }
    }

    public sealed class FileChange
    {
        public string Path { get; set; }
        public string OldPath { get; set; }
        public char Status { get; set; }
        public bool Untracked { get; set; }
        public long? SizeBytes { get; set; }
        public long? LineCount { get; set; }
        public long? AddedLines { get; set; }
        public long? DeletedLines { get; set; }
        internal string SizeObjectId { get; set; }
        public string DisplaySize
        {
            get
            {
                if (!SizeBytes.HasValue) return "—";
                double size = SizeBytes.Value;
                string[] units = { "B", "KB", "MB", "GB", "TB" };
                int unit = 0;
                bool added = Status == 'A' || Status == 'C';
                if (added) { size /= 1024; unit = 1; }
                while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
                string sizeText = size.ToString(added && unit == 1 ? "0.##" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
                if (added && SizeBytes.Value > 0 && size < 0.01) sizeText = "<0.01 KB";
                return sizeText;
            }
        }
        public string DisplayPath
        {
            get
            {
                string lines = LineCount.HasValue ? ", " + LineCount.Value.ToString("N0") + (LineCount.Value == 1 ? " line" : " lines") : "";
                string changed = AddedLines.HasValue && DeletedLines.HasValue ? ", +" + AddedLines.Value.ToString("N0") + " −" + DeletedLines.Value.ToString("N0") + " changed" : "";
                return Path + " (" + (SizeBytes.HasValue ? DisplaySize : "size unavailable") + lines + changed + ")";
            }
        }
        public string StatusLabel
        {
            get
            {
                switch (Status)
                {
                    case 'A': return Untracked ? "Untracked" : "Added";
                    case 'D': return "Deleted";
                    case 'R': return "Renamed";
                    case 'C': return "Copied";
                    case 'U': return "Conflict";
                    case 'T': return "Type changed";
                    default: return "Modified";
                }
            }
        }
    }

    public sealed class GitRepository
    {
        public string Root { get; private set; }
        private const int MaxUntrackedBytes = 2 * 1024 * 1024;

        public GitRepository(string path)
        {
            Root = path;
            Root = Run(new[] { "rev-parse", "--show-toplevel" }).TrimEnd('\r', '\n');
        }

        public string Branch()
        {
            int code;
            string branch = Run(new[] { "symbolic-ref", "--short", "HEAD" }, out code).Trim();
            if (code == 0) return branch;
            return "Detached at " + Run(new[] { "rev-parse", "--short", "HEAD" }).Trim();
        }

        private string Baseline()
        {
            int code;
            Run(new[] { "rev-parse", "--verify", "HEAD" }, out code);
            if (code == 0) return "HEAD";
            // Calculate the empty tree for this repository's object format, without writing it.
            return Run(new[] { "hash-object", "-t", "tree", "--stdin" }).Trim();
        }

        public List<GitBranch> Branches()
        {
            var branches = new List<GitBranch> { new GitBranch { Reference = "HEAD", Name = "Current checkout · " + Branch() } };
            string output = Run(new[] { "for-each-ref", "--sort=refname", "--format=%(refname)%00%(symref)", "refs/heads/", "refs/remotes/" });
            foreach (string line in output.Split('\n'))
            {
                string[] fields = line.TrimEnd('\r').Split('\0');
                if (fields.Length != 2 || fields[1].Length > 0) continue;
                string reference = fields[0];
                bool local = reference.StartsWith("refs/heads/", StringComparison.Ordinal);
                branches.Add(new GitBranch { Reference = reference, Name = local ? reference.Substring(11) : reference.Substring(13) + " (remote)" });
            }
            return branches;
        }

        public List<GitCommit> Commits(int count = 100, int skip = 0, string reference = "HEAD")
        {
            if (reference != "HEAD" && (reference == null || (!reference.StartsWith("refs/heads/", StringComparison.Ordinal) && !reference.StartsWith("refs/remotes/", StringComparison.Ordinal))))
                throw new ArgumentException("Select a branch to review.");
            int code;
            string revision = Run(new[] { "rev-parse", "--verify", reference + "^{commit}" }, out code).Trim();
            var commits = new List<GitCommit>();
            if (code != 0) return commits;
            string[] fields = Run(new[] { "log", "-z", "--format=%H%x00%cs%x00%s", "-n", count.ToString(), "--skip=" + skip, revision, "--" }).Split('\0');
            for (int i = 0; i + 2 < fields.Length; i += 3)
                commits.Add(new GitCommit { Id = fields[i], Date = fields[i + 1], Subject = fields[i + 2] });
            return commits;
        }
        private List<string> DiffArguments(ChangeScope scope, string commit = null)
        {
            var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames" };
            if (scope == ChangeScope.Commit)
            {
                if (commit == null || !Regex.IsMatch(commit, @"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z")) throw new ArgumentException("Select a commit to review.");
                string[] ancestors = Run(new[] { "rev-list", "--parents", "-n", "1", commit, "--" }).Trim().Split(' ');
                args.Add(ancestors.Length > 1 ? ancestors[1] : Run(new[] { "hash-object", "-t", "tree", "--stdin" }).Trim());
                args.Add(commit);
                return args;
            }
            if (scope == ChangeScope.Staged) args.Add("--cached");
            if (scope != ChangeScope.Unstaged) args.Add(Baseline());
            return args;
        }

        public List<FileChange> Changes(ChangeScope scope)
        {
            return Changes(scope, null);
        }

        public List<FileChange> Changes(ChangeScope scope, string commit)
        {
            var args = DiffArguments(scope, commit);
            args.Add("--raw");
            args.Add("--no-abbrev");
            args.Add("-z");
            args.Add("--");
            string[] fields = Run(args).Split('\0');
            var changes = new List<FileChange>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < fields.Length && fields[i].Length > 0;)
            {
                string[] metadata = fields[i++].Split(' ');
                char status = metadata[4][0];
                string first = fields[i++];
                string old = null;
                if (status == 'R' || status == 'C') { old = first; first = fields[i++]; }
                if (!paths.Add(first)) continue;
                changes.Add(new FileChange
                {
                    Path = first, OldPath = old, Status = status,
                    SizeObjectId = status == 'D' ? metadata[2] : scope == ChangeScope.Staged || scope == ChangeScope.Commit ? metadata[3] : null
                });
            }
            if (scope == ChangeScope.WorkingTree || scope == ChangeScope.Unstaged)
            {
                foreach (string path in Run(new[] { "ls-files", "--others", "--exclude-standard", "-z" }).Split('\0'))
                {
                    if (path.Length > 0 && paths.Add(path))
                        changes.Add(new FileChange { Path = path, Status = 'A', Untracked = true });
                }
            }
            LoadSizes(changes);
            LoadChangedLines(changes, scope, commit);
            changes.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            return changes;
        }

        private void LoadChangedLines(List<FileChange> changes, ChangeScope scope, string commit)
        {
            var tracked = new Dictionary<string, FileChange>(StringComparer.Ordinal);
            foreach (FileChange change in changes)
            {
                if (change.Untracked)
                {
                    if (change.LineCount.HasValue) { change.AddedLines = change.LineCount; change.DeletedLines = 0; }
                }
                else tracked[change.Path] = change;
            }
            if (tracked.Count == 0) return;
            var args = DiffArguments(scope, commit);
            args.Add("--numstat");
            args.Add("-z");
            args.Add("--");
            string[] records = Run(args).Split('\0');
            for (int i = 0; i < records.Length; i++)
            {
                string record = records[i];
                int firstTab = record.IndexOf('\t');
                int secondTab = firstTab < 0 ? -1 : record.IndexOf('\t', firstTab + 1);
                if (secondTab < 0) continue;
                string path = record.Substring(secondTab + 1);
                if (path.Length == 0)
                {
                    if (i + 2 >= records.Length) break;
                    path = records[i + 2];
                    i += 2;
                }
                FileChange change;
                long added, deleted;
                if (tracked.TryGetValue(path, out change) && change.LineCount.HasValue && change.Status != 'U'
                    && long.TryParse(record.Substring(0, firstTab), out added)
                    && long.TryParse(record.Substring(firstTab + 1, secondTab - firstTab - 1), out deleted))
                {
                    change.AddedLines = added;
                    change.DeletedLines = deleted;
                }
            }
        }

        private void LoadSizes(List<FileChange> changes)
        {
            var objectIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (FileChange change in changes)
            {
                if (change.SizeObjectId != null) objectIds.Add(change.SizeObjectId);
                else
                {
                    try
                    {
                        var file = new FileInfo(System.IO.Path.Combine(Root, change.Path));
                        if (file.Exists)
                        {
                            change.SizeBytes = file.Length;
                            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                                change.LineCount = CountTextLines(reader);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            if (objectIds.Count == 0) return;
            int code;
            string output = Run(new[] { "cat-file", "--batch-check" }, out code, string.Join("\n", objectIds) + "\n");
            if (code != 0) return;
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            var lfsObjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in output.Split('\n'))
            {
                string[] fields = line.TrimEnd('\r').Split(' ');
                long size;
                if (fields.Length == 3 && fields[1] == "blob" && long.TryParse(fields[2], out size))
                {
                    bool isLfs;
                    sizes[fields[0]] = ResolveAssetSize(fields[0], size, out isLfs);
                    if (isLfs) lfsObjects.Add(fields[0]);
                }
            }
            foreach (FileChange change in changes)
            {
                long size;
                if (change.SizeObjectId != null && sizes.TryGetValue(change.SizeObjectId, out size))
                {
                    change.SizeBytes = size;
                    if (!lfsObjects.Contains(change.SizeObjectId))
                        change.LineCount = CountBlobLines(change.SizeObjectId);
                }
            }
        }

        private long ResolveAssetSize(string objectId, long blobSize, out bool isLfs)
        {
            isLfs = false;
            // LFS stores a small pointer in Git; its size field describes the actual asset.
            if (blobSize > 1024) return blobSize;
            int code;
            string content = Run(new[] { "cat-file", "blob", objectId }, out code);
            if (code != 0) return blobSize;
            Match pointer = Regex.Match(content, @"\Aversion https://git-lfs.github.com/spec/v1\r?\n(?:ext-[^\r\n]+\r?\n)*oid sha256:[0-9a-f]{64}\r?\nsize ([0-9]+)(?:\r?\n)?\z");
            long assetSize;
            if (!pointer.Success || !long.TryParse(pointer.Groups[1].Value, out assetSize)) return blobSize;
            isLfs = true;
            return assetSize;
        }

        private static long? CountTextLines(TextReader reader)
        {
            var buffer = new char[8192];
            long lines = 0;
            char previous = '\0';
            bool hasText = false;
            try
            {
                int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        char c = buffer[i];
                        if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t' && c != '\f') return null;
                        if (c == '\r' || (c == '\n' && previous != '\r')) lines++;
                        previous = c;
                        hasText = true;
                    }
                }
                return lines + (hasText && previous != '\r' && previous != '\n' ? 1 : 0);
            }
            catch (DecoderFallbackException) { return null; }
        }

        private long? CountBlobLines(string objectId)
        {
            var start = GitStart(new[] { "cat-file", "blob", objectId });
            using (var process = Process.Start(start))
            {
                process.StandardInput.Close();
                Task<string> error = process.StandardError.ReadToEndAsync();
                Task<long?> count = Task.Run(() =>
                {
                    using (var reader = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false, true), true))
                        return CountTextLines(reader);
                });
                try
                {
                    if (!count.Wait(30000) || !count.Result.HasValue) return null;
                    if (!process.WaitForExit(30000) || process.ExitCode != 0) return null;
                    return count.Result;
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
            }
        }

        public string Diff(FileChange change, ChangeScope scope, bool fullFile = false)
        {
            return Diff(change, scope, fullFile, null);
        }

        public string Diff(FileChange change, ChangeScope scope, bool fullFile, string commit)
        {
            if (change.Untracked) return UntrackedDiff(change.Path);
            var args = DiffArguments(scope, commit);
            args.Add(fullFile ? "--unified=1000000000" : "--unified=3");
            args.Add("--");
            if (change.OldPath != null) args.Add(change.OldPath);
            args.Add(change.Path);
            string diff = Run(args);
            if (fullFile && change.LineCount.HasValue && diff.IndexOf("\n@@", StringComparison.Ordinal) < 0
                && diff.IndexOf("Binary files ", StringComparison.Ordinal) < 0)
            {
                // Renames and mode-only changes have no hunk; include their unchanged text.
                string source = change.SizeObjectId != null
                    ? Run(new[] { "cat-file", "blob", change.SizeObjectId })
                    : File.ReadAllText(System.IO.Path.Combine(Root, change.Path));
                if (source.Length > 0)
                {
                    string[] lines = source.Split('\n');
                    bool finalNewline = source.EndsWith("\n", StringComparison.Ordinal);
                    int count = lines.Length - (finalNewline ? 1 : 0);
                    var result = new StringBuilder(diff);
                    result.Append("@@ -1,").Append(count).Append(" +1,").Append(count).Append(" @@\n");
                    for (int i = 0; i < count; i++) result.Append(' ').Append(lines[i].TrimEnd('\r')).Append('\n');
                    if (!finalNewline) result.Append("\\ No newline at end of file\n");
                    return result.ToString();
                }
            }
            return diff.Length == 0 ? "No line changes in this view. Refresh to check for updates." : diff;
        }

        private string UntrackedDiff(string path)
        {
            string fullPath = System.IO.Path.Combine(Root, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxUntrackedBytes)
                    return "Untracked file is larger than 2 MB. Preview omitted: " + path;
                byte[] bytes = new byte[(int)stream.Length];
                int count = 0, read;
                while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
                for (int i = 0; i < count; i++)
                    if (bytes[i] == 0) return "Binary file added: " + path + " (" + count + " bytes)";
                string text = Encoding.UTF8.GetString(bytes, 0, count);
                if (text.Length == 0) return "Empty file added: " + path;
                string[] lines = text.Split('\n');
                bool finalNewline = text.EndsWith("\n", StringComparison.Ordinal);
                int length = lines.Length - (finalNewline ? 1 : 0);
                var diff = new StringBuilder("--- /dev/null\n+++ b/" + path + "\n@@ -0,0 +1," + length + " @@\n");
                for (int i = 0; i < length; i++) diff.Append('+').Append(lines[i].TrimEnd('\r')).Append('\n');
                if (!finalNewline) diff.Append("\\ No newline at end of file\n");
                return diff.ToString();
            }
        }

        public string Run(IEnumerable<string> arguments)
        {
            int code;
            string result = Run(arguments, out code);
            if (code != 0) throw new InvalidOperationException(result.Trim());
            return result;
        }

        private string Run(IEnumerable<string> arguments, out int exitCode, string input = null)
        {
            using (var process = Process.Start(GitStart(arguments)))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                if (input != null) process.StandardInput.Write(input);
                process.StandardInput.Close();
                if (!process.WaitForExit(30000))
                {
                    process.Kill();
                    throw new TimeoutException("Git took longer than 30 seconds. Try a smaller repository or retry.");
                }
                exitCode = process.ExitCode;
                return exitCode == 0 ? output.Result : error.Result;
            }
        }

        private ProcessStartInfo GitStart(IEnumerable<string> arguments)
        {
            var command = new StringBuilder("--no-pager --literal-pathspecs -c core.quotepath=false ");
            foreach (string arg in arguments) command.Append(QuoteArgument(arg)).Append(' ');
            var start = new ProcessStartInfo("git", command.ToString())
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            return start;
        }

        // Windows command-line quoting; filenames are always literal Git pathspecs.
        public static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
