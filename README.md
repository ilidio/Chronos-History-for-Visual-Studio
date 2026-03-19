# Chronos History for Visual Studio

Chronos History is a professional-grade time-travel extension for Visual Studio that tracks your file changes automatically and provides advanced Git integration. Never lose a line of code again.

## 🚀 Key Features

### 🕒 Smart Timelines
- **Local History:** Automatic snapshots captured every time you save (**Ctrl+S**).
- **History for Selection:** Precision tracking. Highlight a block of code to see its specific evolution over time, even as lines shift.
- **Project History:** A global "Activity Feed" showing all modifications across every file in your project.
- **Recent Changes:** Quick access to your last 50 modifications for rapid context switching.

### 🌿 Advanced Git Integration
- **Show Git History:** Full file-level commit history directly in the Chronos view.
- **Git History for Selection:** View only the commits that modified your currently selected lines.
- **Compare with Branch:** Professional side-by-side comparison between your current file and any branch (Local or Remote/Origin).
- **Failsafe Core:** Seamlessly switches between high-performance **LibGit2Sharp** and the system **Git CLI**, ensuring 100% reliability on Windows.

### 📊 Powerful Visualizations
- **Interactive History Graph:** A beautiful, node-edge visualization using `vis-network.js`. See how your files and labels relate over time.
- **Code Heatmap:** Direct editor decorations. Colors lines by "age" (Red < 24h, Orange < 1w, Yellow < 1m) based on Git blame.

### 🤖 AI & Utilities
- **AI Commit Messages:** Generates professional, Conventional Commit drafts based on your recent local history using **Google Gemini AI**.
- **Put Label:** Create manual "named checkpoints" for important milestones.
- **Export/Import:** Backup or restore your entire local history as a portable ZIP file.

## 🛠 Context Menu Overview

Access all features by right-clicking in your code editor or Solution Explorer:

```text
Chronos History
 ├─ Show History              (File-specific timeline)
 ├─ Show History for Selection (History for selected lines)
 ├───────────────────────────
 ├─ Show Git History          (Git commits for current file)
 ├─ Git History for Selection  (Git commits for selected lines)
 ├─ Compare with Branch...     (Side-by-side with other branches)
 ├───────────────────────────
 ├─ Show Project History      (Global timeline of all changes)
 ├─ Show History Graph        (Interactive node-edge visualization)
 ├─ Show Recent Changes       (Quick view of latest activity)
 ├─ Toggle Code Heatmap       (Line-level activity visualization)
 ├─ Put Label                 (Create a named checkpoint)
 ├─ Generate Commit Message   (AI draft based on local history)
 ├───────────────────────────
 ├─ Export History            (Backup to ZIP)
 ├─ Import History            (Restore from ZIP)
```

## ⚙️ Configuration

Configure the AI and other settings in **Tools -> Options -> Chronos History -> AI Settings**:
- **Gemini API Key:** Your secret key from [Google AI Studio](https://aistudio.google.com/).
- **Model:** Choose your preferred model (Default: `gemini-2.0-flash`).
- **Language:** Set the language for generated commit messages.

## 📦 Installation & Requirements
- **Visual Studio:** 2022 (Community, Pro, or Enterprise).
- **Environment:** Works on Windows x64, x86, and ARM64.
- **Git:** Works best with Git installed in your system PATH.

## 🏗 Architecture
- **Frontend:** Responsive HTML5/JavaScript rendered via **Microsoft WebView2**.
- **Backend:** C# / .NET Framework 4.7.2.
- **Native Power:** Uses direct Win32 DLLs for performance with automatic CLI fallback for compatibility.

## 🤝 Integrations

### Chronos History Diff Desktop App
Enhance your history tracking experience with the **[Chronos History Diff Desktop App](https://github.com/ilidio/Chronos-History-Diff-App)**. This standalone application works in synergy with the Visual Studio extension to provide:
*   A dedicated, high-performance desktop interface for navigating Chronos local history.
*   Editable side-by-side Monaco diffs for deep version comparison.
*   Advanced comparison tools and full-screen visualization of your local history.

Use it standalone or pair it with this Visual Studio extension to unlock local file snapshots and leverage a powerful comparison environment.

## 📄 License
MIT
