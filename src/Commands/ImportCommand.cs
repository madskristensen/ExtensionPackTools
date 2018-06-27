﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ExtensionManager;
using Microsoft.VisualStudio.Setup.Configuration;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Task = System.Threading.Tasks.Task;

namespace ExtensionPackTools
{
    internal sealed class ImportCommand
    {
        private readonly Package _package;
        private readonly IVsExtensionManager _manager;

        private ImportCommand(Package package, OleMenuCommandService commandService, IVsExtensionManager manager)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _manager = manager;

            var cmdId = new CommandID(PackageGuids.guidExportPackageCmdSet, PackageIds.ImportCmd);
            var cmd = new MenuCommand(Execute, cmdId);
            commandService.AddCommand(cmd);
        }

        public static ImportCommand Instance { get; private set; }

        private IServiceProvider ServiceProvider
        {
            get { return _package; }
        }

        public static async Task InitializeAsync(AsyncPackage package, OleMenuCommandService commandService)
        {
            var manager = await package.GetServiceAsync(typeof(SVsExtensionManager)) as IVsExtensionManager;
            Instance = new ImportCommand(package, commandService, manager);
        }

        private void Execute(object sender, EventArgs e)
        {
            if (!TryGetFilePath(out string filePath))
            {
                return;
            }

            string file = File.ReadAllText(filePath);
            Manifest manifest = JsonConvert.DeserializeObject<Manifest>(file);

            var dialog = new Importer.ImportWindow(manifest.Extensions, _manager, Importer.Purpose.Import);
            dialog.ShowDialog();

            if (dialog.DialogResult == true)
            {
                List<string> toInstall = dialog.SelectedExtensionIds;

                var repository = ServiceProvider.GetService(typeof(SVsExtensionRepository)) as IVsExtensionRepository;
                IEnumerable<GalleryEntry> marketplaceEntries = repository.GetVSGalleryExtensions<GalleryEntry>(toInstall, 1033, false);
                string tempDir = PrepareTempDir();

                var dte = ServiceProvider.GetService(typeof(DTE)) as DTE;
                dte.StatusBar.Text = "Downloading extensions...";

                HasRootSuffix(out string rootSuffix);

                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await DownloadExtensionAsync(marketplaceEntries, tempDir);
                    dte.StatusBar.Text = "Extensions downloaded. Starting VSIX Installer...";
                    InvokeVsixInstaller(tempDir, rootSuffix);
                });
            }
        }

        private static string PrepareTempDir()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), nameof(ExtensionPackTools));

            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            Directory.CreateDirectory(tempDir);
            return tempDir;
        }

        private void InvokeVsixInstaller(string tempDir, string rootSuffix)
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            string dir = Path.GetDirectoryName(process.MainModule.FileName);
            string exe = Path.Combine(dir, "VSIXInstaller.exe");
            var configuration = new SetupConfiguration() as ISetupConfiguration;
            ISetupInstance instance = configuration.GetInstanceForCurrentProcess();
            IEnumerable<string> vsixFiles = Directory.EnumerateFiles(tempDir, "*.vsix").Select(f => Path.GetFileName(f));

            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{string.Join(" ", vsixFiles)} /instanceIds:{instance.GetInstanceId()}",
                WorkingDirectory = tempDir,
                UseShellExecute = false,
            };

            if (!string.IsNullOrEmpty(rootSuffix))
            {
                start.Arguments += $" /rootSuffix:{rootSuffix}";
            }

            System.Diagnostics.Process.Start(start);
        }

        private async Task DownloadExtensionAsync(IEnumerable<GalleryEntry> entries, string dir)
        {
            var tasks = new List<Task>();

            foreach (GalleryEntry entry in entries)
            {
                string localPath = Path.Combine(dir, Guid.NewGuid() + ".vsix");

                using (var client = new System.Net.WebClient())
                {
                    Task task = client.DownloadFileTaskAsync(entry.DownloadUrl, localPath);
                    tasks.Add(task);
                }
            }

            await Task.WhenAll(tasks);
        }

        private bool TryGetFilePath(out string filePath)
        {
            filePath = null;

            using (var sfd = new OpenFileDialog())
            {
                sfd.DefaultExt = ".vsext";
                sfd.FileName = "extensions";
                sfd.Filter = "VSEXT File|*.vsext";

                DialogResult result = sfd.ShowDialog();

                if (result == DialogResult.OK)
                {
                    filePath = sfd.FileName;
                    return true;
                }
            }

            return false;
        }

        public static bool HasRootSuffix(out string rootSuffix)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            rootSuffix = null;

            if (Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(SVsAppCommandLine)) is IVsAppCommandLine appCommandLine)
            {
                if (ErrorHandler.Succeeded(appCommandLine.GetOption("rootsuffix", out int hasRootSuffix, out rootSuffix)))
                {
                    return hasRootSuffix != 0;
                }
            }

            return false;
        }
    }
}