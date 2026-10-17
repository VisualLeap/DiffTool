using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DiffTool
{
    public sealed class RecentRepositories
    {
        private readonly string settingsPath;
        private readonly List<string> paths = new List<string>();
        public IList<string> Paths { get { return paths.AsReadOnly(); } }
        public string LastPath { get { return paths.Count == 0 ? null : paths[0]; } }

        public RecentRepositories(string settingsPath = null)
        {
            this.settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffTool", "recent-repositories.txt");
            try
            {
                string readPath = this.settingsPath;
                // Import the previous application's history on the first DiffTool launch.
                if (settingsPath == null && !File.Exists(readPath))
                    readPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeReviewTool", "recent-repositories.txt");
                if (!File.Exists(readPath)) return;
                foreach (string line in File.ReadAllLines(readPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string path;
                    try { path = Normalize(line); }
                    catch (ArgumentException) { continue; }
                    catch (NotSupportedException) { continue; }
                    if (!paths.Exists(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase))) paths.Add(path);
                    if (paths.Count == 5) break;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            return full.Length > Path.GetPathRoot(full).Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        public bool Remember(string path)
        {
            path = Normalize(path);
            paths.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            paths.Insert(0, path);
            if (paths.Count > 5) paths.RemoveRange(5, paths.Count - 5);
            string temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath)));
                File.WriteAllLines(temporary, paths, new UTF8Encoding(false));
                if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null);
                else File.Move(temporary, settingsPath);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
