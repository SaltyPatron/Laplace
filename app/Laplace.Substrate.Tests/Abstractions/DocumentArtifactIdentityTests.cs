using System.Text;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class DocumentArtifactIdentityTests
{
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
        Assert.Equal(DocumentEntity.Resolve(left.ContentRootId), DocumentEntity.Resolve(right.ContentRootId));
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
    public async Task FileExtract_ExposesContentDocumentAndFileIdsSeparately()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-pillar0-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "book.txt");
            byte[] bytes = Encoding.UTF8.GetBytes("one payload; three addressable trunks");
            await File.WriteAllBytesAsync(path, bytes);

            var records = new List<ContentIngestRecord>();
            await foreach (var record in DocumentFileExtract.OpenAsync(path, "library/book.txt", default))
                records.Add(record);

            var actual = Assert.Single(records);
            Hash128 content = ContentTierSpine.ResolveRoot(bytes)!.Value;
            FileIdentity file = FileEntity.Resolve(bytes, actual.Metadata!.Value);
            Hash128 document = DocumentEntity.Resolve(content);

            Assert.Equal(content, actual.ContentRootId);
            Assert.Equal(document, actual.DocumentId);
            Assert.Equal(file.FileId, actual.FileId);
            Assert.NotEqual(actual.ContentRootId, actual.DocumentId);
            Assert.NotEqual(actual.ContentRootId, actual.FileId);
            Assert.NotEqual(actual.DocumentId, actual.FileId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Handler_StagesWalkableFileAndDocumentTrunks_AndCompletesFileId()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("walk from corpus to file to document to content");
        var metadata = new FileMetadata(
            "walk.txt", "docs/walk.txt", bytes.Length,
            new DateTime(2026, 9, 4, 1, 2, 3, DateTimeKind.Utc));
        FileIdentity file = FileEntity.Resolve(bytes, metadata);
        Hash128 document = DocumentEntity.Resolve(file.ContentRootId);
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

        Assert.Contains(change.Entities,
            e => e.Id == file.FileId && e.TypeId == EntityTypeRegistry.SourceFile);
        Assert.Contains(change.Entities,
            e => e.Id == document && e.TypeId == EntityTypeRegistry.Document);

        PhysicalityRow filePhysicality = Assert.Single(change.Physicalities, p => p.EntityId == file.FileId);
        PhysicalityRow documentPhysicality = Assert.Single(change.Physicalities, p => p.EntityId == document);
        Assert.Equal(DocumentSource.SourceId, filePhysicality.SourceId);
        Assert.Equal(file.FileId, documentPhysicality.SourceId);
        Assert.Equal(2, filePhysicality.NConstituents);
        Assert.Equal(1, documentPhysicality.NConstituents);

        Hash128 contains = RelationTypeRegistry.RelationTypeId("CONTAINS");
        Assert.Contains(change.Attestations,
            a => a.SubjectId == file.FileId && a.TypeId == contains && a.ObjectId == document
                && a.SourceId == file.FileId);

        Hash128 completion = LayerCompletion.RelationTypeId(2);
        Assert.Contains(change.Attestations,
            a => a.SubjectId == file.FileId && a.TypeId == completion && a.SourceId == file.FileId);
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == FileEntity.MetadataRelationTypeId);
    }
}
