using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using LibGit2Sharp;
using System.IO;
using System.Threading.Tasks;

namespace ChronosHistoryVS
{
    internal class HeatmapTag : TextMarkerTag
    {
        public HeatmapTag(string type) : base(type) { }
    }

    [Export(typeof(ITaggerProvider))]
    [ContentType("text")]
    [TagType(typeof(TextMarkerTag))]
    internal class HeatmapTaggerProvider : ITaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (buffer == null) return null;
            return buffer.Properties.GetOrCreateSingletonProperty(() => new HeatmapTagger(buffer)) as ITagger<T>;
        }
    }

    internal class HeatmapTagger : ITagger<TextMarkerTag>, IDisposable
    {
        private readonly ITextBuffer buffer;
        private Dictionary<int, long> lineBlame = new Dictionary<int, long>();
        private bool enabled = false;
        public static HeatmapTagger Instance { get; private set; }

        public HeatmapTagger(ITextBuffer buffer)
        {
            this.buffer = buffer;
            Instance = this;
        }

        public bool Enabled
        {
            get => enabled;
            set
            {
                if (enabled != value)
                {
                    enabled = value;
                    if (enabled) UpdateBlame();
                    TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length)));
                }
            }
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public void UpdateBlame()
        {
            if (!enabled) return;

            string filePath = GetFilePath(buffer);
            if (string.IsNullOrEmpty(filePath)) return;

            Task.Run(async () => {
                bool nativeFailed = false;
                try
                {
                    string repoPath = Repository.Discover(filePath);
                    if (repoPath != null)
                    {
                        using (var repo = new Repository(repoPath))
                        {
                            var blame = repo.Blame(filePath);
                            lineBlame.Clear();
                            foreach (var hunk in blame)
                            {
                                for (int i = 0; i < hunk.LineCount; i++)
                                {
                                    lineBlame[hunk.FinalStartLineNumber + i] = hunk.FinalSignature.When.ToUnixTimeMilliseconds();
                                }
                            }
                        }
                    }
                }
                catch { nativeFailed = true; }

                if (nativeFailed || lineBlame.Count == 0)
                {
                    try {
                        string directory = Path.GetDirectoryName(filePath);
                        string fileName = Path.GetFileName(filePath);
                        // git blame --incremental returns one line per commit info, then lines
                        // We use a simpler format: git blame --porcelain which starts each line with commit info
                        string output = await RunGitCommandAsync(directory, $"blame --porcelain \"{fileName}\"");
                        if (!string.IsNullOrEmpty(output)) {
                            lineBlame.Clear();
                            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            int currentLine = 0;
                            
                            // Porcelain format is complex, but the first 40 chars are the SHA, 
                            // followed by line numbers. We look for 'author-time'
                            var commitTimes = new Dictionary<string, long>();
                            foreach(var line in lines) {
                                if (line.StartsWith("author-time ")) {
                                    string[] parts = line.Split(' ');
                                    if (parts.Length > 1 && long.TryParse(parts[1], out long time)) {
                                        // Next line processing will associate this time
                                    }
                                }
                                // Simplified: Use 'git blame -t' for easier parsing
                            }
                            
                            // Re-trying with a simpler parseable format
                            string simpleOutput = await RunGitCommandAsync(directory, $"blame -p \"{fileName}\"");
                            if (!string.IsNullOrEmpty(simpleOutput)) {
                                var pLines = simpleOutput.Split('\n');
                                string lastSha = "";
                                for(int i=0; i<pLines.Length; i++) {
                                    string pl = pLines[i];
                                    if (pl.Length > 40 && pl.IndexOf(' ') == 40) {
                                        lastSha = pl.Substring(0, 40);
                                        // author-time is usually 3 lines down
                                        for(int j=i+1; j<Math.Min(i+10, pLines.Length); j++) {
                                            if (pLines[j].StartsWith("author-time ")) {
                                                if (long.TryParse(pLines[j].Substring(12).Trim(), out long time)) {
                                                    string[] parts = pl.Split(' ');
                                                    if (parts.Length >= 3 && int.TryParse(parts[2], out int lineNum)) {
                                                        lineBlame[lineNum - 1] = time * 1000;
                                                    }
                                                }
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    } catch { }
                }

                if (lineBlame.Count > 0) {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length)));
                }
            });
        }

        private async Task<string> RunGitCommandAsync(string workingDirectory, string arguments)
        {
            try {
                var startInfo = new System.Diagnostics.ProcessStartInfo {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(startInfo)) {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    process.WaitForExit();
                    return process.ExitCode == 0 ? output : "";
                }
            } catch { return ""; }
        }

        private string GetFilePath(ITextBuffer buffer)
        {
            buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document);
            return document?.FilePath;
        }

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (!enabled || spans.Count == 0 || lineBlame.Count == 0) yield break;

            var snapshot = spans[0].Snapshot;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long ONE_DAY = 24 * 60 * 60 * 1000;
            long ONE_WEEK = 7 * ONE_DAY;
            long ONE_MONTH = 30 * ONE_DAY;

            foreach (var span in spans)
            {
                int startLine = snapshot.GetLineNumberFromPosition(span.Start);
                int endLine = snapshot.GetLineNumberFromPosition(span.End);

                for (int i = startLine; i <= endLine; i++)
                {
                    if (lineBlame.TryGetValue(i, out long timestamp))
                    {
                        long age = now - timestamp;
                        string type = null;

                        if (age < ONE_DAY) type = "HeatmapHot";
                        else if (age < ONE_WEEK) type = "HeatmapWarm";
                        else if (age < ONE_MONTH) type = "HeatmapLukewarm";

                        if (type != null)
                        {
                            var line = snapshot.GetLineFromLineNumber(i);
                            yield return new TagSpan<TextMarkerTag>(
                                new SnapshotSpan(line.Start, line.Length),
                                new HeatmapTag(type)
                            );
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (Instance == this) Instance = null;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name("HeatmapHot")]
    [UserVisible(true)]
    internal class HeatmapHotDefinition : MarkerFormatDefinition
    {
        public HeatmapHotDefinition()
        {
            this.BackgroundColor = Color.FromArgb(40, 255, 0, 0);
            this.DisplayName = "Heatmap Hot (Last 24h)";
            this.ZOrder = 5;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name("HeatmapWarm")]
    [UserVisible(true)]
    internal class HeatmapWarmDefinition : MarkerFormatDefinition
    {
        public HeatmapWarmDefinition()
        {
            this.BackgroundColor = Color.FromArgb(30, 255, 165, 0);
            this.DisplayName = "Heatmap Warm (Last Week)";
            this.ZOrder = 5;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name("HeatmapLukewarm")]
    [UserVisible(true)]
    internal class HeatmapLukewarmDefinition : MarkerFormatDefinition
    {
        public HeatmapLukewarmDefinition()
        {
            this.BackgroundColor = Color.FromArgb(20, 255, 255, 0);
            this.DisplayName = "Heatmap Lukewarm (Last Month)";
            this.ZOrder = 5;
        }
    }
}
