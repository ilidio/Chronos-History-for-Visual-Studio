using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using LibGit2Sharp;

namespace ChronosHistoryVS
{
    public class HistoryFilter
    {
        private readonly HistoryStorage storage;

        public HistoryFilter(HistoryStorage storage)
        {
            this.storage = storage;
        }

        public async Task<List<Snapshot>> FilterHistoryForSelectionAsync(List<Snapshot> history, string filePath, SelectionRange selection)
        {
            if (history == null || history.Count == 0) return new List<Snapshot>();
            var sortedHistory = history.OrderByDescending(s => s.timestamp).ToList();
            var relevantSnapshots = new List<Snapshot>();
            var currentRange = selection;
            try {
                var newestSnapshot = sortedHistory[0];
                string newestContent = await storage.GetSnapshotContentAsync(newestSnapshot);
                string currentContent = File.Exists(filePath) ? File.ReadAllText(filePath) : "";
                string diff = GetUnifiedDiff(newestContent, currentContent);
                var hunks = ParseHunks(diff);
                currentRange = MapRangeBackwards(currentRange, hunks);
            } catch { }
            for (int i = 0; i < sortedHistory.Count - 1; i++) {
                var newSnapshot = sortedHistory[i];
                var oldSnapshot = sortedHistory[i + 1];
                try {
                    string newContent = await storage.GetSnapshotContentAsync(newSnapshot);
                    string oldContent = await storage.GetSnapshotContentAsync(oldSnapshot);
                    string diff = GetUnifiedDiff(oldContent, newContent);
                    var hunks = ParseHunks(diff);
                    if (IsRelevant(currentRange, hunks)) {
                        newSnapshot.relevantRange = new SelectionRange(currentRange.startLine, currentRange.endLine);
                        relevantSnapshots.Add(newSnapshot);
                    }
                    currentRange = MapRangeBackwards(currentRange, hunks);
                } catch { }
            }
            var oldest = sortedHistory.Last();
            oldest.relevantRange = new SelectionRange(currentRange.startLine, currentRange.endLine);
            relevantSnapshots.Add(oldest);
            return relevantSnapshots;
        }

        public async Task<List<Snapshot>> FilterGitHistoryForSelectionAsync(string filePath, SelectionRange range, string repoRoot, string relativePath)
        {
            return await Task.Run(async () => {
                var list = new List<Snapshot>();
                if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(relativePath)) return list;

                bool nativeFailed = false;
                try {
                    using (var repo = new Repository(repoRoot)) {
                        var logEntries = repo.Commits.QueryBy(relativePath, new CommitFilter { SortBy = CommitSortStrategies.Time });
                        var currentRange = range;
                        foreach (var entry in logEntries) {
                            var commit = entry.Commit;
                            var parent = commit.Parents.FirstOrDefault();
                            var newBlob = commit.Tree[relativePath]?.Target as Blob;
                            string newContent = newBlob?.GetContentText() ?? "";
                            if (parent != null) {
                                var oldBlob = parent.Tree[relativePath]?.Target as Blob;
                                string oldContent = oldBlob?.GetContentText() ?? "";
                                string diff = GetUnifiedDiff(oldContent, newContent, repo);
                                var hunks = ParseHunks(diff);
                                if (IsRelevant(currentRange, hunks)) {
                                    list.Add(new Snapshot { id = commit.Sha, timestamp = commit.Author.When.ToUnixTimeMilliseconds(), eventType = "git", label = commit.MessageShort, description = commit.Author.Name, filePath = filePath, relevantRange = new SelectionRange(currentRange.startLine, currentRange.endLine) });
                                }
                                currentRange = MapRangeBackwards(currentRange, hunks);
                            } else {
                                list.Add(new Snapshot { id = commit.Sha, timestamp = commit.Author.When.ToUnixTimeMilliseconds(), eventType = "git", label = commit.MessageShort, description = commit.Author.Name, filePath = filePath, relevantRange = new SelectionRange(currentRange.startLine, currentRange.endLine) });
                            }
                        }
                        return list;
                    }
                } catch { nativeFailed = true; }

                if (nativeFailed || list.Count == 0) {
                    try {
                        int start = Math.Max(1, range.startLine + 1);
                        int end = Math.Max(start, range.endLine + 1);
                        // Use a more robust log -L call. We use -n 100 to avoid excessive wait times on huge files.
                        string output = await RunGitCommandAsync(repoRoot, $"log -L {start},{end}:\"{relativePath}\" -n 100 -s --pretty=format:\"%H|%at|%an|%s\"");
                        
                        if (string.IsNullOrEmpty(output)) {
                            // If log -L failed, it might be due to line count mismatch. Try without -L as a last resort.
                            output = await RunGitCommandAsync(repoRoot, $"log -- \":(icase){relativePath}\" -n 50 --pretty=format:\"%H|%at|%an|%s\"");
                        }

                        if (!string.IsNullOrEmpty(output)) {
                            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in lines) {
                                var parts = line.Trim('"').Split('|');
                                if (parts.Length >= 4) {
                                    list.Add(new Snapshot { 
                                        id = parts[0], 
                                        timestamp = long.Parse(parts[1]) * 1000, 
                                        eventType = "git", 
                                        label = parts[3], 
                                        description = parts[2], 
                                        filePath = filePath, 
                                        relevantRange = range 
                                    });
                                }
                            }
                        }
                    } catch { }
                }
                return list;
            });
        }

        private async Task<string> RunGitCommandAsync(string workingDirectory, string arguments)
        {
            try {
                var startInfo = new System.Diagnostics.ProcessStartInfo { FileName = "git", Arguments = arguments, WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using (var process = System.Diagnostics.Process.Start(startInfo)) {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    process.WaitForExit();
                    return process.ExitCode == 0 ? output : "";
                }
            } catch { return ""; }
        }

        public string GetRelativePath(string repoRoot, string filePath)
        {
            try {
                string fullRoot = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullFile = Path.GetFullPath(filePath);
                if (fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) {
                    string relative = fullFile.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return string.IsNullOrEmpty(relative) ? "." : relative.Replace("\\", "/");
                }
                Uri rootUri = new Uri(fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString()) ? fullRoot : fullRoot + Path.DirectorySeparatorChar);
                Uri fileUri = new Uri(fullFile);
                if (rootUri.IsBaseOf(fileUri)) return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString());
            } catch { }
            return filePath;
        }

        private string GetUnifiedDiff(string oldContent, string newContent, Repository repo = null)
        {
            try {
                if (repo != null) {
                    var oldBlob = repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(oldContent ?? "")));
                    var newBlob = repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(newContent ?? "")));
                    return repo.Diff.Compare(oldBlob, newBlob).Patch;
                }
                return "";
            } catch { return ""; }
        }

        private List<DiffHunk> ParseHunks(string diff)
        {
            var hunks = new List<DiffHunk>();
            if (string.IsNullOrEmpty(diff)) return hunks;
            var lines = diff.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            DiffHunk currentHunk = null;
            int currentNew = 0;
            foreach (var line in lines) {
                if (line.StartsWith("@@")) {
                    var match = Regex.Match(line, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");
                    if (match.Success) {
                        if (currentHunk != null) hunks.Add(currentHunk);
                        int newStart = int.Parse(match.Groups[3].Value);
                        currentHunk = new DiffHunk { OldStart = int.Parse(match.Groups[1].Value), OldLines = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1, NewStart = newStart, NewLines = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1, TouchedLines = new HashSet<int>() };
                        currentNew = newStart - 1;
                    }
                } else if (currentHunk != null) {
                    if (line.StartsWith(" ")) currentNew++;
                    else if (line.StartsWith("-") && !line.StartsWith("---")) currentHunk.TouchedLines.Add(currentNew);
                    else if (line.StartsWith("+") && !line.StartsWith("+++")) { currentHunk.TouchedLines.Add(currentNew); currentNew++; }
                }
            }
            if (currentHunk != null) hunks.Add(currentHunk);
            return hunks;
        }

        private bool IsRelevant(SelectionRange range, List<DiffHunk> hunks)
        {
            foreach (var h in hunks) {
                foreach (var lineIdx in h.TouchedLines) if (lineIdx >= range.startLine && lineIdx <= range.endLine) return true;
            }
            return false;
        }

        private SelectionRange MapRangeBackwards(SelectionRange range, List<DiffHunk> hunks)
        {
            int newStart = range.startLine, newEnd = range.endLine;
            foreach (var h in hunks) {
                int hNewStart = h.NewStart - 1, hNewEnd = h.NewStart - 1 + h.NewLines, shift = h.NewLines - h.OldLines;
                if (hNewEnd <= range.startLine) newStart -= shift;
                else if (hNewStart < range.startLine && hNewEnd > range.startLine) newStart = h.OldStart - 1;
                if (hNewEnd <= range.endLine) newEnd -= shift;
                else if (hNewStart < range.endLine && hNewEnd > range.endLine) newEnd = (h.OldStart - 1) + (h.OldLines > 0 ? h.OldLines - 1 : 0);
            }
            return new SelectionRange(newStart, newEnd);
        }

        private class DiffHunk { public int OldStart { get; set; } public int OldLines { get; set; } public int NewStart { get; set; } public int NewLines { get; set; } public HashSet<int> TouchedLines { get; set; } }
    }
}
