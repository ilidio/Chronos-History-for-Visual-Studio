using System;
using System.Collections.Generic;

namespace ChronosHistoryVS
{
    public class Snapshot
    {
        public string id { get; set; }
        public long timestamp { get; set; }
        public string filePath { get; set; }
        public string eventType { get; set; } // "save", "open", "selection", "label", "git"
        public string storagePath { get; set; }
        public string label { get; set; }
        public string description { get; set; }
        public int? linesAdded { get; set; }
        public int? linesDeleted { get; set; }
        // Shared cross-tool field name (matches the VS Code extension & Diff App).
        public bool pinned { get; set; }
        public SelectionRange relevantRange { get; set; }
    }

    public class SelectionRange
    {
        public int startLine { get; set; }
        public int endLine { get; set; }
        public int startColumn { get; set; }
        public int endColumn { get; set; }

        public SelectionRange(int startLine, int endLine, int startColumn = 0, int endColumn = 0)
        {
            this.startLine = startLine;
            this.endLine = endLine;
            this.startColumn = startColumn;
            this.endColumn = endColumn;
        }
    }

    // Per-project metadata, mirrored into the global workspaces.json registry so the
    // Chronos Diff App (and VS Code extension) can discover history written here.
    public class WorkspaceMetadata
    {
        public string id { get; set; }       // generateProjectHash(rootPath)
        public string name { get; set; }     // project folder name
        public string rootPath { get; set; } // absolute path of the project root
        public long lastActivity { get; set; }
    }

    public class HistoryIndex
    {
        public WorkspaceMetadata workspace { get; set; }
        public List<Snapshot> snapshots { get; set; } = new List<Snapshot>();
    }

    public class WorkspaceRegistry
    {
        public List<WorkspaceMetadata> workspaces { get; set; } = new List<WorkspaceMetadata>();
    }

    public class ChronosConfig
    {
        public bool enabled { get; set; } = true;
        public int maxDays { get; set; } = 30;
        public int maxSizeMB { get; set; } = 500;
        public bool trackSelectionHistory { get; set; } = true;
        public List<string> exclude { get; set; } = new List<string>();
        public bool dailyBriefing { get; set; } = true;
        public bool saveInProjectFolder { get; set; } = false;
        public string aiApiKey { get; set; } = "";
        public string aiModel { get; set; } = "models/gemini-1.5-flash";
    }
}
