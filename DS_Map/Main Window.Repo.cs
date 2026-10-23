using Microsoft.WindowsAPICodePack.Dialogs;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DSPRE
{
    /// <summary>
    /// Opening a SourPlatinum decomp repository instead of a ROM. The repo's own tools do the work through WSL:
    /// sourplatinum/tools/dspre_build.py builds the ROM DSPRE opens, and dspre_writeback.py turns the edits DSPRE
    /// saved into the contents folder back into the repo's source files.
    /// </summary>
    public partial class MainProgram
    {
        private const string repoBuildScript = "sourplatinum/tools/dspre_build.py";
        private const string repoWritebackScript = "sourplatinum/tools/dspre_writeback.py";

        private static string RepoRomPath(string repo) => Path.Combine(repo, "build", "dspre", "sourplatinum.nds");
        private static string RepoContentsDir(string repo) => DSUtils.WorkDirPathFromFile(RepoRomPath(repo));

        private string CurrentRepo
        {
            get
            {
                string repo = SettingsManager.Settings.sourPlatinumRepoPath;
                if (!IsProjectLoaded || string.IsNullOrEmpty(repo))
                {
                    return null;
                }
                string loaded = Path.GetFullPath(RomInfo.workDir).TrimEnd('\\');
                return string.Equals(loaded, Path.GetFullPath(RepoContentsDir(repo)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? repo : null;
            }
        }

        private void AddRepositoryMenu()
        {
            ToolStripMenuItem open = new ToolStripMenuItem("Open SourPlatinum Repository...");
            open.Click += delegate { OpenRepositoryPicker(); };

            ToolStripMenuItem writeBack = new ToolStripMenuItem("Write Back to Repository");
            writeBack.Click += delegate { WriteBackToRepository(); };

            ToolStripMenuItem rebuild = new ToolStripMenuItem("Write Back, Rebuild and Reopen");
            rebuild.Click += delegate
            {
                string repo = CurrentRepo;
                if (repo == null)
                {
                    MessageBox.Show("The open project isn't a SourPlatinum repository build.", "Repository", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                OpenRepository(repo, writeBackWithoutAsking: true);
            };

            ToolStripMenuItem menu = new ToolStripMenuItem("Repository");
            menu.DropDownItems.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), writeBack, rebuild });
            menu.DropDownOpening += delegate { writeBack.Enabled = rebuild.Enabled = CurrentRepo != null; };
            menuStrip1.Items.Add(menu);
        }

        private void OpenRepositoryPicker()
        {
            CommonOpenFileDialog picker = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Multiselect = false,
                Title = "Select the SourPlatinum repository folder",
                InitialDirectory = SettingsManager.Settings.sourPlatinumRepoPath,
            };
            if (picker.ShowDialog() != CommonFileDialogResult.Ok)
            {
                return;
            }

            string repo = picker.FileName;
            if (!File.Exists(Path.Combine(repo, repoBuildScript.Replace('/', '\\'))))
            {
                MessageBox.Show($"{repo} doesn't contain {repoBuildScript}.\nSelect the root of the SourPlatinum repository.",
                    "Not a SourPlatinum repository", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SettingsManager.Settings.sourPlatinumRepoPath = repo;
            SettingsManager.Save();
            OpenRepository(repo, writeBackWithoutAsking: false);
        }

        /// <summary>Builds the repo and opens the result, after writing back (or discarding) edits from the last session.</summary>
        private void OpenRepository(string repo, bool writeBackWithoutAsking)
        {
            if (!TryCloseCurrentProject())
            {
                return;
            }

            string contents = RepoContentsDir(repo);
            if (Directory.Exists(contents) && !WriteBackBeforeRebuild(repo, contents, writeBackWithoutAsking))
            {
                return;
            }

            if (RunInWsl(repo, $"python3 -u {repoBuildScript}", "Building SourPlatinum") != 0)
            {
                MessageBox.Show("The build failed, so nothing was opened. The log shows why.", "Build failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                if (Directory.Exists(contents))
                {
                    Directory.Delete(contents, true);
                }
            }
            catch (IOException ex)
            {
                MessageBox.Show($"Couldn't delete the old extracted data:\n{contents}\n\n{ex.Message}", "Repository", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string rom = RepoRomPath(repo);
            if (!DSUtils.UnpackRom(rom, contents))
            {
                Helpers.statusLabelError($"Unpacking of ROM \"{rom}\" has failed");
                return;
            }

            // dspre_writeback.py compares against the baseline ROM; this lets it refuse a baseline from a later build
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            using (FileStream baseline = File.OpenRead(Path.Combine(Path.GetDirectoryName(rom), "sourplatinum.baseline.nds")))
            {
                string hash = BitConverter.ToString(sha1.ComputeHash(baseline)).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(Path.Combine(contents, "dspre_repo_baseline.sha1"), hash + "\n");
            }
            OpenRomFromFolder(contents);
        }

        /// <summary>
        /// The contents folder about to be replaced may hold edits from the last session. Returns false to stop.
        /// </summary>
        private bool WriteBackBeforeRebuild(string repo, string contents, bool writeBackWithoutAsking)
        {
            if (!writeBackWithoutAsking)
            {
                JObject preview = RunWriteback(repo, contents, dryRun: true, showErrors: false);
                if (preview == null)
                {
                    return false;
                }
                if (Count(preview, "errors") > 0)
                {
                    // e.g. the repo was rebuilt outside DSPRE, so these edits can't be told apart from the rebuild
                    return MessageBox.Show(Summary(preview, dryRun: true) +
                        "\n\nThe extracted data from the last session can't be written back. Discard it and rebuild?",
                        "Repository", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                }
                if (Count(preview, "written") == 0 && Count(preview, "notWritten") == 0)
                {
                    return true;
                }

                DialogResult choice = MessageBox.Show(
                    "The extracted data from the last session has edits that aren't in the repository yet:\n\n" +
                    Summary(preview, dryRun: true) +
                    "\n\nWrite them back before rebuilding?\n(No discards them.)",
                    "Unsaved repository edits", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.Cancel)
                {
                    return false;
                }
                if (choice == DialogResult.No)
                {
                    return true;
                }
            }

            JObject report = RunWriteback(repo, contents, dryRun: false);
            if (report == null || Count(report, "errors") > 0)
            {
                return false;
            }
            if (Count(report, "notWritten") > 0)
            {
                return MessageBox.Show(Summary(report, dryRun: false) + "\n\nRebuild anyway and lose the edits that weren't written back?",
                    "Some edits can't be written back", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            }
            return true;
        }

        private void WriteBackToRepository()
        {
            string repo = CurrentRepo;
            if (repo == null)
            {
                MessageBox.Show("The open project isn't a SourPlatinum repository build.", "Repository", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            JObject report = RunWriteback(repo, RomInfo.workDir, dryRun: false);
            if (report != null)
            {
                MessageBox.Show(Summary(report, dryRun: false) + "\n\nEdits in editors you haven't saved yet aren't included.",
                    "Write back", MessageBoxButtons.OK, Count(report, "notWritten") + Count(report, "errors") > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }

        /// <summary>Runs dspre_writeback.py; returns its report, or null if it couldn't run.</summary>
        private JObject RunWriteback(string repo, string contents, bool dryRun, bool showErrors = true)
        {
            string reportPath = Path.Combine(Path.GetTempPath(), "dspre_writeback_report.json");
            File.Delete(reportPath);
            string command = $"python3 -u {repoWritebackScript} {BashQuote(ToWslPath(contents))} --report {BashQuote(ToWslPath(reportPath))}" + (dryRun ? " --dry-run" : "");
            int exitCode = RunInWsl(repo, command, dryRun ? "Checking for edits to write back" : "Writing back to the repository", closeWhenDone: true);
            if (!File.Exists(reportPath))
            {
                MessageBox.Show($"The write-back tool didn't run (exit code {exitCode}).", "Write back", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
            JObject report = JObject.Parse(File.ReadAllText(reportPath));
            if (showErrors && Count(report, "errors") > 0)
            {
                MessageBox.Show(Summary(report, dryRun), "Write back failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return report;
        }

        private static int Count(JObject report, string key) => (report[key] as JArray)?.Count ?? 0;

        private static string Summary(JObject report, bool dryRun)
        {
            StringBuilder sb = new StringBuilder();
            void Section(string key, string title)
            {
                List<string> lines = (report[key] as JArray)?.Select(t => (string)t).ToList() ?? new List<string>();
                if (lines.Count == 0)
                {
                    return;
                }
                sb.AppendLine(title);
                foreach (string line in lines.Take(15))
                {
                    sb.AppendLine("  " + line);
                }
                if (lines.Count > 15)
                {
                    sb.AppendLine($"  ... and {lines.Count - 15} more");
                }
            }
            Section("written", dryRun ? "Would update these source files:" : "Updated these source files:");
            Section("notWritten", "Can't be written back (lost on the next build):");
            Section("errors", "Errors:");
            return sb.Length > 0 ? sb.ToString().TrimEnd() : "Nothing to write back.";
        }

        private static string ToWslPath(string windowsPath)
        {
            string full = Path.GetFullPath(windowsPath);
            return "/mnt/" + char.ToLowerInvariant(full[0]) + full.Substring(2).Replace('\\', '/');
        }

        private static string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

        /// <summary>Runs a command in the repo through WSL, showing its output live. Returns the exit code.</summary>
        private int RunInWsl(string repo, string command, string title, bool closeWhenDone = false)
        {
            string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.Is64BitProcess ? "System32" : "Sysnative", "wsl.exe");
            ProcessStartInfo psi = new ProcessStartInfo(wsl, $"-e bash -lc \"cd {BashQuote(ToWslPath(repo))} && {command.Replace("\"", "\\\"")}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using (Form dialog = new Form())
            using (TextBox log = new TextBox())
            using (Button close = new Button())
            {
                dialog.Text = title;
                dialog.Size = new Size(900, 520);
                dialog.StartPosition = FormStartPosition.CenterParent;
                log.Multiline = true;
                log.ReadOnly = true;
                log.ScrollBars = ScrollBars.Both;
                log.WordWrap = false;
                log.Font = new Font(FontFamily.GenericMonospace, 9f);
                log.Dock = DockStyle.Fill;
                close.Text = "Running...";
                close.Enabled = false;
                close.Dock = DockStyle.Bottom;
                close.Height = 32;
                close.Click += delegate { dialog.Close(); };
                dialog.Controls.Add(log);
                dialog.Controls.Add(close);

                int exitCode = -1;
                bool finished = false;
                dialog.FormClosing += (s, e) => e.Cancel = !finished;
                void Append(string line)
                {
                    if (line != null && !dialog.IsDisposed)
                    {
                        dialog.BeginInvoke((Action)(() => log.AppendText(line + Environment.NewLine)));
                    }
                }

                dialog.Shown += async delegate
                {
                    try
                    {
                        using (Process process = new Process { StartInfo = psi })
                        {
                            process.OutputDataReceived += (s, e) => Append(e.Data);
                            process.ErrorDataReceived += (s, e) => Append(e.Data);
                            process.Start();
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            await Task.Run(() => process.WaitForExit());
                            exitCode = process.ExitCode;
                        }
                    }
                    catch (Exception ex)
                    {
                        Append($"Couldn't run WSL ({wsl}): {ex.Message}");
                    }

                    AppLogger.Info($"{title}: exit code {exitCode}");
                    finished = true;
                    close.Text = exitCode == 0 ? "Done - close" : $"Failed (exit code {exitCode}) - close";
                    close.Enabled = true;
                    if (closeWhenDone && exitCode == 0)
                    {
                        dialog.Close();
                    }
                };

                dialog.ShowDialog(this);
                return exitCode;
            }
        }
    }
}
