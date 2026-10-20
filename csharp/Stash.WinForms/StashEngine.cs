using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace Stash.WinForms
{
    internal sealed class FolderScan
    {
        public string Path { get; set; }
        public string RelativePath { get; set; }
        public string[] ImageNames { get; set; }
        public int SubfolderCount { get; set; }
        public int ImageCount { get { return ImageNames.Length; } }
    }

    internal sealed class ExistingOutputState
    {
        public int FolderCount { get; set; }
        public int ImageCount { get; set; }
        public long LatestIndex { get; set; }
        public int LatestImageCount { get; set; }
        public long HighestSequence { get; set; }
        public string OutputPrefix { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    internal sealed class PlannedImage
    {
        public string SourceName { get; set; }
        public string DestinationFolder { get; set; }
        public string DestinationName { get; set; }
        public long GlobalSequence { get; set; }
        public int SourceSequence { get; set; }
    }

    internal sealed class FolderPreview
    {
        public string DestinationFolder { get; set; }
        public List<PlannedImage> Images { get; set; }
    }

    internal sealed class ScanSummary
    {
        public int FoldersScanned { get; set; }
        public int ImagesFound { get; set; }
        public int QualifyingFolders { get; set; }
        public int QualifyingImages { get; set; }
        public List<string> PlannedDestinationFolders { get; set; } = new List<string>();
        public bool Stopped { get; set; }
    }

    internal sealed class ScanRow
    {
        public FolderScan Folder { get; set; }
        public bool Qualifies { get; set; }
        public FolderPreview Preview { get; set; }
    }

    internal sealed class ScanRun
    {
        public ScanSummary Summary { get; set; }
        public ExistingOutputState Existing { get; set; }
        public List<ScanRow> Rows { get; set; } = new List<ScanRow>();
    }

    internal sealed class CopyItem
    {
        public string Source { get; set; }
        public string Destination { get; set; }
    }

    internal sealed class CopyGroup
    {
        public string SourceFolder { get; set; }
        public List<CopyItem> Items { get; set; }
        public bool SafeToDeleteSource { get; set; }
        public string DeleteBlockReason { get; set; }
    }

    internal sealed class ProcessSummary
    {
        public int FilesCopied { get; set; }
        public int OutputFoldersCreated { get; set; }
        public int SourceFoldersDeleted { get; set; }
        public int SourceFoldersKept { get; set; }
    }

    internal static class StashEngine
    {
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(
            new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly Regex InvalidFilename = new Regex(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);
        private static readonly Regex GlobalSequence = new Regex(@"^(\d+)__", RegexOptions.Compiled);
        private static readonly Regex StashFilename = new Regex(@"^\d+__.+__\d+\.[^.]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> ReservedNames = BuildReservedNames();

        private static HashSet<string> BuildReservedNames()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
            for (int i = 1; i <= 9; i++)
            {
                result.Add("COM" + i);
                result.Add("LPT" + i);
            }
            return result;
        }

        public static string SanitizeOutputPrefix(string value)
        {
            return SanitizeName(value, "Miscellaneous");
        }

        private static string SanitizeFolderName(string value)
        {
            return SanitizeName(value, "Folder");
        }

        private static string SanitizeName(string value, string fallback)
        {
            string cleaned = InvalidFilename.Replace((value ?? "").Trim(), "_");
            cleaned = Regex.Replace(cleaned, @"\s+", "_");
            cleaned = Regex.Replace(cleaned, @"_+", "_");
            cleaned = cleaned.Trim(' ', '.');
            if (cleaned.Length == 0) cleaned = fallback;
            if (ReservedNames.Contains(cleaned)) cleaned += "_";
            if (cleaned.Length > 120) cleaned = cleaned.Substring(0, 120).TrimEnd(' ', '.');
            return cleaned.Length == 0 ? fallback : cleaned;
        }

        private static long? OutputFolderIndex(string name, string prefix)
        {
            Match match = Regex.Match(name, "^" + Regex.Escape(prefix) + @"_(\d+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            long index;
            return long.TryParse(match.Groups[1].Value, out index) ? index : (long?)null;
        }

        private static string OutputFolderName(string prefix, long index)
        {
            return prefix + "_" + index.ToString("D3");
        }

        private static bool IsImage(string path)
        {
            return ImageExtensions.Contains(System.IO.Path.GetExtension(path));
        }

        private static bool LooksLikePreviousOutput(string folder)
        {
            if (!Regex.IsMatch(System.IO.Path.GetFileName(folder), @"^.+_\d+$"))
                return false;
            try
            {
                return Directory.EnumerateFiles(folder).Any(file =>
                    IsImage(file) && Regex.IsMatch(System.IO.Path.GetFileName(file), @"^\d+__.+__\d+\.[^.]+$"));
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private sealed class NumberedFolder
        {
            public long Index;
            public string Path;
        }

        public static ExistingOutputState InspectExistingOutput(string root, string prefix)
        {
            var folders = new List<NumberedFolder>();
            foreach (string child in Directory.EnumerateDirectories(root))
            {
                long? index = OutputFolderIndex(System.IO.Path.GetFileName(child), prefix);
                if (index.HasValue)
                    folders.Add(new NumberedFolder { Index = index.Value, Path = child });
            }
            folders = folders.OrderBy(item => item.Index).ToList();

            int imageCount = 0;
            long highestSequence = 0;
            long latestIndex = 0;
            int latestImageCount = 0;
            var sequenceCounts = new Dictionary<long, int>();
            var unrecognized = new List<string>();
            var warnings = new List<string>();
            var indices = new HashSet<long>(folders.Select(folder => folder.Index));

            if (indices.Count > 0)
            {
                var missingFolders = new List<long>();
                long maximum = indices.Max();
                for (long i = 1; i <= maximum && missingFolders.Count <= 20; i++)
                    if (!indices.Contains(i)) missingFolders.Add(i);
                if (missingFolders.Count > 0)
                    warnings.Add("Missing output folder number(s): " +
                        FormatNumbers(missingFolders, 3, missingFolders.Count > 20));
            }

            foreach (NumberedFolder folder in folders)
            {
                int folderImages = 0;
                string[] files;
                try { files = Directory.GetFiles(folder.Path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    warnings.Add("Could not inspect " + System.IO.Path.GetFileName(folder.Path) + ": " + ex.Message);
                    files = new string[0];
                }

                foreach (string file in files)
                {
                    if (!IsImage(file)) continue;
                    folderImages++;
                    imageCount++;
                    string name = System.IO.Path.GetFileName(file);
                    Match globalMatch = GlobalSequence.Match(name);
                    long sequence;
                    if (globalMatch.Success && long.TryParse(globalMatch.Groups[1].Value, out sequence))
                    {
                        highestSequence = Math.Max(highestSequence, sequence);
                        sequenceCounts[sequence] = sequenceCounts.ContainsKey(sequence)
                            ? sequenceCounts[sequence] + 1 : 1;
                    }
                    if (!StashFilename.IsMatch(name))
                        unrecognized.Add(System.IO.Path.GetFileName(folder.Path) + "\\" + name);
                }

                if (folder.Index >= latestIndex)
                {
                    latestIndex = folder.Index;
                    latestImageCount = folderImages;
                }
            }

            var duplicates = sequenceCounts.Where(pair => pair.Value > 1)
                .Select(pair => pair.Key).OrderBy(value => value).Take(21).ToList();
            if (duplicates.Count > 0)
                warnings.Add("Duplicate global sequence(s): " + FormatNumbers(duplicates, 6, duplicates.Count > 20));

            if (sequenceCounts.Count > 0)
            {
                var missingSequences = new List<long>();
                for (long i = 1; i <= highestSequence && missingSequences.Count <= 20; i++)
                    if (!sequenceCounts.ContainsKey(i)) missingSequences.Add(i);
                if (missingSequences.Count > 0)
                    warnings.Add("Missing global sequence(s): " +
                        FormatNumbers(missingSequences, 6, missingSequences.Count > 20));
            }

            if (unrecognized.Count > 0)
                warnings.Add("Image filename(s) not in Stash format: " +
                    string.Join(", ", unrecognized.Take(5)) + (unrecognized.Count > 5 ? " ..." : ""));

            return new ExistingOutputState
            {
                FolderCount = folders.Count,
                ImageCount = imageCount,
                LatestIndex = latestIndex,
                LatestImageCount = latestImageCount,
                HighestSequence = highestSequence,
                OutputPrefix = prefix,
                Warnings = warnings
            };
        }

        private static string FormatNumbers(IEnumerable<long> values, int width, bool more)
        {
            return string.Join(", ", values.Take(20).Select(value => value.ToString("D" + width))) +
                (more ? " ..." : "");
        }

        private sealed class Planner
        {
            private readonly int batchSize;
            private readonly string prefix;
            private long currentIndex;
            private int currentCount;
            private long nextSequence;
            public readonly List<string> DestinationFolders = new List<string>();

            public Planner(ExistingOutputState existing, int batchSize, string prefix)
            {
                this.batchSize = batchSize;
                this.prefix = prefix;
                currentIndex = existing.LatestIndex == 0 ? 1 : existing.LatestIndex;
                currentCount = existing.LatestIndex == 0 ? 0 : existing.LatestImageCount;
                nextSequence = existing.HighestSequence + 1;
            }

            public FolderPreview Plan(FolderScan folder)
            {
                if (currentCount >= batchSize)
                {
                    currentIndex++;
                    currentCount = 0;
                }

                string destinationFolder = OutputFolderName(prefix, currentIndex);
                if (!DestinationFolders.Contains(destinationFolder, StringComparer.OrdinalIgnoreCase))
                    DestinationFolders.Add(destinationFolder);

                string sourceLabel = SanitizeFolderName(System.IO.Path.GetFileName(folder.Path));
                var images = new List<PlannedImage>();
                for (int i = 0; i < folder.ImageNames.Length; i++)
                {
                    string sourceName = folder.ImageNames[i];
                    string extension = System.IO.Path.GetExtension(sourceName);
                    images.Add(new PlannedImage
                    {
                        SourceName = sourceName,
                        DestinationFolder = destinationFolder,
                        DestinationName = nextSequence.ToString("D6") + "__" + sourceLabel + "__" +
                            (i + 1).ToString("D3") + extension,
                        GlobalSequence = nextSequence,
                        SourceSequence = i + 1
                    });
                    nextSequence++;
                }

                currentCount += folder.ImageCount;
                return new FolderPreview { DestinationFolder = destinationFolder, Images = images };
            }
        }

        public static ScanRun Scan(
            string selectedRoot, int threshold, int batchSize, string prefix,
            Action<ExistingOutputState> onExisting,
            Action<ScanRow> onFolder,
            CancellationToken cancellationToken)
        {
            string root = System.IO.Path.GetFullPath(selectedRoot);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("Root folder does not exist: " + root);
            if (threshold < 1)
                throw new ArgumentOutOfRangeException("threshold", "Threshold must be at least 1.");
            if (batchSize < 1)
                throw new ArgumentOutOfRangeException("batchSize", "Output target size must be at least 1.");
            prefix = SanitizeOutputPrefix(prefix);

            ExistingOutputState existing = InspectExistingOutput(root, prefix);
            if (onExisting != null) onExisting(existing);
            var planner = new Planner(existing, batchSize, prefix);
            var summary = new ScanSummary();
            var rows = new List<ScanRow>();
            var pending = new Stack<string>();
            pending.Push(root);
            bool stopped = false;
            string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            while (pending.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                string current = pending.Pop();
                string[] directories;
                string[] files;
                try
                {
                    directories = Directory.GetDirectories(current);
                    files = Directory.GetFiles(current);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) throw;
                    continue;
                }

                directories = directories.OrderBy(path => Path.GetFileName(path), NaturalStringComparer.Instance).ToArray();
                files = files.OrderBy(path => Path.GetFileName(path), NaturalStringComparer.Instance).ToArray();

                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                {
                    directories = directories.Where(dir =>
                    {
                        string name = Path.GetFileName(dir);
                        if (LooksLikePreviousOutput(dir)) return false;
                        if (OutputFolderIndex(name, prefix).HasValue) return false;
                        if (!string.Equals(prefix, "Miscellaneous", StringComparison.OrdinalIgnoreCase) &&
                            OutputFolderIndex(name, "Miscellaneous").HasValue) return false;
                        return true;
                    }).ToArray();
                }
                else
                {
                    string[] imageNames = files.Select(Path.GetFileName).Where(IsImage).ToArray();
                    string relative = current.Substring(rootPrefix.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var folder = new FolderScan
                    {
                        Path = current,
                        RelativePath = relative,
                        ImageNames = imageNames,
                        SubfolderCount = directories.Length
                    };
                    summary.FoldersScanned++;
                    summary.ImagesFound += folder.ImageCount;
                    bool qualifies = folder.ImageCount >= 1 && folder.ImageCount <= threshold;
                    FolderPreview preview = null;
                    if (qualifies)
                    {
                        summary.QualifyingFolders++;
                        summary.QualifyingImages += folder.ImageCount;
                        preview = planner.Plan(folder);
                        rows.Add(new ScanRow { Folder = folder, Qualifies = true, Preview = preview });
                    }
                    else
                    {
                        rows.Add(new ScanRow { Folder = folder, Qualifies = false, Preview = null });
                    }
                    if (onFolder != null) onFolder(rows[rows.Count - 1]);
                }

                for (int i = directories.Length - 1; i >= 0; i--)
                {
                    try
                    {
                        if ((File.GetAttributes(directories[i]) & FileAttributes.ReparsePoint) == 0)
                            pending.Push(directories[i]);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Skip inaccessible directories as os.walk does.
                    }
                }
            }

            summary.Stopped = stopped;
            summary.PlannedDestinationFolders = planner.DestinationFolders;
            return new ScanRun { Summary = summary, Existing = existing, Rows = rows };
        }

        private sealed class NaturalStringComparer : IComparer<string>
        {
            public static readonly NaturalStringComparer Instance = new NaturalStringComparer();

            public int Compare(string left, string right)
            {
                if (ReferenceEquals(left, right)) return 0;
                if (left == null) return -1;
                if (right == null) return 1;
                int i = 0, j = 0;
                while (i < left.Length && j < right.Length)
                {
                    if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                    {
                        int leftStart = i, rightStart = j;
                        while (i < left.Length && char.IsDigit(left[i])) i++;
                        while (j < right.Length && char.IsDigit(right[j])) j++;
                        string a = left.Substring(leftStart, i - leftStart).TrimStart('0');
                        string b = right.Substring(rightStart, j - rightStart).TrimStart('0');
                        if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                        int numberOrder = string.Compare(a, b, StringComparison.Ordinal);
                        if (numberOrder != 0) return numberOrder;
                    }
                    else
                    {
                        int leftStart = i, rightStart = j;
                        while (i < left.Length && !char.IsDigit(left[i])) i++;
                        while (j < right.Length && !char.IsDigit(right[j])) j++;
                        int textOrder = string.Compare(left.Substring(leftStart, i - leftStart),
                            right.Substring(rightStart, j - rightStart), StringComparison.OrdinalIgnoreCase);
                        if (textOrder != 0) return textOrder;
                    }
                }
                if (i == left.Length && j == right.Length) return 0;
                return i == left.Length ? -1 : 1;
            }
        }

        private static string NormalizePath(string value)
        {
            return System.IO.Path.GetFullPath(value).TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }

        private static bool SourceFolderIsSafe(FolderScan folder, List<CopyItem> items, out string reason)
        {
            reason = null;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(folder.Path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                reason = "could not inspect source folder: " + ex.Message;
                return false;
            }
            if (entries.Any(Directory.Exists))
            {
                reason = "contains one or more subfolders";
                return false;
            }

            var planned = new HashSet<string>(items.Select(item => NormalizePath(item.Source)),
                StringComparer.OrdinalIgnoreCase);
            var actual = new HashSet<string>(entries.Where(File.Exists).Select(NormalizePath),
                StringComparer.OrdinalIgnoreCase);
            if (planned.Except(actual, StringComparer.OrdinalIgnoreCase).Any())
            {
                reason = "one or more planned source files are missing";
                return false;
            }
            if (actual.Except(planned, StringComparer.OrdinalIgnoreCase).Any())
            {
                reason = "contains files that are not part of the copy plan";
                return false;
            }
            return true;
        }

        public static List<CopyGroup> BuildCopyGroups(string root, IEnumerable<ScanRow> rows)
        {
            var groups = new List<CopyGroup>();
            foreach (ScanRow row in rows.Where(item => item.Qualifies && item.Preview != null))
            {
                var items = row.Preview.Images.Select(image => new CopyItem
                {
                    Source = Path.Combine(row.Folder.Path, image.SourceName),
                    Destination = Path.Combine(root, image.DestinationFolder, image.DestinationName)
                }).ToList();
                string reason;
                bool safe = SourceFolderIsSafe(row.Folder, items, out reason);
                groups.Add(new CopyGroup
                {
                    SourceFolder = row.Folder.Path,
                    Items = items,
                    SafeToDeleteSource = safe,
                    DeleteBlockReason = reason
                });
            }
            return groups;
        }

        private static void Preflight(IEnumerable<CopyGroup> groups)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CopyGroup group in groups)
            {
                foreach (CopyItem item in group.Items)
                {
                    if (!File.Exists(item.Source))
                        throw new FileNotFoundException("Source file is missing: " + item.Source, item.Source);
                    string normalized = NormalizePath(item.Destination);
                    if (!seen.Add(normalized))
                        throw new IOException("Duplicate destination in copy plan: " + item.Destination);
                    if (File.Exists(item.Destination) || Directory.Exists(item.Destination))
                        throw new IOException("Destination already exists; nothing was overwritten: " + item.Destination);
                }
            }
        }

        private static byte[] Digest(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(stream);
        }

        private static byte[] VerifiedCopy(string source, string destination)
        {
            using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var targetStream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                sourceStream.CopyTo(targetStream);

            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            if (!File.Exists(destination))
                throw new IOException("Copied file was not found at destination: " + destination);
            if (new FileInfo(source).Length != new FileInfo(destination).Length)
                throw new IOException("Copy verification failed because file sizes differ: " + source + " -> " + destination);

            byte[] copiedDigest = Digest(destination);
            if (!Digest(source).SequenceEqual(copiedDigest))
                throw new IOException("Copy verification failed because contents differ: " + source);
            return copiedDigest;
        }

        public static ProcessSummary ProcessGroups(
            IEnumerable<CopyGroup> groups, bool deleteSourceFolders, Action<CopyItem> onCopy,
            Action<string, bool, string> onSourceResult)
        {
            List<CopyGroup> checkedGroups = groups.ToList();
            Preflight(checkedGroups);
            var createdOutputFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var verifiedDigests = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var summary = new ProcessSummary();

            foreach (CopyGroup group in checkedGroups)
            {
                foreach (CopyItem item in group.Items)
                {
                    string folder = Path.GetDirectoryName(item.Destination);
                    if (!Directory.Exists(folder))
                    {
                        Directory.CreateDirectory(folder);
                        createdOutputFolders.Add(folder);
                    }
                    verifiedDigests[item.Source] = VerifiedCopy(item.Source, item.Destination);
                    summary.FilesCopied++;
                    if (onCopy != null) onCopy(item);
                }
            }

            if (deleteSourceFolders)
            {
                foreach (CopyGroup group in checkedGroups)
                {
                    if (!group.SafeToDeleteSource)
                    {
                        summary.SourceFoldersKept++;
                        if (onSourceResult != null)
                            onSourceResult(group.SourceFolder, false,
                                group.DeleteBlockReason ?? "source folder was not safe to delete");
                        continue;
                    }

                    string[] entries;
                    try { entries = Directory.GetFileSystemEntries(group.SourceFolder); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        summary.SourceFoldersKept++;
                        if (onSourceResult != null)
                            onSourceResult(group.SourceFolder, false, "could not re-check source folder: " + ex.Message);
                        continue;
                    }

                    var currentFiles = new HashSet<string>(
                        entries.Where(File.Exists).Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
                    var plannedSources = new HashSet<string>(
                        group.Items.Select(item => NormalizePath(item.Source)), StringComparer.OrdinalIgnoreCase);
                    bool hasSubfolders = entries.Any(Directory.Exists);
                    if (hasSubfolders || !currentFiles.SetEquals(plannedSources))
                    {
                        summary.SourceFoldersKept++;
                        if (onSourceResult != null)
                            onSourceResult(group.SourceFolder, false,
                                "folder contents changed or extra content was found");
                        continue;
                    }

                    bool contentsUnchanged;
                    try
                    {
                        contentsUnchanged = group.Items.All(item =>
                            Digest(item.Source).SequenceEqual(verifiedDigests[item.Source]) &&
                            Digest(item.Destination).SequenceEqual(verifiedDigests[item.Source]));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        contentsUnchanged = false;
                    }

                    if (!contentsUnchanged)
                    {
                        summary.SourceFoldersKept++;
                        if (onSourceResult != null)
                            onSourceResult(group.SourceFolder, false,
                                "source or destination contents changed or could not be verified");
                        continue;
                    }

                    foreach (CopyItem item in group.Items) File.Delete(item.Source);
                    Directory.Delete(group.SourceFolder, false);
                    summary.SourceFoldersDeleted++;
                    if (onSourceResult != null) onSourceResult(group.SourceFolder, true, null);
                }
            }

            summary.OutputFoldersCreated = createdOutputFolders.Count;
            return summary;
        }
    }
}