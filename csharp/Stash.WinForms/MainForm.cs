using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Stash.WinForms
{
    internal sealed class MainForm : Form
    {
        private readonly TextBox rootPath = new TextBox();
        private readonly NumericUpDown threshold = new NumericUpDown();
        private readonly NumericUpDown batchSize = new NumericUpDown();
        private readonly RadioButton rootOutput = new RadioButton();
        private readonly RadioButton customOutput = new RadioButton();
        private readonly TextBox customPrefix = new TextBox();
        private readonly Label outputExample = new Label();
        private readonly CheckBox deleteSources = new CheckBox();
        private readonly TextBox log = new TextBox();
        private readonly Label statusLabel = new Label();
        private readonly Label summaryLabel = new Label();
        private readonly Button scanButton = new Button();
        private readonly Button stopButton = new Button();
        private CancellationTokenSource cancellation;
        private bool busy;

        public MainForm()
        {
            Text = "Stash";
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 490);
            MinimumSize = new Size(580, 500);
            Font = new Font("Segoe UI", 9F);
            BuildUi();
            RefreshOutputExample(null, EventArgs.Empty);
        }

        private void BuildUi()
        {
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 58,
                Padding = new Padding(14, 8, 14, 4)
            };
            var title = new Label
            {
                Text = "Stash",
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(0, 0)
            };
            var subtitle = new Label
            {
                Text = "Scan, confirm, then copy matching images",
                AutoSize = true,
                Location = new Point(2, 34)
            };
            header.Controls.Add(title);
            header.Controls.Add(subtitle);

            var settings = new GroupBox
            {
                Text = "Stash settings",
                Dock = DockStyle.Top,
                Height = 270,
                Padding = new Padding(10)
            };
            var table = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                ColumnCount = 3,
                RowCount = 5,
                Padding = new Padding(4)
            };
            var cellMargin = new Padding(3, 5, 3, 5);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 5; i++)
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            rootPath.Dock = DockStyle.Fill;
            rootPath.Margin = cellMargin;
            var browse = new Button { Text = "Browse…", AutoSize = true, Anchor = AnchorStyles.Right, Margin = cellMargin };
            browse.Click += Browse;
            table.Controls.Add(new Label { Text = "Root folder:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = cellMargin }, 0, 0);
            table.Controls.Add(rootPath, 1, 0);
            table.Controls.Add(browse, 2, 0);

            threshold.Minimum = 1;
            threshold.Maximum = 999;
            threshold.Value = 10;
            threshold.Width = 74;
            var thresholdFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = cellMargin
            };
            thresholdFlow.Controls.Add(threshold);
            thresholdFlow.Controls.Add(new Label
            {
                Text = "images or fewer",
                AutoSize = true,
                Margin = new Padding(8, 6, 0, 0)
            });
            table.Controls.Add(new Label { Text = "Small-folder threshold:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = cellMargin }, 0, 1);
            table.Controls.Add(thresholdFlow, 1, 1);
            table.SetColumnSpan(thresholdFlow, 2);

            batchSize.Minimum = 1;
            batchSize.Maximum = 100000;
            batchSize.Value = 500;
            batchSize.Width = 74;
            var batchFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = cellMargin
            };
            batchFlow.Controls.Add(batchSize);
            batchFlow.Controls.Add(new Label
            {
                Text = "images (source folders stay together)",
                AutoSize = true,
                Margin = new Padding(8, 6, 0, 0)
            });
            table.Controls.Add(new Label { Text = "Output target:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = cellMargin }, 0, 2);
            table.Controls.Add(batchFlow, 1, 2);
            table.SetColumnSpan(batchFlow, 2);

            var naming = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = cellMargin
            };
            var customRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            rootOutput.Text = "Root folder name + _Misc";
            rootOutput.AutoSize = true;
            rootOutput.Checked = true;
            rootOutput.CheckedChanged += OutputModeChanged;
            customOutput.Text = "Custom:";
            customOutput.AutoSize = true;
            customOutput.CheckedChanged += OutputModeChanged;
            customPrefix.Width = 230;
            customRow.Controls.Add(customOutput);
            customRow.Controls.Add(customPrefix);
            naming.Controls.Add(rootOutput);
            naming.Controls.Add(customRow);
            outputExample.AutoSize = true;
            outputExample.Margin = new Padding(3, 2, 0, 0);
            naming.Controls.Add(outputExample);
            table.Controls.Add(new Label { Text = "Output folder name:", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = cellMargin }, 0, 3);
            table.Controls.Add(naming, 1, 3);
            table.SetColumnSpan(naming, 2);

            deleteSources.Text = string.Empty;
            deleteSources.AccessibleName = "Delete safe source folders after successful copy";
            deleteSources.AutoSize = true;
            deleteSources.Margin = new Padding(0, 4, 0, 0);
            var deleteLabel = new Label
            {
                Text = "Delete safe source folders after successful copy",
                AutoSize = true,
                Margin = new Padding(9, 5, 0, 0),
                Cursor = Cursors.Hand
            };
            deleteLabel.Click += (sender, args) => deleteSources.Checked = !deleteSources.Checked;
            var deleteRow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = cellMargin,
                Padding = Padding.Empty,
                Anchor = AnchorStyles.Left
            };
            deleteRow.Controls.Add(deleteSources);
            deleteRow.Controls.Add(deleteLabel);
            table.Controls.Add(deleteRow, 1, 4);
            table.SetColumnSpan(deleteRow, 2);
            settings.Controls.Add(table);

            var actions = new Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(12, 4, 12, 4)
            };
            scanButton.Text = "Scan";
            scanButton.Size = new Size(76, 28);
            scanButton.Location = new Point(0, 3);
            scanButton.Click += async (sender, args) => await StartScanAsync();
            stopButton.Text = "Stop";
            stopButton.Size = new Size(76, 28);
            stopButton.Location = new Point(84, 3);
            stopButton.Enabled = false;
            stopButton.Click += (sender, args) => cancellation?.Cancel();
            var clear = new Button { Text = "Clear Log", AutoSize = true, Location = new Point(180, 3) };
            clear.Click += (sender, args) => log.Clear();
            var copy = new Button { Text = "Copy Log", AutoSize = true, Location = new Point(270, 3) };
            copy.Click += (sender, args) => CopyLog();
            summaryLabel.AutoSize = true;
            summaryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            summaryLabel.Location = new Point(380, 8);
            actions.Controls.AddRange(new Control[] { scanButton, stopButton, clear, copy, summaryLabel });

            var logGroup = new GroupBox { Text = "Activity log", Dock = DockStyle.Fill, Padding = new Padding(8) };
            log.Multiline = true;
            log.ReadOnly = true;
            log.WordWrap = false;
            log.ScrollBars = ScrollBars.Both;
            log.Font = new Font("Consolas", 9.5F);
            log.Dock = DockStyle.Fill;
            logGroup.Controls.Add(log);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(12, 0, 12, 8) };
            var separator = new Label { BorderStyle = BorderStyle.Fixed3D, Dock = DockStyle.Top, Height = 2 };
            statusLabel.Text = "Ready";
            statusLabel.AutoSize = true;
            statusLabel.Location = new Point(0, 7);
            footer.Controls.Add(separator);
            footer.Controls.Add(statusLabel);

            var workspace = new SplitContainer
            {
                Size = new Size(640, 398),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                FixedPanel = FixedPanel.Panel1,
                SplitterWidth = 6,
                Panel1MinSize = 80,
                Panel2MinSize = 60,
                SplitterDistance = 312,
                BackColor = SystemColors.ControlDark,
                AccessibleName = "Resize settings and activity log"
            };
            workspace.Panel1.BackColor = SystemColors.Control;
            workspace.Panel2.BackColor = SystemColors.Control;
            var settingsScroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            settingsScroll.Controls.Add(settings);
            actions.Dock = DockStyle.Bottom;
            workspace.Panel1.Controls.Add(settingsScroll);
            workspace.Panel1.Controls.Add(actions);
            workspace.Panel2.Controls.Add(logGroup);

            Controls.Add(workspace);
            Controls.Add(footer);
            Controls.Add(header);

            rootPath.TextChanged += RefreshOutputExample;
            customPrefix.TextChanged += RefreshOutputExample;
            FormClosing += (sender, args) => cancellation?.Cancel();
        }

        private void Browse(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "Choose image root folder" })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    rootPath.Text = dialog.SelectedPath;
            }
        }

        private void OutputModeChanged(object sender, EventArgs e)
        {
            // These radio buttons have different layout parents, so enforce one selection.
            if (ReferenceEquals(sender, rootOutput) && rootOutput.Checked)
                customOutput.Checked = false;
            else if (ReferenceEquals(sender, customOutput) && customOutput.Checked)
                rootOutput.Checked = false;

            customPrefix.Enabled = customOutput.Checked;
            RefreshOutputExample(null, EventArgs.Empty);
        }

        private string GetOutputPrefix(string selectedRoot)
        {
            if (rootOutput.Checked)
                return StashEngine.SanitizeOutputPrefix(new DirectoryInfo(selectedRoot).Name + "_Misc");
            string raw = customPrefix.Text.Trim();
            if (raw.Length == 0)
                throw new InvalidOperationException("Enter a custom output folder name.");
            return StashEngine.SanitizeOutputPrefix(raw);
        }

        private void RefreshOutputExample(object sender, EventArgs e)
        {
            string selectedRoot = rootPath.Text.Trim();
            string baseName = "Root";
            if (selectedRoot.Length > 0)
            {
                try { baseName = new DirectoryInfo(selectedRoot).Name; }
                catch { baseName = "Root"; }
            }
            string prefix = rootOutput.Checked
                ? StashEngine.SanitizeOutputPrefix(baseName + "_Misc")
                : StashEngine.SanitizeOutputPrefix(customPrefix.Text);
            outputExample.Text = "Example: " + prefix + "_001, " + prefix + "_002, …";
            customPrefix.Enabled = customOutput.Checked;
        }

        private void SetBusy(bool value, bool scanning)
        {
            busy = value;
            rootPath.Enabled = !value;
            threshold.Enabled = !value;
            batchSize.Enabled = !value;
            rootOutput.Enabled = !value;
            customOutput.Enabled = !value;
            customPrefix.Enabled = !value && customOutput.Checked;
            deleteSources.Enabled = !value;
            scanButton.Enabled = !value;
            stopButton.Enabled = value && scanning;
        }

        private void AppendLog(string text)
        {
            log.AppendText(text + Environment.NewLine);
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        private void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }

        private static string Plural(int count, string singular)
        {
            return count == 1 ? singular : singular + "s";
        }

        private async Task StartScanAsync()
        {
            if (busy) return;
            string root = rootPath.Text.Trim();
            if (root.Length == 0)
            {
                MessageBox.Show(this, "Choose a root folder first.", "Stash", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!Directory.Exists(root))
            {
                MessageBox.Show(this, "The selected root folder does not exist.", "Stash", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int thresholdValue = Decimal.ToInt32(threshold.Value);
            int batchValue = Decimal.ToInt32(batchSize.Value);
            string prefix;
            try { prefix = GetOutputPrefix(root); }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "Stash", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            root = Path.GetFullPath(root);
            log.Clear();
            summaryLabel.Text = "Scanning…";
            statusLabel.Text = "Scanning…";
            cancellation = new CancellationTokenSource();
            SetBusy(true, true);
            AppendLog("STASH — SCAN");
            AppendLog("Root: " + root);
            AppendLog("Match rule: folders containing 1 to " + thresholdValue + " images");
            AppendLog("Output target: " + batchValue + " images");
            AppendLog("Output folder prefix: " + prefix);
            AppendLog("Rule: an original source folder is never split between output folders.");
            AppendLog("Existing " + prefix + "_NNN folders are excluded from source scanning.");
            AppendLog("Delete source folders: " + (deleteSources.Checked ? "YES (safe folders only)" : "NO"));
            AppendLog(new string('-', 84));

            try
            {
                ScanRun run = await Task.Run(() => StashEngine.Scan(
                    root, thresholdValue, batchValue, prefix,
                    existing => Post(() => ShowExistingOutput(existing)),
                    row => Post(() => ShowFolder(row)),
                    cancellation.Token));
                ShowScanSummary(run);
                if (run.Summary.Stopped)
                {
                    statusLabel.Text = "Scan stopped";
                    FinishIdle();
                    return;
                }

                if (run.Summary.QualifyingImages == 0)
                {
                    AppendLog("Scan complete. Nothing to copy.");
                    statusLabel.Text = "Scan complete — no matches";
                    FinishIdle();
                    return;
                }

                List<CopyGroup> groups = StashEngine.BuildCopyGroups(root, run.Rows);
                int filesToCopy = groups.Sum(group => group.Items.Count);
                int outputFolderCount = groups.SelectMany(group => group.Items)
                    .Select(item => Path.GetDirectoryName(item.Destination))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                bool deleteRequested = deleteSources.Checked;
                int deletable = groups.Count(group => group.SafeToDeleteSource);
                int protectedCount = groups.Count - deletable;
                string cleanupText;
                if (deleteRequested)
                {
                    cleanupText = Environment.NewLine + "Source-folder deletion is ON." +
                        Environment.NewLine + deletable + " source folder(s) are currently safe to delete after verified copying.";
                    if (protectedCount > 0)
                        cleanupText += Environment.NewLine + protectedCount +
                            " source folder(s) contain extra content and will be left untouched.";
                }
                else
                {
                    cleanupText = Environment.NewLine + "Source-folder deletion is OFF. Originals will remain.";
                }

                DialogResult confirm = MessageBox.Show(this,
                    "Scan complete." + Environment.NewLine + Environment.NewLine +
                    run.Summary.QualifyingFolders + " matching folder(s)" + Environment.NewLine +
                    filesToCopy + " image(s) will be copied" + Environment.NewLine +
                    outputFolderCount + " output folder(s) will be used" + cleanupText +
                    Environment.NewLine + Environment.NewLine + "Continue?",
                    "Stash — Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes)
                {
                    AppendLog("");
                    AppendLog("Processing cancelled. No files were changed.");
                    statusLabel.Text = "Scan complete — processing cancelled";
                    FinishIdle();
                    return;
                }

                SetBusy(true, false);
                statusLabel.Text = "Copying…";
                AppendLog("");
                AppendLog(new string('=', 84));
                AppendLog("PROCESSING");
                AppendLog("Copying " + filesToCopy + " image(s)…");
                AppendLog(deleteRequested
                    ? "Safe source folders will be removed only after verified copying."
                    : "Original source files and folders will be kept.");
                AppendLog(new string('-', 84));

                ProcessSummary processed = await Task.Run(() =>
                    StashEngine.ProcessGroups(groups, deleteRequested,
                        item => Post(() =>
                        {
                            AppendLog("[COPIED] " + item.Source);
                            AppendLog("         -> " + item.Destination);
                        }),
                        (folder, deleted, reason) => Post(() =>
                            AppendLog(deleted
                                ? "[DELETED SOURCE FOLDER] " + folder
                                : "[KEPT SOURCE FOLDER] " + folder + " — " + (reason ?? "not safe to delete")))));

                AppendLog("");
                AppendLog("Processing complete.");
                AppendLog("Files copied: " + processed.FilesCopied);
                AppendLog("Output folders created: " + processed.OutputFoldersCreated);
                if (deleteRequested)
                {
                    AppendLog("Source folders deleted: " + processed.SourceFoldersDeleted);
                    AppendLog("Source folders kept: " + processed.SourceFoldersKept);
                }
                statusLabel.Text = "Complete";
                summaryLabel.Text = run.Summary.QualifyingFolders + " matches · " +
                    run.Summary.QualifyingImages + " images";
            }
            catch (OperationCanceledException)
            {
                AppendLog("Scan stopped by user. No files were changed.");
                statusLabel.Text = "Scan stopped";
            }
            catch (Exception ex)
            {
                AppendLog("");
                AppendLog("ERROR: " + ex.Message);
                statusLabel.Text = "Operation failed";
                MessageBox.Show(this, ex.Message, "Stash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                FinishIdle();
            }
        }

        private void ShowExistingOutput(ExistingOutputState state)
        {
            if (state.FolderCount == 0)
            {
                AppendLog("Existing " + state.OutputPrefix + " output: none found");
                AppendLog("Next global sequence: 000001");
            }
            else
            {
                AppendLog("Existing " + state.OutputPrefix + " output: " + state.FolderCount +
                    " folder(s), " + state.ImageCount + " image(s)");
                AppendLog("Latest output: " + state.OutputPrefix + "_" + state.LatestIndex.ToString("D3") +
                    " — " + state.LatestImageCount + " image(s)");
                AppendLog("Highest global sequence found: " + state.HighestSequence.ToString("D6"));
                AppendLog("Next global sequence: " + (state.HighestSequence + 1).ToString("D6"));
                if (state.Warnings.Count > 0)
                {
                    AppendLog("Output integrity: WARNING");
                    foreach (string warning in state.Warnings) AppendLog("  - " + warning);
                    AppendLog("Integrity warnings are informational only; Stash will not renumber or repair existing output automatically.");
                }
                else AppendLog("Output integrity: OK");
            }
            AppendLog(new string('-', 84));
        }

        private void ShowFolder(ScanRow row)
        {
            int count = row.Folder.ImageCount;
            if (!row.Qualifies || row.Preview == null)
            {
                AppendLog("[KEEP ] " + row.Folder.RelativePath + " — " + count + " " + Plural(count, "image"));
                return;
            }
            AppendLog("[MATCH] " + row.Folder.RelativePath + " — " + count + " " +
                Plural(count, "image") + " -> " + row.Preview.DestinationFolder);
            foreach (PlannedImage item in row.Preview.Images)
            {
                AppendLog("        " + item.SourceSequence.ToString().PadLeft(3) + ". " + item.SourceName);
                AppendLog("             -> " + item.DestinationFolder + "\\" + item.DestinationName);
            }
        }

        private void ShowScanSummary(ScanRun run)
        {
            ScanSummary summary = run.Summary;
            AppendLog(new string('-', 84));
            AppendLog("Folders scanned: " + summary.FoldersScanned);
            AppendLog("Images found: " + summary.ImagesFound);
            AppendLog("Matching folders: " + summary.QualifyingFolders);
            AppendLog("Images in matching folders: " + summary.QualifyingImages);
            AppendLog(summary.PlannedDestinationFolders.Count > 0
                ? "Planned destinations: " + string.Join(", ", summary.PlannedDestinationFolders)
                : "Planned destinations: none");
            if (summary.Stopped)
                AppendLog("Scan stopped by user. No files were changed.");
            else
                summaryLabel.Text = summary.QualifyingFolders + " matches · " +
                    summary.QualifyingImages + " images";
        }

        private void CopyLog()
        {
            if (log.TextLength == 0) return;
            try
            {
                Clipboard.SetText(log.Text);
                statusLabel.Text = "Log copied to clipboard";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not copy the log: " + ex.Message,
                    "Stash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void FinishIdle()
        {
            SetBusy(false, false);
            cancellation?.Dispose();
            cancellation = null;
        }
    }
}
