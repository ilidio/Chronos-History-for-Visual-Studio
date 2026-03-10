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

        public HistoryStorage()
        {
            globalStorageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChronosHistoryVS"
            );
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
                Directory.CreateDirectory(globalStorageRoot);
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

            // Simplified: for now always use global storage or logic to find project root if we had Env context
            string root = globalStorageRoot;
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
            
            // For now, just check global index. In a real VS extension, we'd also check project-local .history
            string indexUri = GetIndexUri(globalStorageRoot);
            var index = await LoadIndexAsync(indexUri);

            return index.snapshots
                .Where(s => GetNormalizedPath(s.filePath) == normalizedPath)
                .OrderByDescending(s => s.timestamp)
                .ToList();
        }

        public async Task<List<Snapshot>> GetAllHistoryAsync()
        {
            await InitAsync();
            string indexUri = GetIndexUri(globalStorageRoot);
            var index = await LoadIndexAsync(indexUri);

            return index.snapshots
                .OrderByDescending(s => s.timestamp)
                .ToList();
        }

        public async Task<string> GetSnapshotContentAsync(Snapshot snapshot)
        {
            string root = globalStorageRoot; // Should be matched with where snapshot was saved
            string fullPath = Path.Combine(root, snapshot.storagePath);
            if (File.Exists(fullPath))
            {
                return await Task.Run(() => File.ReadAllText(fullPath));
            }
            return null;
        }
    }
}
