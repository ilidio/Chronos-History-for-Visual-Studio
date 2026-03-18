using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio;
using LibGit2Sharp;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;

namespace ChronosHistoryVS
{
    public partial class HistoryToolWindowControl : UserControl
    {
        private readonly HistoryStorage storage;
        private readonly HistoryFilter filter;
        private string currentViewMode = "history";
        private bool isWebViewReady = false;

        public HistoryToolWindowControl(HistoryStorage storage)
        {
            InitializeComponent();
            this.storage = storage;
            this.filter = new HistoryFilter(storage);
            _ = InitializeWebViewAsync();
        }

        public async Task SetViewModeAsync(string mode)
        {
            currentViewMode = mode;
            if (isWebViewReady)
            {
                await RefreshCurrentViewAsync();
            }
        }

        private async Task RefreshCurrentViewAsync()
        {
            string path = await GetActiveFilePathAsync();
            
            switch (currentViewMode)
            {
                case "history": await SendHistoryAsync(path); break;
                case "gitHistory": await SendGitHistoryAsync(path); break;
                case "historySelection": await SendSelectionHistoryAsync(path); break;
                case "gitHistorySelection": await SendGitSelectionHistoryAsync(path); break;
                case "projectHistory": await SendProjectHistoryAsync(); break;
                case "putLabel": await RequestLabelAsync(path); break;
                case "recentChanges": await SendRecentChangesAsync(); break;
                case "export": await ExportHistoryAsync(); break;
                case "import": await ImportHistoryAsync(); break;
                case "historyGraph": await SendHistoryGraphAsync(); break;
                case "heatmap": await SendHeatmapAsync(path); break;
                case "generateCommit": await GenerateCommitMessageAsync(); break;
                case "compareWithBranch": await CompareWithBranchAsync(path); break;
            }
        }

        private async Task SendHistoryGraphAsync()
        {
            var history = await storage.GetAllHistoryAsync();
            var graphData = history
                .GroupBy(s => DateTimeOffset.FromUnixTimeMilliseconds(s.timestamp).Date)
                .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), count = g.Count() })
                .OrderBy(x => x.date)
                .ToList();

            var response = new { command = "graphData", data = graphData, fullHistory = history.Take(100).ToList(), title = "History activity Graph" };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
        }

        private async Task SendHeatmapAsync(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (HeatmapTagger.Instance != null)
            {
                HeatmapTagger.Instance.Enabled = !HeatmapTagger.Instance.Enabled;
                if (HeatmapTagger.Instance.Enabled)
                {
                    HeatmapTagger.Instance.UpdateBlame();
                    System.Windows.MessageBox.Show("Code Heatmap: Enabled (Red < 24h, Orange < 1w, Yellow < 1m)", "Chronos History");
                }
                else
                {
                    System.Windows.MessageBox.Show("Code Heatmap: Disabled", "Chronos History");
                }
            }
            else
            {
                System.Windows.MessageBox.Show("Heatmap tagger not found. Try opening a code file first.", "Error");
            }

            var response = new { command = "heatmapData", enabled = HeatmapTagger.Instance?.Enabled ?? false, title = "Code Heatmap: " + Path.GetFileName(filePath) };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
        }

        private async Task GenerateCommitMessageAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var package = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(ChronosPackage)) as ChronosPackage;
            var options = package?.GetDialogPage(typeof(ChronosOptionsPage)) as ChronosOptionsPage;

            if (string.IsNullOrEmpty(options?.ApiKey))
            {
                System.Windows.MessageBox.Show("Please configure your Gemini API Key in Tools -> Options -> Chronos History -> AI Settings.", "AI Not Configured");
                return;
            }

            var history = await storage.GetAllHistoryAsync();
            var recent = history.Take(20).ToList();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"User language: {options.Language}");
            foreach(var s in recent) sb.AppendLine($"- File: {Path.GetFileName(s.filePath)}, Event: {s.eventType}, Label: {s.label ?? "N/A"}, Time: {DateTimeOffset.FromUnixTimeMilliseconds(s.timestamp)}");

            string prompt = $@"You are an expert developer. Based on the summary of changes, generate a professional Git commit message in {options.Language} using Conventional Commits format. Changes:\n{sb.ToString()}\nCommit Message:";
            string result = "Failed to generate message.";
            try {
                using (var client = new HttpClient()) {
                    var requestBody = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
                    string url = $"https://generativelanguage.googleapis.com/v1beta/models/{options.Model}:generateContent?key={options.ApiKey}";
                    var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(url, content);
                    if (response.IsSuccessStatusCode) {
                        var responseJson = await response.Content.ReadAsStringAsync();
                        using (var doc = JsonDocument.Parse(responseJson)) {
                            result = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                        }
                    } else result = $"Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}";
                }
            } catch (Exception ex) { result = $"AI Error: {ex.Message}"; }

            var webResponse = new { command = "commitMessage", message = result, title = "AI Commit Message (Generated)" };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(webResponse)})");
        }

        private async Task ExportHistoryAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var sfd = new Microsoft.Win32.SaveFileDialog { Filter = "Chronos History (*.zip)|*.zip", FileName = $"ChronosHistory_{DateTime.Now:yyyyMMdd_HHmm}.zip" };
            if (sfd.ShowDialog() == true) {
                await storage.ExportHistoryAsync(sfd.FileName);
                System.Windows.MessageBox.Show("History exported successfully!", "Chronos History", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }

        private async Task ImportHistoryAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Chronos History (*.zip)|*.zip" };
            if (ofd.ShowDialog() == true) {
                await storage.ImportHistoryAsync(ofd.FileName);
                currentViewMode = "projectHistory";
                await RefreshCurrentViewAsync();
                System.Windows.MessageBox.Show("History imported successfully!", "Chronos History", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }

        private async Task RequestLabelAsync(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var response = new { command = "askLabel", filePath = path };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
        }

        private async Task SendProjectHistoryAsync()
        {
            var history = await storage.GetAllHistoryAsync();
            var response = new { command = "historyData", data = history, title = "Project History (All Files)", queryPath = "Global" };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
        }

        private async Task SendRecentChangesAsync()
        {
            var history = await storage.GetAllHistoryAsync();
            var recent = history.Take(50).ToList();
            var response = new { command = "historyData", data = recent, title = "Recent Changes (Last 50)", queryPath = "Global" };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
        }

        private async Task CreateLabelAsync(string path, string labelName)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(labelName)) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as EnvDTE.DTE;
            string content = "";
            if (dte?.ActiveDocument != null && dte.ActiveDocument.FullName.Equals(path, StringComparison.OrdinalIgnoreCase)) {
                var textDoc = dte.ActiveDocument.Object("TextDocument") as EnvDTE.TextDocument;
                if (textDoc != null) content = textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint);
            }
            if (string.IsNullOrEmpty(content) && File.Exists(path)) content = File.ReadAllText(path);
            if (!string.IsNullOrEmpty(content)) {
                await storage.SaveSnapshotAsync(path, content, "label", labelName);
                currentViewMode = "history";
                await RefreshCurrentViewAsync();
            }
        }

        private async Task InitializeWebViewAsync()
        {
            try {
                string tempDir = Path.GetTempPath();
                string userDataFolder = Path.Combine(tempDir, "ChronosHistoryVS_WebView2");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);
                webView.CoreWebView2.Settings.IsScriptEnabled = true;
                webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                string extensionPath = Path.GetDirectoryName(typeof(HistoryToolWindowControl).Assembly.Location);
                string[] possiblePaths = new[] { Path.Combine(extensionPath, "Resources", "webview", "history.html"), Path.Combine(extensionPath, "webview", "history.html"), Path.Combine(extensionPath, "history.html") };
                string webviewPath = possiblePaths.FirstOrDefault(File.Exists);
                if (webviewPath != null) {
                    webView.CoreWebView2.Navigate(new Uri(webviewPath).AbsoluteUri);
                    isWebViewReady = true;
                } else webView.CoreWebView2.NavigateToString("<html><body><h1>Chronos History</h1><p>Webview assets not found.</p></body></html>");
            } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WebView2 Error: {ex}"); }
        }

        private async Task<(string repoRoot, string relativePath)> GetGitInfoAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return (null, null);
            string dir = Path.GetDirectoryName(filePath);

            string repoRoot = (await RunGitCommandAsync(dir, "rev-parse --show-toplevel")).Trim().Replace("\\", "/");
            if (string.IsNullOrEmpty(repoRoot)) return (null, null);

            // Step 1: Try to get relative path manually first to avoid drive letter issues
            string relativePath = filePath.Replace("\\", "/");
            string normalizedRoot = repoRoot.EndsWith("/") ? repoRoot : repoRoot + "/";
            if (relativePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) {
                relativePath = relativePath.Substring(normalizedRoot.Length);
            } else {
                // If drive letter casing differs, try matching after the drive letter
                int colonIdx = relativePath.IndexOf(':');
                int rootColonIdx = normalizedRoot.IndexOf(':');
                if (colonIdx > 0 && rootColonIdx > 0 && 
                    string.Equals(relativePath.Substring(colonIdx), normalizedRoot.Substring(rootColonIdx), StringComparison.OrdinalIgnoreCase)) {
                    relativePath = relativePath.Substring(normalizedRoot.Length);
                }
            }

            // Step 2: Use Git's Case-Insensitive lookup with the relative path
            string gitRelativePath = (await RunGitCommandAsync(repoRoot, $"ls-files --full-name \":(icase){relativePath}\"")).Trim().Replace("\\", "/");
            
            if (string.IsNullOrEmpty(gitRelativePath)) {
                // Fallback to the manually calculated relative path if ls-files failed
                gitRelativePath = relativePath;
            }

            return (repoRoot, gitRelativePath);
        }

        private async Task<List<string>> GetBranchesAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) filePath = await GetActiveFilePathAsync();
            return await Task.Run(async () => {
                var list = new List<string>();
                var gitInfo = await GetGitInfoAsync(filePath);
                if (string.IsNullOrEmpty(gitInfo.repoRoot)) return list;

                bool nativeFailed = false;
                try {
                    using (var repo = new Repository(gitInfo.repoRoot)) {
                        foreach (var b in repo.Branches) {
                            if (!b.FriendlyName.Contains("HEAD")) list.Add(b.FriendlyName);
                        }
                    }
                } catch { nativeFailed = true; }
                
                if (nativeFailed || list.Count == 0) {
                    string output = await RunGitCommandAsync(gitInfo.repoRoot, "branch -a --format=\"%(refname:short)\"");
                    if (!string.IsNullOrEmpty(output)) {
                        var cliBranches = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(b => b.Trim().Trim('"'))
                            .Where(b => !string.IsNullOrEmpty(b) && !b.Contains("HEAD") && b != "*");
                        list.AddRange(cliBranches);
                    }
                }
                return list.Select(b => b.Replace("origin/", "").Replace("remotes/", "")).Distinct().OrderBy(b => b).ToList();
            });
        }

        private async Task<string> GetFileContentFromBranchOrCommitAsync(string filePath, string refName)
        {
            if (string.IsNullOrEmpty(filePath)) filePath = await GetActiveFilePathAsync();
            return await Task.Run(async () => {
                var gitInfo = await GetGitInfoAsync(filePath);
                if (string.IsNullOrEmpty(gitInfo.repoRoot)) return null;

                bool nativeFailed = false;
                try {
                    using (var repo = new Repository(gitInfo.repoRoot)) {
                        var reference = repo.Branches[refName] ?? repo.Branches[$"origin/{refName}"] ?? repo.Branches[$"remotes/origin/{refName}"];
                        LibGit2Sharp.Commit commit = reference != null ? reference.Tip : null;
                        if (commit == null) { try { commit = repo.Lookup<LibGit2Sharp.Commit>(refName); } catch { } }
                        if (commit != null) {
                            var blob = commit.Tree[gitInfo.relativePath]?.Target as Blob;
                            return blob?.GetContentText();
                        }
                    }
                } catch { nativeFailed = true; }

                if (nativeFailed) {
                    string[] refsToTry = new[] { refName, $"origin/{refName}", $"remotes/origin/{refName}" };
                    foreach (var r in refsToTry) {
                        string exists = await RunGitCommandAsync(gitInfo.repoRoot, $"ls-tree -r {r} --name-only \"{gitInfo.relativePath}\"");
                        if (!string.IsNullOrEmpty(exists.Trim())) return await RunGitCommandAsync(gitInfo.repoRoot, $"show {r}:\"{gitInfo.relativePath}\"");
                    }
                }
                return null;
            });
        }

        private async Task CompareWithBranchAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) filePath = await GetActiveFilePathAsync();
            var branches = await GetBranchesAsync(filePath);
            if (branches.Count == 0) {
                System.Windows.MessageBox.Show("No branches found. Ensure the project is a Git repository.", "Git");
                return;
            }
            var response = new { command = "pickBranch", branches = branches, filePath = filePath };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
        }

        private async Task OpenDiffWithRefAsync(string filePath, string refName, string label)
        {
            if (string.IsNullOrEmpty(filePath)) filePath = await GetActiveFilePathAsync();
            string refContent = await GetFileContentFromBranchOrCommitAsync(filePath, refName);
            if (refContent == null) {
                System.Windows.MessageBox.Show($"File not found in {refName}", "Error");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var diffService = ServiceProvider.GlobalProvider.GetService(typeof(SVsDifferenceService)) as IVsDifferenceService;
            if (diffService != null) {
                string fileName = Path.GetFileName(filePath);
                string safeRef = refName.Replace("/", "_").Replace("\\", "_");
                string tempFile = Path.Combine(Path.GetTempPath(), $"Chronos_{safeRef}_{fileName}");
                File.WriteAllText(tempFile, refContent);
                string leftLabel = label ?? refName;
                string rightLabel = $"Current: {fileName}";
                diffService.OpenComparisonWindow2(tempFile, filePath, $"{leftLabel} vs {rightLabel}", "Chronos History", leftLabel, rightLabel, null, null, 0);
            }
        }

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try {
                string jsonMessage = e.WebMessageAsJson;
                var request = JsonSerializer.Deserialize<WebMessageRequest>(jsonMessage);
                switch (request.command)
                {
                    case "getHistory": await RefreshCurrentViewAsync(); break;
                    case "preview": await PreviewDiffAsync(request.snapshotId); break;
                    case "restore": await RestoreSnapshotAsync(request.snapshotId); break;
                    case "openDiff": await OpenDiffAsync(request.snapshotId); break;
                    case "createLabel": await CreateLabelAsync(request.filePath, request.label); break;
                    case "compareWithBranch": await CompareWithBranchAsync(request.filePath); break;
                    case "compareWithRef": await OpenDiffWithRefAsync(request.filePath, request.refName, request.label); break;
                }
            } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Message Error: {ex}"); }
        }

        private async Task PreviewDiffAsync(string snapshotId)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await OpenDiffAsync(snapshotId);
        }

        private async Task SendGitHistoryAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            
            var gitSnapshots = await Task.Run(async () => {
                var list = new List<Snapshot>();
                string repoRoot = "Not Found";
                string relativePath = "N/A";
                bool discovered = false;
                string errorMsg = "";

                try {
                    var gitInfo = await GetGitInfoAsync(filePath);
                    if (!string.IsNullOrEmpty(gitInfo.repoRoot)) {
                        repoRoot = gitInfo.repoRoot;
                        relativePath = gitInfo.relativePath;
                        discovered = true;

                        bool nativeFailed = false;
                        try {
                            using (var repo = new Repository(repoRoot)) {
                                var commitFilter = new CommitFilter { SortBy = CommitSortStrategies.Time, IncludeReachableFrom = repo.Head.Tip };
                                foreach (var entry in repo.Commits.QueryBy(relativePath, commitFilter)) {
                                    list.Add(new Snapshot { id = entry.Commit.Sha, timestamp = entry.Commit.Author.When.ToUnixTimeMilliseconds(), eventType = "git", label = entry.Commit.MessageShort, description = entry.Commit.Author.Name, filePath = filePath });
                                }
                            }
                        } catch { nativeFailed = true; }

                        if (nativeFailed || list.Count == 0) {
                            string output = await RunGitCommandAsync(repoRoot, $"log --pretty=format:\"%H|%at|%an|%s\" -- \"{relativePath}\"");
                            if (!string.IsNullOrEmpty(output)) {
                                var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var line in lines) {
                                    var parts = line.Trim('"').Split('|');
                                    if (parts.Length >= 4) list.Add(new Snapshot { id = parts[0], timestamp = long.Parse(parts[1]) * 1000, eventType = "git", label = parts[3], description = parts[2], filePath = filePath });
                                }
                            }
                        }
                    }
                } catch (Exception ex) { errorMsg = ex.Message; }

                return new { list, repoRoot, relativePath, discovered, errorMsg };
            });

            var response = new { command = "historyData", data = gitSnapshots.list, title = "Git History: " + Path.GetFileName(filePath), queryPath = filePath, repoRoot = gitSnapshots.repoRoot, relativePath = gitSnapshots.relativePath, gitDiscovered = gitSnapshots.discovered, debugInfo = gitSnapshots.errorMsg };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
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

        private async Task SendHistoryAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) {
                await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(new { command = "historyData", data = new object[] { }, queryPath = "None" })})");
                return;
            }
            var history = await storage.GetHistoryForFileAsync(filePath);
            var response = new { command = "historyData", data = history, title = "Local History: " + Path.GetFileName(filePath), queryPath = filePath };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
        }

        private async Task SendSelectionHistoryAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            var range = await GetSelectionRangeAsync();
            if (range == null) return;
            var history = await storage.GetHistoryForFileAsync(filePath);
            var filtered = await filter.FilterHistoryForSelectionAsync(history, filePath, range);
            var response = new { command = "historyData", data = filtered, title = $"Selection History: {Path.GetFileName(filePath)} (Lines {range.startLine + 1}-{range.endLine + 1})", queryPath = filePath };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
        }

        private async Task SendGitSelectionHistoryAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            var range = await GetSelectionRangeAsync();
            if (range == null) return;
            
            var result = await Task.Run(async () => {
                string repoRoot = "Not Found";
                string relativePath = "N/A";
                bool discovered = false;
                List<Snapshot> list = new List<Snapshot>();

                var gitInfo = await GetGitInfoAsync(filePath);
                if (!string.IsNullOrEmpty(gitInfo.repoRoot)) {
                    repoRoot = gitInfo.repoRoot;
                    relativePath = gitInfo.relativePath;
                    discovered = true;
                    try { list = await filter.FilterGitHistoryForSelectionAsync(filePath, range, repoRoot, relativePath); } catch { }
                }
                return new { list, repoRoot, relativePath, discovered };
            });

            var response = new { command = "historyData", data = result.list, title = $"Git Selection History: {Path.GetFileName(filePath)} (Lines {range.startLine + 1}-{range.endLine + 1})", queryPath = filePath, repoRoot = result.repoRoot, relativePath = result.relativePath, gitDiscovered = result.discovered };
            await webView.CoreWebView2.ExecuteScriptAsync($"onMessage({JsonSerializer.Serialize(response)})");
        }

        private async Task<SelectionRange> GetSelectionRangeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as EnvDTE.DTE;
            var selection = dte?.ActiveDocument?.Selection as EnvDTE.TextSelection;
            if (selection == null) return null;
            return new SelectionRange(selection.TopLine - 1, selection.BottomLine - 1);
        }

        private async Task<string> GetSnapshotContentByIdAsync(string snapshotId, string currentPath)
        {
            if (string.IsNullOrEmpty(snapshotId) || string.IsNullOrEmpty(currentPath)) return null;
            if (snapshotId.Length == 40 && !snapshotId.Contains("-")) {
                var gitInfo = await GetGitInfoAsync(currentPath);
                if (!string.IsNullOrEmpty(gitInfo.repoRoot)) {
                    try {
                        using (var repo = new Repository(gitInfo.repoRoot)) {
                            var commit = repo.Lookup<LibGit2Sharp.Commit>(snapshotId);
                            var blob = (Blob)commit.Tree[gitInfo.relativePath].Target;
                            return blob.GetContentText();
                        }
                    } catch { }
                    return await RunGitCommandAsync(gitInfo.repoRoot, $"show {snapshotId}:\"{gitInfo.relativePath}\"");
                }
            } else {
                var history = await storage.GetAllHistoryAsync();
                var snapshot = history.FirstOrDefault(s => s.id == snapshotId);
                if (snapshot != null) return await storage.GetSnapshotContentAsync(snapshot);
            }
            return null;
        }

        private async Task RestoreSnapshotAsync(string snapshotId)
        {
            string currentPath = await GetActiveFilePathAsync();
            string content = await GetSnapshotContentByIdAsync(snapshotId, currentPath);
            if (content != null) {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as EnvDTE.DTE;
                if (dte?.ActiveDocument != null) {
                    var textDoc = dte.ActiveDocument.Object("TextDocument") as EnvDTE.TextDocument;
                    if (textDoc != null) {
                        var editPoint = textDoc.StartPoint.CreateEditPoint();
                        editPoint.ReplaceText(textDoc.EndPoint, content, (int)EnvDTE.vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);
                    }
                }
            }
        }

        private async Task OpenDiffAsync(string snapshotId)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string currentPath = await GetActiveFilePathAsync();
            if (string.IsNullOrEmpty(currentPath)) return;
            string snapshotContent = await GetSnapshotContentByIdAsync(snapshotId, currentPath);
            if (snapshotContent == null) return;
            var diffService = ServiceProvider.GlobalProvider.GetService(typeof(SVsDifferenceService)) as IVsDifferenceService;
            if (diffService != null) {
                string fileName = Path.GetFileName(currentPath);
                string tempFile = Path.Combine(Path.GetTempPath(), $"Chronos_{snapshotId.Substring(0, Math.Min(8, snapshotId.Length))}_{fileName}");
                File.WriteAllText(tempFile, snapshotContent);
                string leftLabel = $"Snapshot: {snapshotId.Substring(0, Math.Min(8, snapshotId.Length))}";
                string rightLabel = $"Current: {fileName}";
                diffService.OpenComparisonWindow2(tempFile, currentPath, $"{leftLabel} vs {rightLabel}", "Chronos History", leftLabel, rightLabel, null, null, 0);
            }
        }

        private async Task<string> GetActiveFilePathAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            IVsMonitorSelection selectionMonitor = ServiceProvider.GlobalProvider.GetService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (selectionMonitor == null) return null;
            IntPtr hierarchyPtr, selectionContainerPtr;
            uint itemid;
            IVsMultiItemSelect multiSelect;
            selectionMonitor.GetCurrentSelection(out hierarchyPtr, out itemid, out multiSelect, out selectionContainerPtr);
            if (hierarchyPtr != IntPtr.Zero) {
                IVsHierarchy hierarchy = Marshal.GetObjectForIUnknown(hierarchyPtr) as IVsHierarchy;
                if (hierarchy != null) {
                    try {
                        hierarchy.GetCanonicalName(itemid, out string path);
                        if (File.Exists(path)) return new FileInfo(path).FullName;
                        if (Directory.Exists(path)) return new DirectoryInfo(path).FullName;
                        return path;
                    } catch { }
                }
            }
            var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as EnvDTE.DTE;
            try { 
                string path = dte?.ActiveDocument?.FullName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return new FileInfo(path).FullName;
                return path;
            } catch { return null; }
        }

        private class WebMessageRequest { public string command { get; set; } public string filePath { get; set; } public string snapshotId { get; set; } public string label { get; set; } public string refName { get; set; } }
    }
}
