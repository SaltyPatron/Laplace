using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Ingestion;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The upload transport to the Engine: where an upload's bytes are kept and how the Engine is asked to read them as
/// the participant states them. The file's recording (its trunk, its pathname, its readback) is the Engine's and the
/// extension's, checked by the deployed pipeline against a database.
/// </summary>
public sealed class UserUploadsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "laplace-uploads-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("tenant-a", true)]
    [InlineData("3f2a9c1e-0000-4000-8000-000000000001", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void AParticipantNamesADirectoryOfItsOwn(string participant, bool valid)
        => Assert.Equal(valid, UserUploads.IsParticipant(participant));

    [Theory]
    [InlineData("notes/deep/a.txt", true)]
    [InlineData("a.txt", true)]
    [InlineData("/a.txt", false)]
    [InlineData("C:/a.txt", false)]
    [InlineData("a/../b.txt", false)]
    [InlineData("a//b.txt", false)]
    [InlineData("./a.txt", false)]
    [InlineData("", false)]
    public void AnUploadIsNamedByARelativePath(string path, bool valid)
        => Assert.Equal(valid, UserUploads.IsRelativePath(path));

    [Fact]
    public async Task AnUploadIsKeptAtItsPathAndStatedByItsParticipant()
    {
        NativeRecipeRequest? asked = null;
        var uploads = new UserUploads("Host=db;Database=laplace;Username=u", _root, (request, progress, _) =>
        {
            asked = request;
            progress(new NativeRecipeProgress("file-composed", "witness", 1, 1, 5, 3, 3, request.Paths[0], 5, 0,
                "0123456789abcdef0123456789abcdef", null));
            return Task.FromResult((0, ""));
        });
        byte[] bytes = Encoding.UTF8.GetBytes("\ufeffKing\r\n");
        string id = await uploads.AdmitAsync("tenant-a", "notes/deep/a.txt", bytes);

        Assert.Equal("0123456789abcdef0123456789abcdef", id);
        string kept = Path.Combine(_root, "tenant-a", "notes", "deep", "a.txt");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(kept));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(kept)!, "*.partial-*"));
        Assert.NotNull(asked);
        Assert.Equal([Path.GetFullPath(kept)], asked!.Paths);
        Assert.Equal(new NativeRuntimeWitness("tenant-a", "UserPromptContent", Root: Path.Combine(_root, "tenant-a")), asked.Witness);
        Assert.False(asked.ExpandPrerequisites);
        Assert.Contains("dbname='laplace'", asked.ConnectionInfo);
    }

    [Fact]
    public async Task AnUploadTheEngineDidNotRecordIsNeverReportedRecorded()
    {
        var failed = new UserUploads("Host=db;Database=laplace", _root,
            (_, _, _) => Task.FromResult((1, "witness: the database refused")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failed.AdmitAsync("tenant-a", "a.txt", "a"u8.ToArray()));
        Assert.Contains("the database refused", error.Message);

        var silent = new UserUploads("Host=db;Database=laplace", _root, (_, _, _) => Task.FromResult((0, "")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => silent.AdmitAsync("tenant-a", "a.txt", "a"u8.ToArray()));
    }

    [Fact]
    public async Task WhatIsNoUploadIsRefusedBeforeAnythingIsWritten()
    {
        var uploads = new UserUploads("Host=db;Database=laplace", _root,
            (_, _, _) => throw new InvalidOperationException("the Engine was asked"));
        await Assert.ThrowsAsync<ArgumentException>(() => uploads.AdmitAsync("..", "a.txt", "a"u8.ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() => uploads.AdmitAsync("tenant-a", "../a.txt", "a"u8.ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() => uploads.AdmitAsync("tenant-a", "a.txt", []));
        await Assert.ThrowsAsync<DecoderFallbackException>(() => uploads.AdmitAsync("tenant-a", "a.txt", [0xff]));
        Assert.False(Directory.Exists(_root));
    }
}
