using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ChronosHistoryVS
{
    public class HistoryStorage
    {
        // Shared cross-tool root: %LOCALAPPDATA%\.chronos-history (Windows) — the same
        // location used by the Chronos VS Code extension and the Chronos Diff App.
        private readonly string globalStorageRoot;
        // Legacy root used by older versions of this extension. Read-only fallback so
        // existing users don't lose access to history captured before the format change.
        private readonly string legacyStorageRoot;
        private readonly Dictionary<string, (HistoryIndex index, string root)> indices = new Dictionary<string, (HistoryIndex, string)>();
        private bool initialized = false;

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public Func<string, Task<string>> GetProjectRoot { get; set; }
        public ChronosOptionsPage Settings { get; set; }

        public HistoryStorage()
        {
            globalStorageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ".chronos-history"
            );
            legacyStorageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChronosHistoryVS"
            );
        }

        // Mirrors the VS Code extension / Diff App hashing so project folder names line up.
        // Equivalent to the JS: hash = ((hash << 5) - hash) + char; truncated to int32.
        private static string GenerateProjectHash(string p)
        {
            int hash = 0;
            if (!string.IsNullOrEmpty(p))
            {
                foreach (char c in p)
                {
                    unchecked { hash = ((hash << 5) - hash) + c; }
                }
            }
            long abs = Math.Abs((long)hash);
            string hex = abs.ToString("x");
            return hex.Length > 8 ? hex.Substring(0, 8) : hex;
        }

        // Resolves the project root for a file (via the host-provided callback), then the
        // storage root + the path of that file relative to the project root.
        private async Task<(string storageRoot, string projectRoot, string relativePath)> GetContextAsync(string filePath)
        {
            string projectRoot = null;
            if (GetProjectRoot != null && !string.IsNullOrEmpty(filePath))
            {
                try { projectRoot = await GetProjectRoot(filePath); } catch { }
            }

            string storageRoot;
            if (Settings != null && Settings.SaveInProjectFolder && !string.IsNullOrEmpty(projectRoot))
            {
                storageRoot = Path.Combine(projectRoot, ".history");
            }
            else if (!string.IsNullOrEmpty(projectRoot))
            {
                string name = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string hash = GenerateProjectHash(projectRoot);
                storageRoot = Path.Combine(globalStorageRoot, $"{name}-{hash}");
            }
            else
            {
                storageRoot = globalStorageRoot;
            }

            string relativePath = MakeRelative(projectRoot, filePath);
            return (storageRoot, projectRoot, relativePath);
        }

        // Path of file relative to root, using forward slashes (matches VS Code's stored form).
        private static string MakeRelative(string root, string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath;
            if (string.IsNullOrEmpty(root)) return Path.GetFileName(filePath);
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullFile = Path.GetFullPath(filePath);
                if (fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = fullFile.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return string.IsNullOrEmpty(rel) ? Path.GetFileName(filePath) : rel.Replace("\\", "/");
                }
            }
            catch { }
            return Path.GetFileName(filePath);
        }

        public async Task ExportHistoryAsync(string destPath)
        {
            await InitAsync();
            if (File.Exists(destPath)) File.Delete(destPath);
            await Task.Run(() => ZipFile.CreateFromDirectory(globalStorageRoot, destPath));
        }

        public async Task ImportHistoryAsync(string zipPath)
        {
            await InitAsync();
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try {
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempDir));

                string importedIndexUri = GetIndexUri(tempDir);
                if (File.Exists(importedIndexUri)) {
                    var importedIndex = await LoadIndexAsync(importedIndexUri);
                    var currentIndex = await LoadIndexAsync(GetIndexUri(globalStorageRoot));

                    foreach (var s in importedIndex.snapshots) {
                        if (!currentIndex.snapshots.Any(cs => cs.id == s.id)) {
                            string src = Path.Combine(tempDir, s.storagePath);
                            string dst = Path.Combine(globalStorageRoot, s.storagePath);
                            if (File.Exists(src)) {
                                File.Copy(src, dst, true);
                                currentIndex.snapshots.Add(s);
                            }
                        }
                    }
                    await SaveIndexAsync(currentIndex, GetIndexUri(globalStorageRoot));
                }
            } finally {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        public async Task InitAsync()
        {
            if (initialized) return;
            if (!Directory.Exists(globalStorageRoot))
            {
                await Task.Run(() => Directory.CreateDirectory(globalStorageRoot));
            }
            initialized = true;
        }

        private string GetNormalizedPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s = p.Replace("\\", "/");
            if (s.StartsWith("./")) s = s.Substring(2);
            return s.TrimStart('/').ToLower();
        }

        // Flexible matcher tolerant of absolute (legacy) vs relative (current) stored paths.
        private bool PathMatches(string storedPath, string normalizedTarget)
        {
            string s = GetNormalizedPath(storedPath);
            if (string.IsNullOrEmpty(normalizedTarget)) return false;
            return s == normalizedTarget
                || s.EndsWith("/" + normalizedTarget)
                || normalizedTarget.EndsWith("/" + s);
        }

        private string GetIndexUri(string root) => Path.Combine(root, "index.json");

        private async Task<HistoryIndex> LoadIndexAsync(string indexUri)
        {
            if (indices.TryGetValue(indexUri, out var entry)) return entry.index;

            string root = Path.GetDirectoryName(indexUri);
            if (File.Exists(indexUri))
            {
                try
                {
                    string json = await Task.Run(() => File.ReadAllText(indexUri));
                    var index = JsonSerializer.Deserialize<HistoryIndex>(json);
                    if (index == null) index = new HistoryIndex();
                    if (index.snapshots == null) index.snapshots = new List<Snapshot>();
                    indices[indexUri] = (index, root);
                    return index;
                }
                catch
                {
                    // Fallback to new index
                }
            }

            var newIndex = new HistoryIndex();
            indices[indexUri] = (newIndex, root);
            return newIndex;
        }

        private async Task SaveIndexAsync(HistoryIndex index, string indexUri)
        {
            string json = JsonSerializer.Serialize(index, WriteOptions);
            string dir = Path.GetDirectoryName(indexUri);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await Task.Run(() => File.WriteAllText(indexUri, json));
        }

        // Tags the local index with project metadata and mirrors it into the global
        // workspaces.json registry that the Diff App scans to discover projects.
        private async Task RegisterWorkspaceAsync(HistoryIndex index, string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return;

            if (index.workspace == null)
            {
                index.workspace = new WorkspaceMetadata
                {
                    id = GenerateProjectHash(projectRoot),
                    name = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    rootPath = projectRoot,
                    lastActivity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            else
            {
                index.workspace.lastActivity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            try
            {
                string registryUri = Path.Combine(globalStorageRoot, "workspaces.json");
                WorkspaceRegistry registry = new WorkspaceRegistry();
                if (File.Exists(registryUri))
                {
                    try
                    {
                        string data = await Task.Run(() => File.ReadAllText(registryUri));
                        registry = JsonSerializer.Deserialize<WorkspaceRegistry>(data) ?? new WorkspaceRegistry();
                        if (registry.workspaces == null) registry.workspaces = new List<WorkspaceMetadata>();
                    }
                    catch { registry = new WorkspaceRegistry(); }
                }

                int existing = registry.workspaces.FindIndex(w => w.id == index.workspace.id || w.rootPath == index.workspace.rootPath);
                if (existing >= 0) registry.workspaces[existing] = index.workspace;
                else registry.workspaces.Add(index.workspace);

                if (!Directory.Exists(globalStorageRoot)) Directory.CreateDirectory(globalStorageRoot);
                string outJson = JsonSerializer.Serialize(registry, WriteOptions);
                await Task.Run(() => File.WriteAllText(registryUri, outJson));
            }
            catch { /* registry is best-effort */ }
        }

        public async Task<Snapshot> SaveSnapshotAsync(string filePath, string content, string eventType, string label = null, string description = null)
        {
            await InitAsync();

            var ctx = await GetContextAsync(filePath);
            string root = ctx.storageRoot;
            string indexUri = GetIndexUri(root);
            var index = await LoadIndexAsync(indexUri);

            string normalizedRelPath = GetNormalizedPath(ctx.relativePath);

            var lastSnapshot = index.snapshots
                .Where(s => GetNormalizedPath(s.filePath) == normalizedRelPath)
                .OrderByDescending(s => s.timestamp)
                .FirstOrDefault();

            if (eventType != "label" && lastSnapshot != null && !string.IsNullOrEmpty(lastSnapshot.storagePath))
            {
                string lastPath = Path.Combine(root, lastSnapshot.storagePath);
                if (File.Exists(lastPath))
                {
                    string lastContent = File.ReadAllText(lastPath);
                    if (lastContent == content) return null;
                }
            }

            string id = Guid.NewGuid().ToString();
            string storagePath = id;
            string fullPath = Path.Combine(root, storagePath);

            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            await Task.Run(() => File.WriteAllText(fullPath, content));

            var snapshot = new Snapshot
            {
                id = id,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                filePath = ctx.relativePath,
                eventType = eventType,
                storagePath = storagePath,
                label = label,
                description = description
            };

            index.snapshots.Add(snapshot);
            await RegisterWorkspaceAsync(index, ctx.projectRoot);
            await SaveIndexAsync(index, indexUri);

            return snapshot;
        }

        public async Task<List<Snapshot>> GetHistoryForFileAsync(string filePath)
        {
            await InitAsync();

            var ctx = await GetContextAsync(filePath);
            // Match against both the relative (current) and absolute (legacy) forms.
            string normalizedRel = GetNormalizedPath(ctx.relativePath);
            string normalizedAbs = GetNormalizedPath(filePath);

            var results = new List<Snapshot>();

            foreach (var root in EnumerateRootsForFile(ctx.storageRoot))
            {
                var index = await LoadIndexAsync(GetIndexUri(root));
                results.AddRange(index.snapshots.Where(s =>
                    PathMatches(s.filePath, normalizedRel) || PathMatches(s.filePath, normalizedAbs)));
            }

            return results
                .GroupBy(s => s.id)
                .Select(g => g.First())
                .OrderByDescending(s => s.timestamp)
                .ToList();
        }

        // Storage roots to consult for a single file: its own project root, the shared
        // global root, and the legacy root (read-only fallback).
        private IEnumerable<string> EnumerateRootsForFile(string storageRoot)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(storageRoot) && seen.Add(storageRoot)) yield return storageRoot;
            if (seen.Add(globalStorageRoot)) yield return globalStorageRoot;
            if (Directory.Exists(legacyStorageRoot) && seen.Add(legacyStorageRoot)) yield return legacyStorageRoot;
        }

        public async Task<List<Snapshot>> GetAllHistoryAsync()
        {
            await InitAsync();
            var results = new List<Snapshot>();

            foreach (var root in EnumerateAllRoots())
            {
                string indexUri = GetIndexUri(root);
                if (File.Exists(indexUri))
                {
                    var index = await LoadIndexAsync(indexUri);
                    results.AddRange(index.snapshots);
                }
            }

            // Include anything already loaded this session (e.g. project .history folders).
            foreach (var entry in indices.Values)
            {
                results.AddRange(entry.index.snapshots);
            }

            return results
                .GroupBy(s => s.id)
                .Select(g => g.First())
                .OrderByDescending(s => s.timestamp)
                .ToList();
        }

        // All roots for a global view: every per-project subfolder under the shared root,
        // the shared root itself, and the legacy root.
        private IEnumerable<string> EnumerateAllRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(globalStorageRoot))
            {
                if (seen.Add(globalStorageRoot)) yield return globalStorageRoot;
                string[] subs = null;
                try { subs = Directory.GetDirectories(globalStorageRoot); } catch { }
                if (subs != null)
                {
                    foreach (var sub in subs)
                        if (seen.Add(sub)) yield return sub;
                }
            }
            if (Directory.Exists(legacyStorageRoot) && seen.Add(legacyStorageRoot)) yield return legacyStorageRoot;
        }

        public async Task<string> GetSnapshotContentAsync(Snapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.storagePath)) return null;

            // Check every root we know about (loaded indices + global + legacy + subfolders).
            foreach (var entry in indices.Values)
            {
                string fullPath = Path.Combine(entry.root, snapshot.storagePath);
                if (File.Exists(fullPath))
                {
                    return await Task.Run(() => File.ReadAllText(fullPath));
                }
            }

            foreach (var root in EnumerateAllRoots())
            {
                string fullPath = Path.Combine(root, snapshot.storagePath);
                if (File.Exists(fullPath))
                {
                    return await Task.Run(() => File.ReadAllText(fullPath));
                }
            }

            return null;
        }
    }
}
