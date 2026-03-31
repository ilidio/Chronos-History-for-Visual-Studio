using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChronosHistoryVS
{
    public class HistoryStorage
    {
        private readonly string globalStorageRoot;
        private readonly Dictionary<string, (HistoryIndex index, string root)> indices = new Dictionary<string, (HistoryIndex, string)>();
        private bool initialized = false;

        public Func<string, Task<string>> GetProjectRoot { get; set; }
        public ChronosOptionsPage Settings { get; set; }

        public HistoryStorage()
        {
            globalStorageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChronosHistoryVS"
            );
        }

        private async Task<string> GetRootForFileAsync(string filePath)
        {
            if (Settings != null && Settings.SaveInProjectFolder && GetProjectRoot != null)
            {
                string projectRoot = await GetProjectRoot(filePath);
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    string historyPath = Path.Combine(projectRoot, ".history");
                    if (!Directory.Exists(historyPath))
                    {
                        Directory.CreateDirectory(historyPath);
                    }
                    return historyPath;
                }
            }
            return globalStorageRoot;
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
            return p.Replace("\\", "/").TrimStart('.').TrimStart('/').ToLower();
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
            string json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
            await Task.Run(() => File.WriteAllText(indexUri, json));
        }

        public async Task<Snapshot> SaveSnapshotAsync(string filePath, string content, string eventType, string label = null, string description = null)
        {
            await InitAsync();

            string root = await GetRootForFileAsync(filePath);
            string indexUri = GetIndexUri(root);
            var index = await LoadIndexAsync(indexUri);

            string normalizedRelPath = GetNormalizedPath(filePath);

            var lastSnapshot = index.snapshots
                .Where(s => GetNormalizedPath(s.filePath) == normalizedRelPath)
                .OrderByDescending(s => s.timestamp)
                .FirstOrDefault();

            if (eventType != "label" && lastSnapshot != null)
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

            await Task.Run(() => File.WriteAllText(fullPath, content));

            var snapshot = new Snapshot
            {
                id = id,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                filePath = filePath,
                eventType = eventType,
                storagePath = storagePath,
                label = label,
                description = description
            };

            index.snapshots.Add(snapshot);
            await SaveIndexAsync(index, indexUri);

            return snapshot;
        }

        public async Task<List<Snapshot>> GetHistoryForFileAsync(string filePath)
        {
            await InitAsync();
            string normalizedPath = GetNormalizedPath(filePath);
            
            var results = new List<Snapshot>();

            // Check global storage
            var globalIndex = await LoadIndexAsync(GetIndexUri(globalStorageRoot));
            results.AddRange(globalIndex.snapshots
                .Where(s => GetNormalizedPath(s.filePath) == normalizedPath));

            // Check project storage if enabled
            string root = await GetRootForFileAsync(filePath);
            if (root != globalStorageRoot)
            {
                var localIndex = await LoadIndexAsync(GetIndexUri(root));
                results.AddRange(localIndex.snapshots
                    .Where(s => GetNormalizedPath(s.filePath) == normalizedPath));
            }

            return results
                .GroupBy(s => s.id) // Ensure unique if same snapshot somehow ended up in both (not expected)
                .Select(g => g.First())
                .OrderByDescending(s => s.timestamp)
                .ToList();
        }

        public async Task<List<Snapshot>> GetAllHistoryAsync()
        {
            await InitAsync();
            var results = new List<Snapshot>();

            // Collect snapshots from all loaded indices (this covers both global and any local indices we've encountered)
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

        public async Task<string> GetSnapshotContentAsync(Snapshot snapshot)
        {
            // First check the roots we know about in indices
            foreach (var entry in indices.Values)
            {
                string fullPath = Path.Combine(entry.root, snapshot.storagePath);
                if (File.Exists(fullPath))
                {
                    return await Task.Run(() => File.ReadAllText(fullPath));
                }
            }

            // Fallback: check global storage explicitly
            string globalPath = Path.Combine(globalStorageRoot, snapshot.storagePath);
            if (File.Exists(globalPath))
            {
                return await Task.Run(() => File.ReadAllText(globalPath));
            }

            // Fallback: if we have a file path, check its project-local root
            if (!string.IsNullOrEmpty(snapshot.filePath))
            {
                string root = await GetRootForFileAsync(snapshot.filePath);
                string localPath = Path.Combine(root, snapshot.storagePath);
                if (File.Exists(localPath))
                {
                    return await Task.Run(() => File.ReadAllText(localPath));
                }
            }

            return null;
        }
    }
}
