using System.Collections.Concurrent;
using System.Xml.Linq;
using Xunit;

namespace Laplace.Decomposers.FrameNet.Tests;

public sealed class FrameNetInstalledEstateAuditTests
{
    private static string Estate =>
        Path.Combine(TestIngestPaths.Root, "FrameNet", "framenet_v17");

    [SkippableFact]
    public async Task EveryAdmittedInstalledFrameNetXmlParses()
    {
        Skip.IfNot(Directory.Exists(Estate), $"FrameNet estate not installed at {Estate}");

        var files = new List<(string Path, string Kind)>();
        Add(files, "frame", "*.xml", "frame");
        Add(files, "lu", "lu*.xml", "lu");
        Add(files, "fulltext", "*.xml", "fulltext");
        Assert.NotEmpty(files);
        // Parse the complete admitted physical estate without opening a database writer.
        var failures = new ConcurrentBag<string>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount)),
        };

        await Parallel.ForEachAsync(files, options, async (item, ct) =>
        {
            try
            {
                switch (item.Kind)
                {
                    case "frame":
                    {
                        XDocument doc = XDocument.Load(item.Path, LoadOptions.PreserveWhitespace);
                        if (FrameNetDecomposer.ParseFrame(doc) is null)
                            throw new FormatException("FrameNet frame parser returned null");
                        break;
                    }
                    case "lu":
                    {
                        XDocument doc = XDocument.Load(item.Path, LoadOptions.PreserveWhitespace);
                        string label = $"framenet/lu/{Path.GetFileName(item.Path)}";
                        if (FrameNetLuIngest.ParseLu(doc, label) is null)
                            throw new FormatException("FrameNet LU parser returned null");
                        break;
                    }
                    case "fulltext":
                    {
                        string label = $"framenet/fulltext/{Path.GetFileName(item.Path)}";
                        await foreach (var _ in FrameNetDecomposer.ParseFulltextAsync(
                                           item.Path, label, ct).ConfigureAwait(false))
                        {
                            // Full enumeration is the parse proof; zero annotations is valid.
                        }
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"unknown FrameNet estate kind {item.Kind}");
                }
            }
            catch (Exception error)
            {
                failures.Add(
                    $"{Path.GetRelativePath(Estate, item.Path).Replace('\\', '/')}: " +
                    $"{error.GetType().Name}: {error.Message}");
            }
        });

        Assert.True(
            failures.IsEmpty,
            "Installed FrameNet parser failures:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures.OrderBy(static value => value, StringComparer.Ordinal)));
    }

    private static void Add(
        List<(string Path, string Kind)> files,
        string directory,
        string pattern,
        string kind)
    {
        string root = Path.Combine(Estate, directory);
        if (!Directory.Exists(root)) return;
        files.AddRange(
            Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Select(path => (path, kind)));
    }
}
