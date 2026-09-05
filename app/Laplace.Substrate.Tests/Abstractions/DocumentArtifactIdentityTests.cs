using System.Text;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class DocumentArtifactIdentityTests
{
    [Fact]
    public void Metadata_WithEmbeddedSeparators_RemainsDistinctAndRoundTrips()
    {
        var left = new FileMetadata("a", "b\npath=c", 0, DateTime.UnixEpoch);
        var right = new FileMetadata("a\npath=b", "c", 0, DateTime.UnixEpoch);
        byte[] content = Encoding.UTF8.GetBytes("Hello");

        Assert.NotEqual(FileEntity.Resolve(content, left).FileId,
            FileEntity.Resolve(content, right).FileId);
        Assert.Equal(left, FileMetadata.ParseIdentityCanonicalUtf8(left.IdentityCanonicalUtf8()));
        Assert.Equal(right, FileMetadata.ParseIdentityCanonicalUtf8(right.IdentityCanonicalUtf8()));
    }

    [Fact]
    public void SameContent_DifferentPath_SharesContentAndDocumentButNotFile()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("same content, two file occurrences");
        var leftMeta = new FileMetadata(
            "book.txt", "left/book.txt", bytes.Length,
            new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc));
        var rightMeta = leftMeta with { RelativePath = "right/book.txt" };

        FileIdentity left = FileEntity.Resolve(bytes, leftMeta);
        FileIdentity right = FileEntity.Resolve(bytes, rightMeta);

        Assert.Equal(left.ContentRootId, right.ContentRootId);
        Assert.NotEqual(left.MetadataRootId, right.MetadataRootId);
        Assert.NotEqual(left.FileId, right.FileId);
    }

    [Fact]
    public void SizeAndMtime_AreObservations_NotFileIdentity()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("identity is not filesystem observation time");
        var first = new FileMetadata(
            "a.txt", "docs/a.txt", 10,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var later = first with
        {
            SizeBytes = 999999,
            ModifiedUtc = new DateTime(2030, 2, 3, 4, 5, 6, DateTimeKind.Utc),
        };

        FileIdentity a = FileEntity.Resolve(bytes, first);
        FileIdentity b = FileEntity.Resolve(bytes, later);

        Assert.Equal(a.MetadataRootId, b.MetadataRootId);
        Assert.Equal(a.FileId, b.FileId);
        Assert.NotEqual(first.ObservationCanonicalUtf8(), later.ObservationCanonicalUtf8());
    }

    [Fact]
    public void Modality_IsOptionalCanonicalFileIdentityMetadata()
    {
        var text = new FileMetadata("main.py", "src/main.py", 0, DateTime.UnixEpoch);
        var code = text with { Modality = "python" };
        byte[] content = Encoding.UTF8.GetBytes("print('x')");

        Assert.NotEqual(FileEntity.Resolve(content, text).FileId,
            FileEntity.Resolve(content, code).FileId);
        Assert.Equal(code, FileMetadata.ParseIdentityCanonicalUtf8(code.IdentityCanonicalUtf8()));
        Assert.DoesNotContain("\"modality\"", Encoding.UTF8.GetString(text.IdentityCanonicalUtf8()));
    }

    [Fact]
    public async Task FileExtract_PreservesSingletonDocumentIdentityAndSeparateFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-pillar0-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "book.txt");
            byte[] bytes = Encoding.UTF8.GetBytes("Hello");
            await File.WriteAllBytesAsync(path, bytes);

            var records = new List<ContentIngestRecord>();
            await foreach (var record in DocumentFileExtract.OpenAsync(path, "library/book.txt", default))
                records.Add(record);

            var actual = Assert.Single(records);
            Hash128 content = ContentTierSpine.ResolveRoot(bytes)!.Value;
            FileIdentity file = FileEntity.Resolve(bytes, actual.Metadata!.Value);
            Hash128 document = content;

            Assert.Equal(content, actual.ContentRootId);
            Assert.Equal(document, actual.DocumentId);
            Assert.Equal(file.FileId, actual.FileId);
            Assert.Equal(actual.ContentRootId, actual.DocumentId);
            Assert.NotEqual(actual.ContentRootId, actual.FileId);
            Assert.NotEqual(actual.DocumentId, actual.FileId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Handler_ReusesPresentContentAndStagesContainingFile()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("walk from corpus to file to document to content");
        var metadata = new FileMetadata(
            "walk.txt", "docs/walk.txt", bytes.Length,
            new DateTime(2026, 9, 4, 1, 2, 3, DateTimeKind.Utc));
        FileIdentity file = FileEntity.Resolve(bytes, metadata);
        Hash128 document = file.ContentRootId;
        var record = new ContentIngestRecord(
            CanonicalUtf8: bytes,
            SourceId: file.ContentRootId,
            Metadata: metadata,
            ContentRootId: file.ContentRootId,
            DocumentId: document,
            FileId: file.FileId);

        var builder = new SubstrateChangeBuilder(DocumentSource.SourceId, "test/pillar0");
        new DocumentIngestHandler(layerOrder: 2).WalkWitness(
            record, file.ContentRootId, builder, PresentRootDeferredUnit.Instance);
        SubstrateChange change = builder.Build();

        var stagedEntities = CopyTupleParser.ParseEntities(
            change.IntentStages.Select(s => s.TupleBuffer(IntentStageTable.Entities)).ToList());
        var stagedPhysicalities = CopyTupleParser.ParsePhysicalities(
            change.IntentStages.Select(s => s.TupleBuffer(IntentStageTable.Physicalities)).ToList());
        int fileIndex = stagedEntities.Ids.IndexOf(file.FileId);
        Assert.True(fileIndex >= 0);
        Assert.Equal(EntityTypeRegistry.SourceFile, stagedEntities.TypeIds[fileIndex]);
        Assert.True(TextEntityBuilder.TryDecomposeRoot(bytes, out _, out var contentFloor,
            out _, out _, out _, out _));
        Assert.True(TextEntityBuilder.TryDecomposeRoot(metadata.IdentityCanonicalUtf8(),
            out _, out var metadataFloor, out _, out _, out _, out _));
        Assert.Equal((short)(Math.Max(contentFloor, metadataFloor) + 1), stagedEntities.Tiers[fileIndex]);
        Assert.Contains(file.FileId, stagedPhysicalities.EntityIds);
        // Present content is reused; no document-role wrapper or self-trajectory is emitted.
        Assert.DoesNotContain(document, stagedEntities.Ids);
        Assert.DoesNotContain(document, stagedPhysicalities.EntityIds);
        Assert.DoesNotContain(change.Entities, e => e.Id == document);
        Assert.DoesNotContain(change.Physicalities, p => p.EntityId == document);

        Hash128 contains = RelationTypeRegistry.RelationTypeId("CONTAINS");
        Assert.DoesNotContain(change.Attestations,
            a => a.SubjectId == file.FileId && a.TypeId == contains && a.ObjectId == document);

        Hash128 completion = LayerCompletion.RelationTypeId(2);
        Assert.Contains(change.Attestations,
            a => a.SubjectId == file.FileId && a.TypeId == completion && a.SourceId == file.FileId);
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == FileEntity.MetadataRelationTypeId);
    }

    [Fact]
    public void GrammarRoot_StagesBeforeAndComposesIntoContainingFile()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}");
        var source = SubstrateCanonicalIds.OfVersioned("source", "test", "grammar-file");
        var metadata = new FileMetadata("facts.tsv", "facts/facts.tsv", utf8.Length, DateTime.UnixEpoch);

        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(
            utf8, ast, source, "tsv", GrammarCompositionMode.FullSource);
        OrderedCompositionComponent root = composer.RootComponent();
        IntPtr nativeSource = IntPtr.Zero;
        unsafe
        {
            fixed (byte* p = utf8)
            {
                Assert.Equal(0, NativeInterop.GrammarSourceCompose(
                    p, (nuint)utf8.Length, ast.Handle, "tsv", &nativeSource));
            }
            try
            {
                Assert.Equal(NativeInterop.ComposeRootId(nativeSource), root.Id);
            }
            finally
            {
                NativeInterop.ComposeResultFree(nativeSource);
            }
        }
        FileIdentity resolved = FileEntity.Resolve(root, metadata);

        var builder = new SubstrateChangeBuilder(source, "test/grammar-file");
        Assert.Equal(root.Id, composer.DrainInto(builder, 1.0));
        FileIdentity emitted = FileEntity.Emit(builder, source, root, metadata);
        Assert.Equal(resolved, emitted);

        SubstrateChange change = builder.Build();
        var entities = CopyTupleParser.ParseEntities(
            change.IntentStages.Select(s => s.TupleBuffer(IntentStageTable.Entities)).ToList());
        var physicalities = CopyTupleParser.ParsePhysicalities(
            change.IntentStages.Select(s => s.TupleBuffer(IntentStageTable.Physicalities)).ToList());
        Assert.Contains(root.Id, entities.Ids);
        Assert.Contains(root.Id, physicalities.EntityIds);
        Assert.Contains(emitted.FileId, entities.Ids);
        Assert.Contains(emitted.FileId, physicalities.EntityIds);
    }


}
