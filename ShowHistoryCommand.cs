using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ChronosHistoryVS
{
    internal sealed class ShowHistoryCommand
    {
        public static readonly Guid CommandSet = new Guid("3a435889-4e0e-4340-985e-9e7f86414c27");

        private readonly AsyncPackage package;

        private ShowHistoryCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            // Register all command IDs
            AddCommand(commandService, 0x0100, this.ExecuteShowHistory);
            AddCommand(commandService, 0x0101, this.ExecuteShowHistorySelection);
            AddCommand(commandService, 0x0102, this.ExecuteShowGitHistory);
            AddCommand(commandService, 0x0103, this.ExecuteGitHistorySelection);
            AddCommand(commandService, 0x0104, this.ExecuteShowProjectHistory);
            AddCommand(commandService, 0x0105, this.ExecuteShowHistoryGraph);
            AddCommand(commandService, 0x0106, this.ExecuteShowRecentChanges);
            AddCommand(commandService, 0x0107, this.ExecuteToggleHeatmap);
            AddCommand(commandService, 0x0108, this.ExecutePutLabel);
            AddCommand(commandService, 0x0109, this.ExecuteGenerateCommitMessage);
            AddCommand(commandService, 0x0110, this.ExecuteExportHistory);
            AddCommand(commandService, 0x0111, this.ExecuteImportHistory);
            AddCommand(commandService, 0x0112, this.ExecuteCompareWithBranch);
        }

        private void AddCommand(OleMenuCommandService commandService, int commandId, EventHandler handler)
        {
            var menuCommandID = new CommandID(CommandSet, commandId);
            var menuItem = new MenuCommand(handler, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static ShowHistoryCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ShowHistoryCommand(package, commandService);
        }

        private void ExecuteShowHistory(object sender, EventArgs e) => OpenToolWindow("history");
        private void ExecuteShowHistorySelection(object sender, EventArgs e) => OpenToolWindow("historySelection");
        private void ExecuteShowGitHistory(object sender, EventArgs e) => OpenToolWindow("gitHistory");
        private void ExecuteGitHistorySelection(object sender, EventArgs e) => OpenToolWindow("gitHistorySelection");
        private void ExecuteShowProjectHistory(object sender, EventArgs e) => OpenToolWindow("projectHistory");
        private void ExecuteShowHistoryGraph(object sender, EventArgs e) => OpenToolWindow("historyGraph");
        private void ExecuteShowRecentChanges(object sender, EventArgs e) => OpenToolWindow("recentChanges");
        private void ExecuteToggleHeatmap(object sender, EventArgs e) => OpenToolWindow("heatmap");
        private void ExecutePutLabel(object sender, EventArgs e) => OpenToolWindow("putLabel");
        private void ExecuteGenerateCommitMessage(object sender, EventArgs e) => OpenToolWindow("generateCommit");
        private void ExecuteExportHistory(object sender, EventArgs e) => OpenToolWindow("export");
        private void ExecuteImportHistory(object sender, EventArgs e) => OpenToolWindow("import");
        private void ExecuteCompareWithBranch(object sender, EventArgs e) => OpenToolWindow("compareWithBranch");

        private void OpenToolWindow(string viewMode)
        {
            this.package.JoinableTaskFactory.RunAsync(async delegate
            {
                var window = await this.package.ShowToolWindowAsync(typeof(HistoryToolWindow), 0, true, this.package.DisposalToken);
                if (window != null && window.Frame != null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var control = window.Content as HistoryToolWindowControl;
                    if (control != null)
                    {
                        await control.SetViewModeAsync(viewMode);
                    }
                    IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;
                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
                }
            });
        }
    }
}
