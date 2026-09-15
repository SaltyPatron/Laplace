using System.Text.Json;
using System.Diagnostics;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Code;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Cli;

internal static partial class IngestCommands
{
    private sealed record NativeCorpusRuntime(string CorePath, string CoreSha256,
        string ManagedCliPath, string ManagedCliSha256, string ManagedCorePath, string ManagedCoreSha256);

    private static NativeCorpusRuntime ObserveCorpusRuntime()
    {
        string core = ObserveLoadedCorePath();
        string cli = typeof(IngestCommands).Assembly.Location;
        string managedCore = typeof(GrammarDecomposer).Assembly.Location;
        return new(core, VerifiedGitRepository.HashFile(core), cli, VerifiedGitRepository.HashFile(cli),
            managedCore, VerifiedGitRepository.HashFile(managedCore));
    }

    internal static string ObserveLoadedCorePath()
    {
        // Resolve the same native import used by admission before asking the OS
        // which artifact backs it. Process.Modules on Linux describes contiguous
        // executable mappings; multiple entries can refer to one library file.
        _ = NativeInterop.LaplaceCoreVersion();
        using var process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>().Where(module =>
        {
            string name = Path.GetFileName(module.FileName);
            return name.Equals("laplace_core.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("liblaplace_core.dylib", StringComparison.Ordinal)
                || name.Equals("liblaplace_core.so", StringComparison.Ordinal)
                || name.StartsWith("liblaplace_core.so.", StringComparison.Ordinal);
        }).Select(module => Path.GetFullPath(module.FileName)).ToArray();
        var paths = modules.Distinct(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (paths.Length != 1)
            throw new InvalidDataException($"The OS loader must identify exactly one loaded Laplace core artifact for corpus provenance; "
                + $"observed {paths.Length} paths in {modules.Length} executable mappings: {string.Join(", ", paths)}");
        return paths[0];
    }

    private static void ValidateGitCorpusOutputPaths(string root, string selection, string receipt)
    {
        string run = Environment.GetEnvironmentVariable("LAPLACE_INGEST_RUN_RECEIPT_PATH")
            ?? throw new InvalidDataException("Verified admission requires a fresh common ingest run receipt path.");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var paths = new[] { selection, receipt, run }.Select(Path.GetFullPath).ToArray();
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length)
            throw new InvalidDataException("Selection, readback and run receipts require distinct paths.");
        foreach (string candidate in paths)
        {
            if (candidate == root || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("All corpus receipt paths must be outside the upstream checkout.");
            for (string? parent = Path.GetDirectoryName(candidate); parent is not null; parent = Path.GetDirectoryName(parent))
                if (new DirectoryInfo(parent).LinkTarget is not null)
                    throw new InvalidDataException("Corpus receipt parents must use real paths, without symbolic links.");
        }
        if (File.Exists(paths[1]) || File.Exists(paths[2]))
            throw new InvalidDataException("Admission requires fresh readback and run receipt paths.");
    }

    private static OrderedCompositionComponent RawSourceRoot(byte[] bytes)
    {
        if (!GrammarSourceFileSupport.IsExactNativeText(bytes))
            throw new InvalidDataException("Raw source is not an exact native text representation.");
        using var tree = ContentTierSpine.BuildTree(bytes)
            ?? throw new InvalidDataException("Raw source did not produce native content.");
        return FileEntity.RootComponent(tree);
    }

    private static async Task WriteVerifiedGitCorpusReceiptAsync(NpgsqlDataSource ds,
        RepoDecomposer decomposer, IngestRunResult result, long observations, long cells, string receiptPath,
        NativeCorpusRuntime runtimeBefore)
    {
        var repository = decomposer.VerifiedRepository!;
        string output = Path.GetFullPath(receiptPath);
        if (output.StartsWith(repository.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Corpus receipts must be outside the upstream checkout.");
        repository.VerifyUnchanged();
        string runReceiptPath = Environment.GetEnvironmentVariable("LAPLACE_INGEST_RUN_RECEIPT_PATH")
            ?? throw new InvalidDataException("Verified admission requires the common ingest run receipt.");
        using var runReceipt = JsonDocument.Parse(await File.ReadAllBytesAsync(runReceiptPath));
        Guid runId = runReceipt.RootElement.GetProperty("run_id").GetGuid();
        var journal = (await NpgsqlIngestOps.VerifiedArtifactJournalAsync(ds, runId, RepoSource.SourceName))
            .ToDictionary(row => row.FileLabel, StringComparer.Ordinal);
        if (journal.Count != repository.Graph.Artifacts.Count || result.FilesDone != repository.Graph.Selected.Count)
            throw new InvalidDataException("Run receipt does not cover the complete declared Git artifact graph.");
        var readbacks = new List<object>();
        int nativeFiles = 0, partialFiles = 0, rawFiles = 0;
        foreach (var artifact in repository.Graph.Artifacts)
        {
            if (!journal.TryGetValue(artifact.FileLabel, out var observed)
                || observed.Disposition != artifact.DispositionName || observed.RelativePath != artifact.RelativePath
                || (artifact.IsSelected ? observed.Status is not ("ok" or "skipped-complete") : observed.Status != "not-selected"))
                throw new InvalidDataException("Artifact journal differs from the declared Git selection: " + artifact.RelativePath);
            if (!artifact.IsSelected) continue;
            var entry = repository.Entries.Single(e => e.Path == artifact.RelativePath);
            byte[] source = repository.ReadVerified(entry);
            // These are the same native grammar/parser and FileEntity carrier as admission.
            // The database then independently reconstructs the stored content DAG.
            bool rawText = entry.Representation == "raw-text";
            using var ast = rawText ? null : GrammarDecomposer.Parse(source, entry.Modality!);
            using var composer = ast is null ? null : new GrammarRowComposer(source, ast, RepoSource.SourceId,
                entry.Modality!, GrammarCompositionMode.FullSource);
            GrammarAstDiagnostics? diagnostics = ast is null ? null : GrammarSourceFileSupport.RequireNativeSourceAst(ast);
            OrderedCompositionComponent content = rawText ? RawSourceRoot(source) : composer!.RootComponent();
            if (rawText) rawFiles++;
            else { nativeFiles++; if (!diagnostics!.Value.SyntaxComplete) partialFiles++; }
            var metadata = GrammarSourceFileSupport.MetadataFromPath(artifact.Path, entry.Path, entry.Modality!);
            FileIdentity file = FileEntity.Resolve(content, metadata);
            if (observed.FileId is not null && !observed.FileId.AsSpan().SequenceEqual(file.FileId.ToBytes()))
                throw new InvalidDataException("Journal file identity differs from native source composition.");
            using var metadataTree = ContentTierSpine.BuildTree(metadata.IdentityCanonicalUtf8())
                ?? throw new InvalidDataException("Metadata native root is absent.");
            var metadataNode = metadataTree.GetNode(metadataTree.NaturalUnitIndex());
            var children = await NpgsqlIngestOps.VerifiedFileCarrierAsync(ds, file.FileId);
            ulong[] expectedFlags = [Trajectory.VertexFlags(content.Tier, content.HasAtom, content.Atom),
                Trajectory.VertexFlags(metadataNode.Tier, metadataNode.Tier == 0, metadataNode.Atom)];
            byte[][] expectedChildren = [content.Id.ToBytes(), file.MetadataRootId.ToBytes()];
            if (children.Count != 2 || children.Where((child, index) => child.Ordinal != index + 1
                || child.RunLength != 1 || (ulong)child.Flags != expectedFlags[index]
                || !child.ChildId.AsSpan().SequenceEqual(expectedChildren[index])).Any())
                throw new InvalidDataException("Canonical Content file carrier differs in type, identity, ordinal, flags, run length or constituent count.");
            byte[] reconstructedMetadata = await NpgsqlContentReconstructor.ReconstructUtf8Async(ds, file.MetadataRootId);
            if (!metadata.IdentityCanonicalUtf8().AsSpan().SequenceEqual(reconstructedMetadata))
                throw new InvalidDataException("Native file metadata reconstruction differs from its exact selected occurrence.");
            byte[] reconstructed = await NpgsqlContentReconstructor.ReconstructUtf8Async(ds, content.Id, rawText ? null : entry.Modality);
            if (!source.AsSpan().SequenceEqual(reconstructed))
                throw new InvalidDataException("Native database reconstruction differs from tracked Git bytes: " + entry.Path);
            readbacks.Add(new { path = entry.Path, file_id = file.FileId.ToString(), content_id = content.Id.ToString(),
                sha256 = VerifiedGitRepository.Hash(reconstructed), bytes = reconstructed.LongLength,
                metadata_sha256 = VerifiedGitRepository.Hash(reconstructedMetadata),
                modality = entry.Modality, representation = entry.Representation, syntax_complete = diagnostics?.SyntaxComplete,
                native_ast_nodes = diagnostics?.AstNodeCount, native_syntax_nodes = diagnostics?.SyntaxNodeCount,
                native_error_nodes = diagnostics?.ErrorNodeCount, native_missing_nodes = diagnostics?.MissingNodeCount,
                native_root_has_error = diagnostics is { } d ? (bool?)(d.RootHasError != 0) : null,
                journal_status = observed.Status });
        }
        var provenanceRoot = decomposer.ProvenanceRoot ?? throw new InvalidDataException("Missing substrate provenance content identity.");
        byte[] provenance = await NpgsqlContentReconstructor.ReconstructUtf8Async(ds, provenanceRoot);
        if (!provenance.AsSpan().SequenceEqual(repository.ProvenanceUtf8))
            throw new InvalidDataException("Native provenance reconstruction differs from the verified Git/build observation.");
        var provenanceEvidence = await NpgsqlConsensusCell.ReadAsync(ds, decomposer.RepositoryId,
            RepoSource.ReferencesTypeId, provenanceRoot)
            ?? throw new InvalidDataException("Repository-to-provenance relation is absent from the native consensus readback.");
        if (provenanceEvidence.WitnessCount < 1)
            throw new InvalidDataException("Repository provenance has no admitted witness.");
        repository.VerifyUnchanged();
        NativeCorpusRuntime runtimeAfter = ObserveCorpusRuntime();
        if (runtimeBefore != runtimeAfter)
            throw new InvalidDataException("Loaded native/managed corpus runtime identity changed during admission/readback.");
        var receipt = new {
            laplace_runtime = runtimeAfter,
            grammar_linkage = "native grammar providers are statically linked into the OS-observed Laplace core; source pins require separate build linkage",
            schema = "laplace.verified-git-corpus-admission.v1", status = "verified", run_id = runId,
            source = RepoSource.SourceName, source_root = repository.Root,
            repository_id = decomposer.RepositoryId.ToString(), provenance_witnesses = provenanceEvidence.WitnessCount,
            provenance_content_id = provenanceRoot.ToString(), provenance_sha256 = VerifiedGitRepository.Hash(provenance),
            provenance = JsonSerializer.Deserialize<JsonElement>(provenance),
            selected_files = readbacks.Count, tracked_entries = journal.Count,
            coverage = new { native_grammar_files = nativeFiles, native_partial_cst_files = partialFiles,
                native_complete_cst_files = nativeFiles - partialFiles,
                native_cpp_files = repository.Entries.Count(e => e.Disposition == "admitted" && e.Modality == "cpp"),
                unadmitted_entries = repository.Entries.Count(e => e.Disposition != "admitted"),
                all_tracked_bytes_roundtripped = repository.Entries.All(e => e.Disposition == "admitted"),
                raw_only_files = rawFiles }, readbacks,
            inserted = new { entities = result.EntitiesInserted, physicalities = result.PhysicalitiesInserted, attestations = result.AttestationsInserted },
            consensus_observations = observations, consensus_cells = cells,
            completed_utc = DateTimeOffset.UtcNow };
        await using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(outputStream, receipt, new JsonSerializerOptions { WriteIndented = true });
    }
}
