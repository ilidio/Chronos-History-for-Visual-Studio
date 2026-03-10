using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace ChronosHistoryVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideBindingPath]
    [Guid(ChronosPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(HistoryToolWindow))]
    [ProvideOptionPage(typeof(ChronosOptionsPage), "Chronos History", "AI Settings", 0, 0, true)]
    public sealed class ChronosPackage : AsyncPackage, IVsRunningDocTableEvents
    {
        public const string PackageGuidString = "43717282-5A32-4412-B063-2336D0907B64";

        private HistoryStorage storage;
        private uint rdtCookie;

        public HistoryStorage Storage => storage;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            
            // Set LibGit2Sharp native library path
            try {
                string extensionPath = Path.GetDirectoryName(typeof(ChronosPackage).Assembly.Location);
                string arch = IntPtr.Size == 8 ? "x64" : "x86";
                string nativePath = Path.Combine(extensionPath, "lib", "win32", arch);
                
                System.Diagnostics.Debug.WriteLine($"Chronos: Extension Path: {extensionPath}");
                System.Diagnostics.Debug.WriteLine($"Chronos: Target Native Path: {nativePath}");

                if (Directory.Exists(nativePath)) {
                    LibGit2Sharp.GlobalSettings.NativeLibraryPath = nativePath;
                    System.Diagnostics.Debug.WriteLine("Chronos: LibGit2Sharp Native Path set successfully.");
                } else {
                    System.Diagnostics.Debug.WriteLine("Chronos WARNING: Native path NOT found. Git features may fail.");
                }
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"Chronos ERROR setting native path: {ex.Message}");
            }

            storage = new HistoryStorage();
            await storage.InitAsync();

            // Register for document events
            var rdt = (IVsRunningDocumentTable)await GetServiceAsync(typeof(SVsRunningDocumentTable));
            if (rdt != null)
            {
                rdt.AdviseRunningDocTableEvents(this, out rdtCookie);
            }

            // Register commands
            await ShowHistoryCommand.InitializeAsync(this);
        }

        #region IVsRunningDocTableEvents Members

        public int OnAfterSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var rdt = (IVsRunningDocumentTable)GetService(typeof(SVsRunningDocumentTable));
            if (rdt == null) return VSConstants.S_OK;
            
            // Correct signature for GetDocumentInfo
            rdt.GetDocumentInfo(docCookie, out _, out _, out _, out string filePath, out _, out _, out IntPtr docData);

            // Cast docData (IntPtr) to IVsTextLines
            if (docData != IntPtr.Zero)
            {
                object docDataObject = Marshal.GetObjectForIUnknown(docData);
                if (docDataObject is IVsTextLines textLines && !string.IsNullOrEmpty(filePath))
                {
                    textLines.GetLineCount(out int lineCount);
                    if (lineCount > 0)
                    {
                        // Get the last line's length to specify the end index
                        textLines.GetLengthOfLine(lineCount - 1, out int lastLineLength);
                        textLines.GetLineText(0, 0, lineCount - 1, lastLineLength, out string content);
                        
                        // Fire and forget save
                        _ = storage.SaveSnapshotAsync(filePath, content, "save");
                    }
                }
            }
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint lockType, uint readLocksRemaining, uint editLocksRemaining) => VSConstants.S_OK;
        public int OnAfterFirstDocumentLock(uint docCookie, uint lockType, uint readLocksRemaining, uint editLocksRemaining) => VSConstants.S_OK;
        public int OnBeforeSave(uint docCookie) => VSConstants.S_OK;
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;

        #endregion
    }

    [Guid("7A6B4319-3E9E-4E90-95A3-741E5D57A599")]
    public class HistoryToolWindow : ToolWindowPane
    {
        public HistoryToolWindow() : base(null)
        {
            this.Caption = "Chronos History";
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            var package = this.Package as ChronosPackage;
            this.Content = new HistoryToolWindowControl(package?.Storage ?? new HistoryStorage());
        }
    }
}
