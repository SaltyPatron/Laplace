using System.IO;
using System.Linq;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Substrate.Tests.Abstractions;

// Pins GH #608: DocumentDecomposer must share the ONE VendoredPathFilter
// (Laplace.Core) that Code/RepoDecomposer use, so vendored/junk .txt inside an
// ecosystem tree never gets enumerated for document ingest under the corpus's
// identity. Before the fix, EnumerateInputFiles was a bare EnumerateFiles with
// no filter.
public sealed class DocumentEnumerationTests
{
    [Fact]
    public void EnumerateInputFiles_ExcludesVendoredAndBuildTrees_KeepsAuthoredText()
    {
        string root = Path.Combine(Path.GetTempPath(), "laplace608_" + Path.GetRandomFileName());
        try
        {
            // authored content — kept
            Write(root, "doc.txt", "authored prose");
            Write(root, Path.Combine("sub", "chapter.txt"), "more authored prose");
            // vendored / build trees — dropped (segment match)
            Write(root, Path.Combine("external", "core-isl.txt"), "OMW vendored wordlist");
            Write(root, Path.Combine("node_modules", "pkg", "readme.txt"), "third party");
            Write(root, Path.Combine(".venv", "lib", "notes.txt"), "python venv");
            Write(root, Path.Combine("obj", "generated.txt"), "build artifact");

            var got = DocumentDecomposer.EnumerateInputFiles(root)
                .Select(p => Path.GetFileName(p))
                .OrderBy(x => x)
                .ToArray();

            Assert.Equal(new[] { "chapter.txt", "doc.txt" }, got);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateInputFiles_SingleFile_PassesThrough()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace608f_" + Path.GetRandomFileName());
        try
        {
            string file = Write(dir, "only.txt", "one file");
            var got = DocumentDecomposer.EnumerateInputFiles(file).ToArray();
            Assert.Single(got);
            Assert.Equal(Path.GetFullPath(file), got[0]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static string Write(string root, string rel, string content)
    {
        string full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }
}
