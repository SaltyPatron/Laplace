using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class ProjectGutenbergMetadataTests
{
    static ProjectGutenbergMetadataTests() => CodepointPerfcache.LoadDefault();

    [Fact]
    public void ClassicHeader_ExtractsDeclaredFieldsAndRawBoundary()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ClassicHeader + "Body text.\n");
        DocumentFormatMetadata metadata = Assert.IsType<DocumentFormatMetadata>(
            ProjectGutenbergMetadata.Extract(bytes));

        Assert.Equal(ProjectGutenbergMetadata.FormatName, metadata.Format);
        Assert.Equal("57532", metadata.EbookId);
        Assert.Equal("Passages from the Life of a Philosopher", metadata.Title);
        Assert.Equal("Charles Babbage", metadata.Author);
        Assert.Equal("English", metadata.Language);
        Assert.Equal("July 18, 2018 [eBook #57532]", metadata.ReleaseDate);
        Assert.Equal("November 9, 2020", metadata.UpdatedDate);
        Assert.Equal("Produced by The Online Distributed Proofreading Team and RichardW", metadata.Credits);
        Assert.Equal("*** START OF THE PROJECT GUTENBERG EBOOK PASSAGES FROM THE LIFE OF A PHILOSOPHER ***",
            metadata.HeaderBoundary);
        Assert.Equal("complete", metadata.HeaderStatus);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(ClassicHeader[..ClassicHeader.IndexOf("*** START", StringComparison.Ordinal)]),
            metadata.HeaderBoundaryByteOffset);
    }

    [Fact]
    public void CurrentSplitKeyHeader_ExtractsMetadataWithoutInventingABoundary()
    {
        DocumentFormatMetadata metadata = Assert.IsType<DocumentFormatMetadata>(
            ProjectGutenbergMetadata.Extract(Encoding.UTF8.GetBytes(SplitHeader)));

        Assert.Equal("76404", metadata.EbookId);
        Assert.Equal("Newton's Principia The mathematical principles of natural philosophy",
            metadata.Title);
        Assert.Equal("Isaac Newton", metadata.Author);
        Assert.Equal("English", metadata.Language);
        Assert.Equal("June 27, 2025 [eBook #76404]", metadata.ReleaseDate);
        Assert.Equal("May 21, 2026", metadata.UpdatedDate);
        Assert.Null(metadata.HeaderBoundary);
        Assert.Null(metadata.HeaderBoundaryByteOffset);
        Assert.Equal("complete", metadata.HeaderStatus);
    }

    [Fact]
    public async Task HeaderMetadata_IsDurableFileIdentityContent_AndRoundTrips()
    {
        string root = Path.Combine(Path.GetTempPath(), $"laplace-gutenberg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "edition.txt");
            await File.WriteAllTextAsync(path, ClassicHeader + "Body text.\n");
            var records = new List<ContentIngestRecord>();
            await foreach (ContentIngestRecord extracted in
                           DocumentFileExtract.OpenAsync(path, "books/edition.txt", default))
                records.Add(extracted);

            FileMetadata metadata = Assert.Single(records).Metadata!.Value;
            Assert.Equal("Passages from the Life of a Philosopher", metadata.FormatMetadata?.Title);
            FileMetadata parsed = FileMetadata.ParseIdentityCanonicalUtf8(
                metadata.IdentityCanonicalUtf8());
            Assert.Equal(metadata.FormatMetadata, parsed.FormatMetadata);
            Assert.Equal(metadata.IdentityCanonicalUtf8(), parsed.IdentityCanonicalUtf8());

            FileMetadata withoutHeader = metadata with { FormatMetadata = null };
            byte[] bytes = await File.ReadAllBytesAsync(path);
            Assert.NotEqual(
                FileEntity.Resolve(bytes, metadata).FileId,
                FileEntity.Resolve(bytes, withoutHeader).FileId);

            ContentIngestRecord record = Assert.Single(records);
            var builder = new SubstrateChangeBuilder(DocumentSource.SourceId, "gutenberg/edition");
            new DocumentIngestHandler(layerOrder: 2).WalkWitness(
                record, record.ContentRootId, builder, unit: null!);
            SubstrateChange change = builder.Build();
            WorkIdentity work = WorkEntity.Resolve(
                metadata.FormatMetadata!.Title!, metadata.FormatMetadata.Author);
            Hash128 expresses = RelationTypeRegistry.Resolve("EXPRESSES").Id;
            Assert.Contains(change.Attestations, row =>
                row.SubjectId == record.FileId && row.TypeId == expresses
                && row.ObjectId == work.WorkId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TwoEditions_ConvergeOnOneNativeWork_AndKeepTwoFileIdentities()
    {
        var firstMetadata = new FileMetadata(
            "first.txt", "edition/first.txt", 10, DateTime.UnixEpoch,
            FormatMetadata: new DocumentFormatMetadata(
                ProjectGutenbergMetadata.FormatName,
                Title: "  Passages  From the Life of a Philosopher ",
                Author: "CHARLES BABBAGE"));
        var secondMetadata = new FileMetadata(
            "second.txt", "edition/second.txt", 20, DateTime.UnixEpoch,
            FormatMetadata: new DocumentFormatMetadata(
                ProjectGutenbergMetadata.FormatName,
                Title: "passages from THE LIFE OF A philosopher",
                Author: "Charles   Babbage"));
        byte[] firstBytes = "first edition body"u8.ToArray();
        byte[] secondBytes = "second edition body with revisions"u8.ToArray();
        FileIdentity firstFile = FileEntity.Resolve(firstBytes, firstMetadata);
        FileIdentity secondFile = FileEntity.Resolve(secondBytes, secondMetadata);
        Assert.NotEqual(firstFile.FileId, secondFile.FileId);

        var firstBuilder = new SubstrateChangeBuilder(DocumentSource.SourceId, "work/first");
        WorkIdentity first = WorkEntity.Emit(
            firstBuilder, firstFile.FileId,
            firstMetadata.FormatMetadata!.Title!, firstMetadata.FormatMetadata.Author);
        SubstrateChange firstChange = firstBuilder.Build();
        var secondBuilder = new SubstrateChangeBuilder(DocumentSource.SourceId, "work/second");
        WorkIdentity second = WorkEntity.Emit(
            secondBuilder, secondFile.FileId,
            secondMetadata.FormatMetadata!.Title!, secondMetadata.FormatMetadata.Author);
        SubstrateChange secondChange = secondBuilder.Build();

        Assert.Equal(first.WorkId, second.WorkId);
        Assert.Equal(first.TitleId, second.TitleId);
        Assert.Equal(first.AuthorId, second.AuthorId);
        Hash128 expresses = RelationTypeRegistry.Resolve("EXPRESSES").Id;
        Assert.Contains(firstChange.Attestations, row =>
            row.SubjectId == firstFile.FileId && row.TypeId == expresses
            && row.ObjectId == first.WorkId);
        Assert.Contains(secondChange.Attestations, row =>
            row.SubjectId == secondFile.FileId && row.TypeId == expresses
            && row.ObjectId == second.WorkId);
        Hash128 hasTitle = RelationTypeRegistry.Resolve("HAS_TITLE").Id;
        Hash128 authoredBy = RelationTypeRegistry.Resolve("AUTHORED_BY").Id;
        Assert.Contains(firstChange.Attestations, row =>
            row.SubjectId == first.WorkId && row.TypeId == hasTitle
            && row.ObjectId == first.TitleId);
        Assert.Contains(firstChange.Attestations, row =>
            row.SubjectId == first.WorkId && row.TypeId == authoredBy
            && row.ObjectId == first.AuthorId);
    }

    [Fact]
    public void MissingAuthor_UsesNativeSingletonFloor_AndMissingTitleEmitsNoWork()
    {
        WorkIdentity anonymous = WorkEntity.Resolve("Anonymous Work", author: null);
        Assert.Equal(anonymous.TitleId, anonymous.WorkId);

        DocumentFormatMetadata markerOnly = Assert.IsType<DocumentFormatMetadata>(
            ProjectGutenbergMetadata.Extract(Encoding.UTF8.GetBytes(
                "*** START OF THE PROJECT GUTENBERG EBOOK 11 ***\nAlice body\n")));
        Assert.Equal("11", markerOnly.EbookId);
        Assert.Null(markerOnly.Title);
        Assert.Null(markerOnly.Author);
    }

    [Fact]
    public void OrdinaryDocumentMentioningProjectGutenberg_IsNotFormatMetadata()
    {
        byte[] ordinary = Encoding.UTF8.GetBytes("""
            Meeting notes

            We compared Project Gutenberg with another archive.
            Title: This is body text, not a source header
            """);

        Assert.Null(ProjectGutenbergMetadata.Extract(ordinary));
    }

    [Fact]
    public void HeaderBeyondBoundedProbe_IsExplicitlyIncomplete()
    {
        string prefix = "The Project Gutenberg eBook of a long fixture\nTitle: Declared Title\n";
        byte[] bytes = Encoding.UTF8.GetBytes(
            prefix + new string(' ', 70 * 1024) + "\nAuthor: Beyond Probe\n");

        DocumentFormatMetadata metadata = Assert.IsType<DocumentFormatMetadata>(
            ProjectGutenbergMetadata.Extract(bytes));
        Assert.Equal("Declared Title", metadata.Title);
        Assert.Null(metadata.Author);
        Assert.Equal("incomplete-probe-limit", metadata.HeaderStatus);
    }

    [SkippableFact]
    public void CuratedEstate_All195ArtifactsHaveExplicitFormatMetadataCoverage()
    {
        const string root = "/vault/Data/ProjectGutenberg/text";
        Skip.IfNot(Directory.Exists(root), "curated Gutenberg mini-collection is not mounted");
        string[] files = Directory.GetFiles(root, "*.txt", SearchOption.TopDirectoryOnly);
        Assert.Equal(195, files.Length);
        var inventory = files
            .Select(path => (Path: path, Metadata: Assert.IsType<DocumentFormatMetadata>(
                ProjectGutenbergMetadata.Extract(File.ReadAllBytes(path)))))
            .ToArray();
        DocumentFormatMetadata[] metadata = inventory.Select(static item => item.Metadata).ToArray();

        Assert.True(metadata.All(item => item.EbookId is not null),
            "missing Gutenberg ID: " + string.Join(", ", inventory
                .Where(static item => item.Metadata.EbookId is null)
                .Select(static item => Path.GetFileName(item.Path))));
        Assert.Equal(187, metadata.Count(item => item.Title is not null));
        Assert.Equal(187, metadata.Count(item => item.Author is not null));
        Assert.Equal(187, metadata.Count(item => item.Language is not null));
        Assert.Equal(187, metadata.Count(item => item.ReleaseDate is not null));
        Assert.Equal(141, metadata.Count(item => item.UpdatedDate is not null));
        Assert.True(metadata.Count(item => item.Credits is not null) == 182,
            "missing Gutenberg credits: " + string.Join(", ", inventory
                .Where(static item => item.Metadata.Credits is null)
                .Select(static item => Path.GetFileName(item.Path))));
        Assert.Equal(194, metadata.Count(item => item.HeaderBoundary is not null));
    }

    private const string ClassicHeader = """
        The Project Gutenberg eBook of Passages from the Life of a Philosopher

        Title: Passages from the Life of a Philosopher

        Author: Charles Babbage

        Release date: July 18, 2018 [eBook #57532]
                Most recently updated: November 9, 2020

        Language: English

        Credits: Produced by The Online Distributed Proofreading Team and
                RichardW

        *** START OF THE PROJECT GUTENBERG EBOOK PASSAGES FROM THE LIFE OF A PHILOSOPHER ***

        """;

    private const string SplitHeader = """
        The Mathematical Principles of Natural Philosophy | Project Gutenberg
        The Project Gutenberg eBook of
        Newton's Principia
        Title
        : Newton's Principia
        The mathematical principles of natural philosophy
        Author
        : Isaac Newton
        Contributor
        : N. W. Chittenden
        Release date
        : June 27, 2025 [eBook #76404]
        Most recently updated: May 21, 2026
        Language
        : English
        Credits
        : Chris Curnow and the Online Distributed Proofreading Team
        SIR ISAAC NEWTON.
        """;
}
