using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Stash.WinForms;

namespace Stash.SafetyChecks
{
    internal static class Program
    {
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
                Check("natural sorting, batching, and continued numbering work", SortingBatchingAndNumbering)
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