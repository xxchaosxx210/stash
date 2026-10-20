using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using Stash.WinForms;

namespace Stash.SafetyChecks
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            var checks = new List<KeyValuePair<string, Action>>
            {
                Check("existing destination is never overwritten", ExistingDestinationIsPreserved),
                Check("a destination appearing during copy is preserved", DestinationRaceIsPreserved),
                Check("changed source is kept", ChangedSourceIsKept),
                Check("changed destination is kept", ChangedDestinationIsKept),
                Check("copy failure prevents all cleanup", CopyFailurePreventsCleanup),
                Check("safe source folder is deleted after verified copy", SafeCleanup),
                Check("source folder with extra files is kept", ExtraFilesAreKept),
                Check("previous Stash output is excluded from scanning", PreviousOutputIsExcluded),
                Check("ordinary numbered source folder is scanned", NumberedSourceIsIncluded),
                Check("natural sorting, batching, and continued numbering work", SortingBatchingAndNumbering),
                Check("busy UI blocks cleanup, browsing, and custom input", BusyUiIsLocked),
                Check("linked existing output is rejected during scan", LinkedOutputScanIsRejected),
                Check("output linked after scanning is rejected before copying", LinkedOutputPreflightIsRejected),
                Check("output linked between copies prevents cleanup", LinkedOutputDuringCopyIsRejected),
                Check("output linked after verification keeps source", LinkedOutputPreventsCleanup)
            };

            int failures = 0;
            foreach (var check in checks)
            {
                try
                {
                    check.Value();
                    Console.WriteLine("PASS  " + check.Key);
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine("FAIL  " + check.Key + " — " + ex.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine(checks.Count - failures + " passed; " + failures + " failed.");
            return failures == 0 ? 0 : 1;
        }

        private static KeyValuePair<string, Action> Check(string name, Action action)
        {
            return new KeyValuePair<string, Action>(name, action);
        }

        private static T Field<T>(MainForm form, string name) where T : Control
        {
            return (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }

        private static void BusyUiIsLocked()
        {
            using (var form = new MainForm())
            {
                var custom = Field<RadioButton>(form, "customOutput");
                var root = Field<RadioButton>(form, "rootOutput");
                var prefix = Field<TextBox>(form, "customPrefix");
                var path = Field<TextBox>(form, "rootPath");
                var cleanup = Field<CheckBox>(form, "deleteSources");
                var label = Field<Label>(form, "deleteLabel");
                var browse = Field<Button>(form, "browse");
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var setBusy = typeof(MainForm).GetMethod("SetBusy", flags);
                var clickLabel = typeof(Label).GetMethod("OnClick", flags);
                custom.Checked = true;
                Assert(!root.Checked, "Custom did not exclude root option.");
                foreach (bool scanning in new[] { true, false })
                {
                    setBusy.Invoke(form, new object[] { true, scanning });
                    Assert(!browse.Enabled && !cleanup.Enabled && !label.Enabled && !prefix.Enabled,
                        "A settings control remained enabled while busy.");
                    clickLabel.Invoke(label, new object[] { EventArgs.Empty });
                    Assert(!cleanup.Checked, "Cleanup label toggled deletion while busy.");
                    path.Text = scanning ? @"C:\TestImages" : @"C:\OtherImages";
                    Assert(!prefix.Enabled, "Preview refresh re-enabled custom input while busy.");
                }
                setBusy.Invoke(form, new object[] { false, false });
                Assert(browse.Enabled && label.Enabled && cleanup.Enabled && prefix.Enabled,
                    "Settings were not restored after processing.");
                clickLabel.Invoke(label, new object[] { EventArgs.Empty });
                Assert(cleanup.Checked, "Cleanup label does not work while idle.");
                root.Checked = true;
                Assert(!custom.Checked && !prefix.Enabled, "Root option did not disable custom mode.");
            }
        }

        private sealed class TestJunction : IDisposable
        {
            private readonly string path;

            public TestJunction(string path, string target)
            {
                this.path = path;
                var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + path + "\" \"" + target + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(start))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    Assert(process.ExitCode == 0, "Could not create test junction: " + output + error);
                }
            }

            public void Dispose()
            {
                // Remove only the link before the test workspace recursively removes its files.
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(path);
            }
        }

        private static void LinkedOutputScanIsRejected()
        {
            using (var workspace = new TestWorkspace())
            using (var outside = new TestWorkspace())
            {
                string source = workspace.CreateSource("A");
                using (var junction = new TestJunction(Path.Combine(workspace.Root, "Test_Misc_001"), outside.Root))
                {
                    AssertThrows<IOException>(() => workspace.Plan());
                    Assert(Directory.Exists(source), "Source changed during rejected scan.");
                    AssertEqual(0, Directory.GetFiles(outside.Root).Length, "Scan wrote outside root.");
                }
            }
        }

        private static void LinkedOutputPreflightIsRejected()
        {
            using (var workspace = new TestWorkspace())
            using (var outside = new TestWorkspace())
            {
                string source = workspace.CreateSource("A");
                var groups = workspace.Plan();
                using (var junction = new TestJunction(Path.GetDirectoryName(groups[0].Items[0].Destination), outside.Root))
                {
                    AssertThrows<IOException>(() => StashEngine.ProcessGroups(groups, true, null, null));
                    Assert(Directory.Exists(source), "Source was deleted after blocked destination.");
                    AssertEqual(0, Directory.GetFiles(outside.Root).Length, "Copy escaped through junction.");
                }
            }
        }

        private static void LinkedOutputDuringCopyIsRejected()
        {
            using (var workspace = new TestWorkspace())
            using (var outside = new TestWorkspace())
            {
                string first = workspace.CreateSource("A");
                string second = workspace.CreateSource("B");
                var groups = workspace.Plan(1);
                TestJunction junction = null;
                try
                {
                    AssertThrows<IOException>(() => StashEngine.ProcessGroups(groups, true,
                        item => junction = new TestJunction(Path.GetDirectoryName(groups[1].Items[0].Destination), outside.Root), null));
                    Assert(Directory.Exists(first) && Directory.Exists(second), "Cleanup ran after linked-copy failure.");
                    AssertEqual(0, Directory.GetFiles(outside.Root).Length, "Second copy escaped through junction.");
                }
                finally { junction?.Dispose(); }
            }
        }

        private static void LinkedOutputPreventsCleanup()
        {
            using (var workspace = new TestWorkspace())
            {
                string source = workspace.CreateSource("A");
                var groups = workspace.Plan();
                TestJunction junction = null;
                try
                {
                    var summary = StashEngine.ProcessGroups(groups, true, item =>
                    {
                        string output = Path.GetDirectoryName(item.Destination);
                        string moved = Path.Combine(workspace.Root, "MovedOutput");
                        Directory.Move(output, moved);
                        junction = new TestJunction(output, moved);
                    }, null);
                    Assert(Directory.Exists(source), "Source deleted after destination became linked.");
                    AssertEqual(1, summary.SourceFoldersKept, "Protected source was not reported as kept.");
                }
                finally { junction?.Dispose(); }
            }
        }

        private sealed class TestWorkspace : IDisposable
        {
            public string Root { get; private set; }

            public TestWorkspace()
            {
                Root = Path.Combine(Path.GetTempPath(), "stash-csharp-checks-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public string CreateSource(string name, bool extraFile = false)
            {
                string folder = Path.Combine(Root, name);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "image2.jpg"), Bytes("original"));
                if (extraFile)
                    File.WriteAllText(Path.Combine(folder, "note.txt"), "keep");
                return folder;
            }

            public List<CopyGroup> Plan(int batchSize = 500)
            {
                ScanRun run = StashEngine.Scan(
                    Root, 10, batchSize, "Test_Misc", null, null, CancellationToken.None);
                return StashEngine.BuildCopyGroups(Root, run.Rows);
            }

            public void Dispose()
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
        }

        private static byte[] Bytes(string value)
        {
            return System.Text.Encoding.UTF8.GetBytes(value);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(message + " Expected " + expected + ", got " + actual + ".");
        }

        private static void AssertThrows<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private static void ExistingDestinationIsPreserved()
        {
            using (var workspace = new TestWorkspace())
            {
                workspace.CreateSource("A");
                List<CopyGroup> groups = workspace.Plan();
                CopyItem item = groups[0].Items[0];
                Directory.CreateDirectory(Path.GetDirectoryName(item.Destination));
                File.WriteAllBytes(item.Destination, Bytes("existing"));

                AssertThrows<IOException>(() => StashEngine.ProcessGroups(groups, true, null, null));
                AssertEqual("existing", File.ReadAllText(item.Destination), "Existing destination changed.");
                Assert(File.Exists(item.Source), "Source was deleted after the preflight failure.");
            }
        }

        private static void DestinationRaceIsPreserved()
        {
            using (var workspace = new TestWorkspace())
            {
                string source = workspace.CreateSource("A");
                File.WriteAllBytes(Path.Combine(source, "image10.jpg"), Bytes("ten"));
                List<CopyGroup> groups = workspace.Plan();
                CopyItem competingItem = groups[0].Items[1];
                bool created = false;

                AssertThrows<IOException>(() => StashEngine.ProcessGroups(
                    groups, true,
                    copied =>
                    {
                        if (!created)
                        {
                            File.WriteAllBytes(competingItem.Destination, Bytes("competitor"));
                            created = true;
                        }
                    },
                    null));

                AssertEqual("competitor", File.ReadAllText(competingItem.Destination),
                    "The competing destination was overwritten.");
                Assert(groups.SelectMany(group => group.Items).All(item => File.Exists(item.Source)),
                    "A source file was deleted after the copy failed.");
            }
        }

        private static void ChangedSourceIsKept()
        {
            using (var workspace = new TestWorkspace())
            {
                string folder = workspace.CreateSource("A");
                ProcessSummary summary = StashEngine.ProcessGroups(
                    workspace.Plan(), true,
                    item => File.WriteAllBytes(item.Source, Bytes("modified")),
                    null);

                Assert(Directory.Exists(folder), "Changed source folder was deleted.");
                AssertEqual(1, summary.SourceFoldersKept, "Changed source folder was not reported as kept.");
            }
        }

        private static void ChangedDestinationIsKept()
        {
            using (var workspace = new TestWorkspace())
            {
                string folder = workspace.CreateSource("A");
                ProcessSummary summary = StashEngine.ProcessGroups(
                    workspace.Plan(), true,
                    item => File.WriteAllBytes(item.Destination, Bytes("modified")),
                    null);

                Assert(Directory.Exists(folder), "Source folder was deleted after destination changed.");
                AssertEqual(1, summary.SourceFoldersKept, "Changed destination was not reported as kept.");
            }
        }

        private static void CopyFailurePreventsCleanup()
        {
            using (var workspace = new TestWorkspace())
            {
                string first = workspace.CreateSource("A");
                string second = workspace.CreateSource("B");
                bool failed = false;

                AssertThrows<InvalidOperationException>(() => StashEngine.ProcessGroups(
                    workspace.Plan(), true,
                    item =>
                    {
                        if (!failed)
                        {
                            failed = true;
                            throw new InvalidOperationException("injected copy callback failure");
                        }
                    },
                    null));

                Assert(Directory.Exists(first), "First source folder was deleted after the failure.");
                Assert(Directory.Exists(second), "Second source folder was deleted after the failure.");
            }
        }

        private static void SafeCleanup()
        {
            using (var workspace = new TestWorkspace())
            {
                string folder = workspace.CreateSource("A");
                List<CopyGroup> groups = workspace.Plan();
                ProcessSummary summary = StashEngine.ProcessGroups(groups, true, null, null);

                Assert(!Directory.Exists(folder), "Safe source folder still exists.");
                Assert(File.Exists(groups[0].Items[0].Destination), "Verified destination is missing.");
                AssertEqual(1, summary.SourceFoldersDeleted, "Deleted folder count is incorrect.");
            }
        }

        private static void ExtraFilesAreKept()
        {
            using (var workspace = new TestWorkspace())
            {
                string folder = workspace.CreateSource("A", true);
                ProcessSummary summary = StashEngine.ProcessGroups(workspace.Plan(), true, null, null);

                Assert(File.Exists(Path.Combine(folder, "note.txt")), "Extra file was removed.");
                Assert(Directory.Exists(folder), "Source folder with extra content was removed.");
                AssertEqual(1, summary.SourceFoldersKept, "Protected folder was not reported as kept.");
            }
        }

        private static void PreviousOutputIsExcluded()
        {
            using (var workspace = new TestWorkspace())
            {
                string oldOutput = Path.Combine(workspace.Root, "OldCustom_001");
                Directory.CreateDirectory(oldOutput);
                File.WriteAllBytes(Path.Combine(oldOutput, "000001__A__001.jpg"), Bytes("image"));
                AssertEqual(0, workspace.Plan().Count, "Previous Stash output was treated as a source.");
            }
        }

        private static void NumberedSourceIsIncluded()
        {
            using (var workspace = new TestWorkspace())
            {
                workspace.CreateSource("Holiday_001");
                AssertEqual(1, workspace.Plan().Count, "Ordinary numbered folder was skipped.");
            }
        }

        private static void SortingBatchingAndNumbering()
        {
            using (var workspace = new TestWorkspace())
            {
                string firstFolder = workspace.CreateSource("A");
                File.WriteAllBytes(Path.Combine(firstFolder, "image10.jpg"), Bytes("ten"));
                workspace.CreateSource("B");
                workspace.CreateSource("C");
                List<CopyGroup> groups = workspace.Plan(3);

                AssertEqual("image2.jpg", Path.GetFileName(groups[0].Items[0].Source),
                    "Natural sort placed image10 before image2.");
                AssertEqual(Path.GetDirectoryName(groups[0].Items[0].Destination),
                    Path.GetDirectoryName(groups[1].Items[0].Destination),
                    "The next source folder did not stay in the current batch.");
                Assert(Path.GetDirectoryName(groups[1].Items[0].Destination) !=
                    Path.GetDirectoryName(groups[2].Items[0].Destination),
                    "A full batch did not start a new output folder.");

                StashEngine.ProcessGroups(groups, false, null, null);
                List<CopyGroup> repeated = workspace.Plan(3);
                Assert(Path.GetFileName(repeated[0].Items[0].Destination).StartsWith("000005__",
                    StringComparison.Ordinal), "Global numbering did not continue after existing output.");
            }
        }
    }
}
