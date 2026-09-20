using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Unicode;

/// <summary>
/// Unicode/UCD ingestion at physical-artifact grain. No source is preloaded during
/// Initialize. The Tier-0 floor is generated from the selected complete UCD XML + DUCET
/// inputs by one native source snapshot and persisted through the ordinary working-set/COPY
/// writer. Tier-0 admission never reads the installed T0 perfcache; runtime acceleration is
/// unavailable until the floor persistence barrier. After that barrier, independent property artifacts run
/// through the shared bounded artifact executor.
/// </summary>
public sealed class UnicodeDecomposer
    : DecomposerMultiPhase<UnicodeSource, FullScope>, IIngestInventoryProvider, IIngestArtifactGraphProvider
{
    public static readonly Hash128 Source = UnicodeSource.SourceId;
    public static readonly Hash128 TrustClass = UnicodeSource.TrustClass;
    public static readonly Hash128 CodepointType = EntityTypeRegistry.Codepoint;

    private readonly string? _ucdxmlZip;
    private readonly string? _ducet;
    private readonly ConcurrentStringSet _canonicalNames = new(StringComparer.Ordinal);
    private readonly ConcurrentIdSet _ucdPropertyTypes = new();
    private readonly ConcurrentIdSet _ucdPropertyDeclarations = new();
    private readonly LaplaceCookbook _cookbook = LaplaceCookbook.Shared;
    private UcdXmlRecipe? _ucdXmlRecipe;

    public UnicodeDecomposer(string? ucdxmlZip = null, string? ducet = null)
    {
        _ucdxmlZip = ucdxmlZip;
        _ducet = ducet;
    }

    public override int LayerOrder => 0;

    protected override Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        // Source vocabulary bootstrap is metadata-only. Physical source files are not
        // opened until their claimed RunPhaseAsync worker begins.
        return Task.CompletedTask;
    }

    public override IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            var names = new HashSet<string>(_canonicalNames, StringComparer.Ordinal)
            {
                "Byte",
                "substrate/encoding/ISO-8859-1/v1",
                "substrate/encoding/windows-1252/v1",
                "substrate/utf8/continuation/v1",
                "substrate/utf8/lead2/v1",
                "substrate/utf8/lead3/v1",
                "substrate/utf8/lead4/v1",
                "substrate/utf8/invalid/v1",
                "ordinal/0/v1",
                "ordinal/1/v1",
            };
            return names.ToArray();
        }
    }

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int batch = IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.Unicode, options);
        IReadOnlyList<ArtifactJob> jobs = ResolveArtifactJobs(context);
        ArtifactJob[] xml = jobs.Where(static job => job.Kind == ArtifactKind.UcdXml).ToArray();
        ArtifactJob[] ducet = jobs.Where(static job => job.Kind == ArtifactKind.Ducet).ToArray();
        ArtifactJob[] propertyAliases = jobs
            .Where(static job => job.Kind == ArtifactKind.PropertyAliases)
            .ToArray();
        if (xml.Length != 1 || ducet.Length != 1)
            throw new InvalidOperationException(
                $"Unicode floor requires exactly one admitted complete UCD XML and one DUCET artifact; "
                + $"selected xml={xml.Length}, ducet={ducet.Length}.");
        if (propertyAliases.Length != 1)
            throw new InvalidOperationException(
                "The Unicode UCDXML recipe requires exactly one PropertyAliases.txt sidecar; "
                + $"selected aliases={propertyAliases.Length}.");
        _ucdXmlRecipe = UcdXmlRecipe.Load(propertyAliases[0].Path);
        _cookbook.Register(_ucdXmlRecipe.Recipe);
        SemanticSourceRecipe selectedRecipe = _cookbook.Resolve(
            _ucdXmlRecipe.Recipe.RecipeId);

        FloorRunState floor = PrepareFloor(
            xml[0], ducet[0], selectedRecipe, context);
        await foreach (SubstrateChange change in RunFloorDataAsync(
                           floor, context, options, batch, ct).ConfigureAwait(false))
            yield return change;

        if (!floor.Skipped)
            await foreach (SubstrateChange barrier in ApplyBarrierAsync(
                               "unicode/tier0-floor-persisted", ct).ConfigureAwait(false))
                yield return barrier;

        // Runtime text composition is permitted only after the database's Tier-0 floor
        // is committed (or an existing completed floor was proven). The ROM accelerates
        // downstream composition; it never supplies the Tier-0 database rows.
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.LoadDefault();

        if (!floor.Skipped && options.MaxInputUnits == 0)
        {
            UnicodeSeedSnapshot snapshot = floor.Snapshot
                ?? throw new InvalidOperationException("Unicode source snapshot was not retained.");
            var semanticPhase = new UcdXmlSemanticPhase(this, snapshot, _ucdXmlRecipe, batch);
            await foreach (SubstrateChange original in base.RunPhaseAsync(
                               semanticPhase, context, options, ct).ConfigureAwait(false))
            {
                floor.Records += original.Metadata.InputUnitsConsumed;
                floor.Entities += original.Entities.Length;
                floor.Physicalities += original.Physicalities.Length;
                floor.Attestations += original.Attestations.Length;
                if (!original.IntentStages.IsDefaultOrEmpty)
                    foreach (IntentStage stage in original.IntentStages)
                    {
                        if (stage.IsInvalid) continue;
                        floor.Entities += stage.EntityCount;
                        floor.Physicalities += stage.PhysicalityCount;
                        floor.Attestations += stage.AttestationCount;
                    }
                SubstrateChange change = floor.SemanticRecipeId is { } semanticRecipeId
                    ? SourceArtifactProvenance.Bind(original, semanticRecipeId)
                    : original;
                yield return change;
            }
            floor.Snapshot = null; // semantic phase owns and has disposed it.
        }

        await foreach (SubstrateChange change in FinalizeFloorAsync(
                           floor, options, ct).ConfigureAwait(false))
            yield return change;

        if (options.MaxInputUnits > 0)
        {
            floor.Snapshot?.Dispose();
            floor.Snapshot = null;
            yield break;
        }

        if (SourceVocabularyBootstrap.BuildLicenseChange(Manifest) is { } licenseChange)
            yield return licenseChange;

        ArtifactJob[] independent = jobs
            .Where(static job => job.Kind is not ArtifactKind.UcdXml and not ArtifactKind.Ducet
                && !IsRepertoireCompensation(job.Kind))
            .ToArray();
        await foreach (SubstrateChange change in RunArtifactPhasesAsync(
                           independent,
                           context,
                           options,
                           job => BuildArtifactPhase(job, batch),
                           static job => job.Label,
                           static job => job.Path,
                           ct).ConfigureAwait(false))
            yield return change;
    }

    private sealed class FloorRunState
    {
        public required ArtifactJob Xml { get; init; }
        public required ArtifactJob Ducet { get; init; }
        public required string XmlLabel { get; init; }
        public required string DucetLabel { get; init; }
        public Hash128? XmlRoot { get; init; }
        public Hash128? DucetRoot { get; init; }
        public IngestArtifact? XmlArtifact { get; init; }
        public IngestArtifact? DucetArtifact { get; init; }
        public SourceArtifactIdentity? XmlIdentity { get; init; }
        public SourceArtifactIdentity? DucetIdentity { get; init; }
        public Hash128? RecipeId { get; init; }
        public Hash128? SemanticRecipeId { get; init; }
        public UnicodeSeedSnapshot? Snapshot { get; set; }
        public bool Skipped { get; set; }
        public long Records { get; set; }
        public long Entities { get; set; }
        public long Physicalities { get; set; }
        public long Attestations { get; set; }
    }

    private FloorRunState PrepareFloor(
        ArtifactJob xml,
        ArtifactJob ducet,
        SemanticSourceRecipe semanticRecipe,
        IDecomposerContext context)
    {
        string xmlLabel = ClaimArtifact(context, xml.Path, xml.Label);
        string ducetLabel = ClaimArtifact(context, ducet.Path, ducet.Label);
        Hash128? xmlRoot = IngestBatchPipeline.TryResolveFileIdentity(xml.Path);
        Hash128? ducetRoot = IngestBatchPipeline.TryResolveFileIdentity(ducet.Path);
        IngestArtifact? xmlArtifact = context.HasArtifactGraph
            ? context.SelectedArtifacts.SingleOrDefault(a => string.Equals(
                Path.GetFullPath(a.Path), Path.GetFullPath(xml.Path), StringComparison.Ordinal))
            : null;
        IngestArtifact? ducetArtifact = context.HasArtifactGraph
            ? context.SelectedArtifacts.SingleOrDefault(a => string.Equals(
                Path.GetFullPath(a.Path), Path.GetFullPath(ducet.Path), StringComparison.Ordinal))
            : null;
        SourceArtifactIdentity? xmlIdentity = xmlArtifact is null
            ? null : SourceArtifactProvenance.Resolve(xmlArtifact, xmlRoot);
        SourceArtifactIdentity? ducetIdentity = ducetArtifact is null
            ? null : SourceArtifactProvenance.Resolve(ducetArtifact, ducetRoot);
        Hash128? recipeId = xmlIdentity is { } xi && ducetIdentity is { } di
            ? SourceArtifactProvenance.RecipeId(
                SourceName, Manifest.License.Version ?? "unknown", "tier0-floor",
                [xi.ArtifactId, di.ArtifactId, semanticRecipe.RecipeId])
            : null;
        Hash128? semanticRecipeId = xmlIdentity is { } semanticXml
            ? SourceArtifactProvenance.RecipeId(
                SourceName, Manifest.License.Version ?? "unknown", "ucdxml-semantic",
                [semanticXml.ArtifactId, semanticRecipe.RecipeId])
            : null;

        return new FloorRunState
        {
            Xml = xml,
            Ducet = ducet,
            XmlLabel = xmlLabel,
            DucetLabel = ducetLabel,
            XmlRoot = xmlRoot,
            DucetRoot = ducetRoot,
            XmlArtifact = xmlArtifact,
            DucetArtifact = ducetArtifact,
            XmlIdentity = xmlIdentity,
            DucetIdentity = ducetIdentity,
            RecipeId = recipeId,
            SemanticRecipeId = semanticRecipeId,
        };
    }

    private async IAsyncEnumerable<SubstrateChange> RunFloorDataAsync(
        FloorRunState floor,
        IDecomposerContext context,
        DecomposerOptions options,
        int batch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        bool xmlDone = floor.XmlRoot is { } xr
            && !options.ReObservePresent
            && await context.Reader.HasFileCompletedAsync(
                xr, Source, LayerOrder, ct).ConfigureAwait(false);
        bool ducetDone = floor.DucetRoot is { } dr
            && !options.ReObservePresent
            && await context.Reader.HasFileCompletedAsync(
                dr, Source, LayerOrder, ct).ConfigureAwait(false);
        if (xmlDone && ducetDone)
        {
            floor.Skipped = true;
            yield break;
        }

        var observability = Laplace.Ingestion.IngestObservabilityScope.Current;
        observability.OnFileStarted(
            SourceName, floor.XmlLabel, IngestBatchPipeline.TryFileBytes(floor.Xml.Path));
        observability.OnFileStarted(
            SourceName, floor.DucetLabel, IngestBatchPipeline.TryFileBytes(floor.Ducet.Path));

        floor.Snapshot = UnicodeSeed.OpenSnapshot(floor.Xml.Path, floor.Ducet.Path);
        if (options.MaxInputUnits == 0)
        {
            UcdXmlRecipe recipe = _ucdXmlRecipe
                ?? throw new InvalidOperationException("UCDXML recipe was not selected.");
            await using Stream schemaStream = floor.Snapshot.OpenUcdXmlStream();
            await recipe.ValidateProviderAsync(schemaStream, ct).ConfigureAwait(false);
        }
        var phase = new UnicodeFloorPhase(floor.Snapshot, batch);
        await foreach (SubstrateChange original in base.RunPhaseAsync(
                           phase, context, options, ct).ConfigureAwait(false))
        {
            SubstrateChange change = original;
            floor.Records += change.Metadata.InputUnitsConsumed;
            floor.Entities += change.Entities.Length;
            floor.Physicalities += change.Physicalities.Length;
            floor.Attestations += change.Attestations.Length;
            if (!change.IntentStages.IsDefaultOrEmpty)
                foreach (IntentStage stage in change.IntentStages)
                {
                    if (stage.IsInvalid) continue;
                    floor.Entities += stage.EntityCount;
                    floor.Physicalities += stage.PhysicalityCount;
                    floor.Attestations += stage.AttestationCount;
                }
            if (floor.RecipeId is { } recipeId)
                change = SourceArtifactProvenance.Bind(change, recipeId);
            yield return change;
        }
    }

    private async IAsyncEnumerable<SubstrateChange> FinalizeFloorAsync(
        FloorRunState floor,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var observability = Laplace.Ingestion.IngestObservabilityScope.Current;
        if (floor.Skipped)
        {
            observability.OnFileComposed(
                SourceName, floor.XmlLabel, floor.XmlIdentity?.ArtifactId,
                resumeFingerprint: floor.XmlRoot);
            observability.OnFileComposed(
                SourceName, floor.DucetLabel, floor.DucetIdentity?.ArtifactId,
                resumeFingerprint: floor.DucetRoot);
            yield return IngestBatchPipeline.BuildSkippedBoundary(Source, floor.XmlLabel);
            yield return IngestBatchPipeline.BuildSkippedBoundary(Source, floor.DucetLabel);
            yield break;
        }

        if (options.MaxInputUnits > 0)
        {
            observability.OnFileComposed(
                SourceName, floor.XmlLabel, floor.XmlIdentity?.ArtifactId,
                floor.Records, floor.Entities, floor.Physicalities, floor.Attestations,
                resumeFingerprint: floor.XmlRoot);
            observability.OnFileComposed(
                SourceName, floor.DucetLabel, floor.DucetIdentity?.ArtifactId,
                resumeFingerprint: floor.DucetRoot);
            yield return IngestBatchPipeline.BuildCancelledBoundary(Source, floor.XmlLabel);
            yield return IngestBatchPipeline.BuildCancelledBoundary(Source, floor.DucetLabel);
            yield break;
        }

        if (floor.XmlArtifact is not null)
        {
            SubstrateChange provenance = SourceArtifactProvenance.BuildChange(
                floor.XmlArtifact, Source, TrustClass, floor.XmlRoot)
                with { CountsAsUnit = false };
            provenance = IngestBatchPipeline.BindFileLabel(provenance, floor.XmlLabel);
            yield return provenance;
            floor.Entities += provenance.Entities.Length;
            floor.Physicalities += provenance.Physicalities.Length;
            floor.Attestations += provenance.Attestations.Length;
        }
        if (floor.DucetArtifact is not null)
        {
            SubstrateChange provenance = SourceArtifactProvenance.BuildChange(
                floor.DucetArtifact, Source, TrustClass, floor.DucetRoot)
                with { CountsAsUnit = false };
            provenance = IngestBatchPipeline.BindFileLabel(provenance, floor.DucetLabel);
            yield return provenance;
            floor.Entities += provenance.Entities.Length;
            floor.Physicalities += provenance.Physicalities.Length;
            floor.Attestations += provenance.Attestations.Length;
        }
        if (floor.XmlIdentity is { } xi && floor.DucetIdentity is { } di)
            yield return SourceArtifactProvenance.BuildRecipeChange(
                SourceName,
                Manifest.License.Version ?? "unknown",
                "tier0-floor",
                Source,
                TrustClass,
                [xi.ArtifactId, di.ArtifactId]);

        await foreach (SubstrateChange barrier in ApplyBarrierAsync(
                           "unicode/tier0-provenance-persisted", ct).ConfigureAwait(false))
            yield return barrier;

        observability.OnFileComposed(
            SourceName, floor.XmlLabel, floor.XmlIdentity?.ArtifactId,
            floor.Records, floor.Entities, floor.Physicalities, floor.Attestations,
            resumeFingerprint: floor.XmlRoot);
        observability.OnFileComposed(
            SourceName, floor.DucetLabel, floor.DucetIdentity?.ArtifactId,
            resumeFingerprint: floor.DucetRoot);

        var names = new HashSet<string>(CanonicalNamesForReadback, StringComparer.Ordinal)
        {
            $"substrate/source/{SourceName}/v1",
        };
        yield return floor.XmlRoot is { } completedXml
            ? IngestBatchPipeline.BuildFileCompletion(
                Source, floor.XmlLabel, completedXml, LayerOrder, names)
            : IngestBatchPipeline.BuildPeriodBoundary(Source, floor.XmlLabel);
        yield return floor.DucetRoot is { } completedDucet
            ? IngestBatchPipeline.BuildFileCompletion(
                Source, floor.DucetLabel, completedDucet, LayerOrder, names)
            : IngestBatchPipeline.BuildPeriodBoundary(Source, floor.DucetLabel);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        CancellationToken ct = default)
    {
        IReadOnlyList<ArtifactJob> jobs = ResolveArtifactJobs(context);
        if (jobs.Count == 0) return Task.FromResult<IngestInventory?>(null);

        // Do not open the artifacts just to count them: the physical source law requires
        // one claimed open. DUCET has an exact semantic denominator by contract; the
        // remaining source-row denominator is raised truthfully from observed parser units
        // as those files execute.
        long declared = jobs.Any(static job => job.Kind == ArtifactKind.UcdXml)
            && jobs.Any(static job => job.Kind == ArtifactKind.Ducet)
            ? UnicodeSeed.CodepointCount
            : 0L;
        if (options.MaxInputUnits > 0) declared = Math.Min(declared, options.MaxInputUnits);

        var files = jobs.Select(job => new IngestFileSpec(
            job.Label,
            job.Path,
            job.Kind == ArtifactKind.UcdXml ? UnicodeSeed.CodepointCount : 0L)).ToArray();
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("source-rows", declared, files, TracksFileCompletion: true));
    }

    public override async Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context,
        CancellationToken ct = default)
    {
        IngestInventory? inventory = await DescribeInputAsync(
            context, DecomposerOptions.Default, ct).ConfigureAwait(false);
        return inventory?.TotalInputUnits;
    }

    /// <summary>
    /// Enumerate the complete local Unicode release tree when no release MANIFEST.tsv is
    /// installed. Every physical file receives an explicit disposition; the fallback graph
    /// never silently turns "not recognized by this decomposer" into "not part of the source".
    /// A release MANIFEST.tsv, when present, remains the higher-authority selection.
    /// </summary>
    public Task<IngestArtifactGraph?> DescribeArtifactsAsync(
        string ecosystemPath,
        DecomposerOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(ecosystemPath))
            return Task.FromResult<IngestArtifactGraph?>(null);

        string root = Path.GetFullPath(ecosystemPath);
        string xml = Path.GetFullPath(
            _ucdxmlZip ?? Path.Combine(root, "ucdxml", "ucd.all.flat.zip"));
        string ducet = Path.GetFullPath(
            _ducet ?? Path.Combine(root, "uca", "allkeys.txt"));
        bool hasCanonicalXml = File.Exists(xml);

        var artifacts = new List<IngestArtifact>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                      .OrderBy(static p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(file);
            string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            IngestArtifactDisposition disposition;
            string notes;

            if (IsEquivalentFallbackPackaging(full, relative, root, xml))
            {
                disposition = IngestArtifactDisposition.EquivalentPackaging;
                notes = "alternate packaging of a selected Unicode semantic role; retained but not admitted as another witness";
            }
            else if (TryClassifyArtifact(full, root, xml, ducet, out ArtifactKind kind))
            {
                disposition = hasCanonicalXml && IsRepertoireCompensation(kind)
                    ? IngestArtifactDisposition.Superseded
                    : IngestArtifactDisposition.Admitted;
                notes = hasCanonicalXml && IsRepertoireCompensation(kind)
                    ? "semantic fields are admitted from the selected ucd.all.flat.xml recipe; retained as a conformance/packaging oracle, not duplicate testimony"
                    : "";
            }
            else if (IsUnicodeControlArtifact(relative))
            {
                disposition = IngestArtifactDisposition.ExcludedWithReason;
                notes = "release/provenance/checksum/control artifact; retained in the physical estate but not world testimony";
            }
            else if (IsUnicodeConformanceArtifact(relative))
            {
                disposition = IngestArtifactDisposition.ExcludedWithReason;
                notes = "Unicode conformance oracle; retained for validation rather than deposited as world testimony";
            }
            else
            {
                disposition = IngestArtifactDisposition.Unsupported;
                notes = "physical Unicode artifact is present but has no admitted semantic handler yet; omission is explicit and must not be reported as complete coverage";
            }

            var info = new FileInfo(full);
            artifacts.Add(new IngestArtifact(
                UnicodeSource.SourceName,
                UnicodeSource.License.Version ?? "unknown",
                relative,
                relative,
                full,
                disposition,
                UpstreamUrl: "",
                FetchedAtUtc: "",
                Bytes: info.Length,
                Sha256: "",
                UpstreamChecksum: "",
                MediaType: UnicodeMediaType(relative),
                License: UnicodeSource.License.Spdx ?? "",
                Citation: UnicodeSource.License.Citation ?? "",
                Language: "",
                Split: "",
                AnnotationOrigin: "unicode-standard",
                Notes: notes,
                JournalLabel: $"unicode/{relative}",
                ModifiedAt: info.LastWriteTimeUtc));
        }

        return Task.FromResult<IngestArtifactGraph?>(new IngestArtifactGraph(artifacts));
    }

    private static bool IsEquivalentFallbackPackaging(
        string fullPath,
        string relative,
        string root,
        string selectedXml)
    {
        if (relative.StartsWith("ucdxml/ucd.", StringComparison.Ordinal)
            && (relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            return !string.Equals(fullPath, selectedXml, StringComparison.Ordinal);

        if (relative == "ucd/DerivedJoiningType.txt")
            return File.Exists(Path.Combine(root, "ucd", "extracted", "DerivedJoiningType.txt"));

        if (relative == "ucd/DerivedNumericType.txt")
            return File.Exists(Path.Combine(root, "ucd", "extracted", "DerivedNumericType.txt"));

        if (relative is "ucd/extracted/DerivedGeneralCategory.txt"
            or "ucd/extracted/DerivedCombiningClass.txt"
            or "ucd/extracted/DerivedBidiClass.txt")
            return File.Exists(Path.Combine(root, "ucd", "UnicodeData.txt"));

        if (relative == "ucd/extracted/DerivedEastAsianWidth.txt")
            return File.Exists(Path.Combine(root, "ucd", "EastAsianWidth.txt"));

        if (relative == "ucd/extracted/DerivedLineBreak.txt")
            return File.Exists(Path.Combine(root, "ucd", "LineBreak.txt"));

        if (relative == "ucd/UCD.zip")
            return Directory.Exists(Path.Combine(root, "ucd"))
                && File.Exists(Path.Combine(root, "ucd", "UnicodeData.txt"));

        if (relative == "ucd/Unihan.zip")
            return Directory.EnumerateFiles(Path.Combine(root, "ucd"), "Unihan_*.txt",
                SearchOption.TopDirectoryOnly).Any()
                || Directory.Exists(Path.Combine(root, "ucd", "Unihan"));

        if (relative == "ucd/NamesList.html")
            return File.Exists(Path.Combine(root, "ucd", "NamesList.txt"));

        if (relative == "security/uts39-data-17.0.0.zip")
            return File.Exists(Path.Combine(root, "security", "confusables.txt"));

        if (relative == "security/confusablesSummary.txt")
            return File.Exists(Path.Combine(root, "security", "confusables.txt"));

        return false;
    }

    private static bool IsUnicodeControlArtifact(string relative)
    {
        string name = Path.GetFileName(relative);
        return relative.StartsWith("charts/", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ReadMe.txt", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
            || name.Contains("readme", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("copyright", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("index.html", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dtd", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnicodeConformanceArtifact(string relative)
    {
        string name = Path.GetFileName(relative);
        return name.Contains("Test", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/CollationTest/", StringComparison.OrdinalIgnoreCase);
    }

    private static string UnicodeMediaType(string relative)
    {
        string ext = Path.GetExtension(relative);
        return ext.ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".xml" => "application/xml",
            ".zip" => "application/zip",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };
    }

    internal static Hash128 CodepointId(uint codepoint)
    {
        if (codepoint > 0x10FFFFu)
            throw new ArgumentOutOfRangeException(nameof(codepoint));

        Span<byte> encoded = stackalloc byte[4];
        int length;
        if (codepoint < 0x80u)
        {
            encoded[0] = (byte)codepoint;
            length = 1;
        }
        else if (codepoint < 0x800u)
        {
            encoded[0] = (byte)(0xC0u | (codepoint >> 6));
            encoded[1] = (byte)(0x80u | (codepoint & 0x3Fu));
            length = 2;
        }
        else if (codepoint < 0x10000u)
        {
            // The native tier-0 universe includes surrogate positions as positions, so
            // preserve the same byte preimage rather than applying scalar-value policy.
            encoded[0] = (byte)(0xE0u | (codepoint >> 12));
            encoded[1] = (byte)(0x80u | ((codepoint >> 6) & 0x3Fu));
            encoded[2] = (byte)(0x80u | (codepoint & 0x3Fu));
            length = 3;
        }
        else
        {
            encoded[0] = (byte)(0xF0u | (codepoint >> 18));
            encoded[1] = (byte)(0x80u | ((codepoint >> 12) & 0x3Fu));
            encoded[2] = (byte)(0x80u | ((codepoint >> 6) & 0x3Fu));
            encoded[3] = (byte)(0x80u | (codepoint & 0x3Fu));
            length = 4;
        }
        return Hash128.Blake3(encoded[..length]);
    }

    internal static Hash128 StageCodepointTarget(uint targetCp) => CodepointId(targetCp);

    private Hash128 ClassifierEntity(
        SubstrateChangeBuilder builder,
        string canonicalPrefix,
        string value)
    {
        string canonical = $"{canonicalPrefix}/{value}/v1";
        _canonicalNames.Add(canonical);
        Hash128 id = Hash128.OfCanonical(canonical);
        builder.AddEntity(id, EntityTier.Word, EntityTypeRegistry.UcdClassifier, Source);
        return id;
    }

    private Hash128 PropertyValueEntity(
        SubstrateChangeBuilder builder,
        string property,
        string value)
    {
        string canonicalProperty = _ucdXmlRecipe?.CanonicalProperty(property) ?? property;
        string canonicalValue = _ucdXmlRecipe?.CanonicalValue(canonicalProperty, value) ?? value;
        string prefix = canonicalProperty switch
        {
            "Script" or "Script_Extensions" => "unicode/script",
            "Block" => "unicode/block",
            "General_Category" => "unicode/category",
            "Canonical_Combining_Class" => "unicode/combining_class",
            "Bidi_Class" => "unicode/bidi_class",
            "Age" => "unicode/age",
            "Line_Break" => "unicode/line_break",
            "East_Asian_Width" => "unicode/east_asian_width",
            "Joining_Type" => "unicode/joining_type",
            "Numeric_Type" => "unicode/numeric_type",
            _ => $"unicode/property_value/{canonicalProperty}",
        };
        return ClassifierEntity(builder, prefix, canonicalValue);
    }

    private RelationTypeRegistry.RelationTypeResolution PropertyRelation(
        SubstrateChangeBuilder builder,
        string canonicalPropertyName)
    {
        canonicalPropertyName = _ucdXmlRecipe?.CanonicalProperty(canonicalPropertyName)
            ?? canonicalPropertyName;
        RelationTypeRegistry.RelationTypeResolution relation =
            RelationTypeRegistry.ResolveUcdProperty(canonicalPropertyName);
        _canonicalNames.Add(relation.Canonical);
        if (_ucdPropertyTypes.Add(relation.Id))
            builder.AddEntity(new EntityRow(
                relation.Id, EntityTier.Word,
                BootstrapIntentBuilder.RelationTypeMetaTypeId, Source));
        if (relation.ParentId is { } parent)
        {
            AttestationRow declaration = NativeAttestation.Categorical(
                relation.Id, "IS_A", parent, Source, null,
                TC.StandardsDerived);
            if (_ucdPropertyDeclarations.Add(declaration.Id))
                builder.AddAttestation(declaration);
        }
        return relation;
    }

    private static void EnsureOrdinalContexts(SubstrateChangeBuilder builder)
    {
        builder.AddEntity(new EntityRow(
            UcdProperties.OrdinalCtx0, EntityTier.Word,
            EntityTypeRegistry.OrdinalContext, Source));
        builder.AddEntity(new EntityRow(
            UcdProperties.OrdinalCtx1, EntityTier.Word,
            EntityTypeRegistry.OrdinalContext, Source));
    }

    private IDecomposer BuildArtifactPhase(ArtifactJob job, int batch) =>
        job.Kind switch
        {
            ArtifactKind.Ducet or ArtifactKind.UcdXml => throw new InvalidOperationException(
                "Unicode floor authority artifacts execute together before independent property phases."),
            ArtifactKind.UnicodeData => new UnicodeDataPhase(this, job.Path, batch),
            ArtifactKind.Scripts => new RangePropertyPhase(
                this, job.Path, "scripts", UcdProperties.RelTypeHasScript,
                "unicode/script", batch),
            ArtifactKind.Blocks => new RangePropertyPhase(
                this, job.Path, "blocks", UcdProperties.RelTypeHasBlock,
                "unicode/block", batch),
            ArtifactKind.DerivedAge => new RangePropertyPhase(
                this, job.Path, "age", UcdProperties.RelTypeHasAge,
                "unicode/age", batch),
            ArtifactKind.LineBreak => new RangePropertyPhase(
                this, job.Path, "line-break", UcdProperties.RelTypeHasLineBreak,
                "unicode/line_break", batch),
            ArtifactKind.EastAsianWidth => new RangePropertyPhase(
                this, job.Path, "east-asian-width", UcdProperties.RelTypeHasEastAsianWidth,
                "unicode/east_asian_width", batch),
            ArtifactKind.JoiningType => new RangePropertyPhase(
                this, job.Path, "joining-type", UcdProperties.RelTypeHasJoiningType,
                "unicode/joining_type", batch),
            ArtifactKind.NumericType => new RangePropertyPhase(
                this, job.Path, "numeric-type", UcdProperties.RelTypeHasNumericType,
                "unicode/numeric_type", batch),
            ArtifactKind.BidiMirroring => new MirrorPhase(job.Path, batch),
            ArtifactKind.EmojiData => new RangePropertyPhase(
                this, job.Path, "emoji", UcdProperties.RelTypeHasEmojiProperty,
                "unicode/emoji", batch,
                new HashSet<string>(UcdProperties.EmojiPropNames, StringComparer.Ordinal)),
            ArtifactKind.NameAliases => new AliasPhase(job.Path, batch),
            ArtifactKind.Confusables => new ConfusablePhase(job.Path, batch),
            ArtifactKind.DerivedNormalization => new NormalizationPhase(this, job.Path, batch),
            ArtifactKind.ScriptExtensions => new ScriptExtensionsPhase(this, job.Path, batch),
            ArtifactKind.BinaryProperties => new BinaryPropertyPhase(this, job.Path, batch),
            ArtifactKind.GraphemeBreak => new ContextualRangePropertyPhase(this, job.Path, batch, "Grapheme_Cluster_Break"),
            ArtifactKind.WordBreak => new ContextualRangePropertyPhase(this, job.Path, batch, "Word_Break"),
            ArtifactKind.SentenceBreak => new ContextualRangePropertyPhase(this, job.Path, batch, "Sentence_Break"),
            ArtifactKind.IndicConjunctBreak => new ContextualRangePropertyPhase(this, job.Path, batch, "Indic_Conjunct_Break"),
            ArtifactKind.HangulSyllableType => new ContextualRangePropertyPhase(this, job.Path, batch, "Hangul_Syllable_Type"),
            ArtifactKind.VerticalOrientation => new ContextualRangePropertyPhase(this, job.Path, batch, "Vertical_Orientation"),
            ArtifactKind.IndicPositionalCategory => new ContextualRangePropertyPhase(this, job.Path, batch, "Indic_Positional_Category"),
            ArtifactKind.IndicSyllabicCategory => new ContextualRangePropertyPhase(this, job.Path, batch, "Indic_Syllabic_Category"),
            ArtifactKind.DerivedGeneralCategory => new RangePropertyPhase(
                this, job.Path, "derived-general-category", UcdProperties.RelTypeHasGeneralCategory,
                "unicode/category", batch),
            ArtifactKind.DerivedCombiningClass => new RangePropertyPhase(
                this, job.Path, "derived-combining-class", UcdProperties.RelTypeHasCombiningClass,
                "unicode/combining_class", batch),
            ArtifactKind.DerivedBidiClass => new RangePropertyPhase(
                this, job.Path, "derived-bidi-class", UcdProperties.RelTypeHasBidiClass,
                "unicode/bidi_class", batch),
            ArtifactKind.IdentifierStatus => new ContextualRangePropertyPhase(this, job.Path, batch, "Identifier_Status"),
            ArtifactKind.IdentifierType => new ContextualRangePropertyPhase(this, job.Path, batch, "Identifier_Type"),
            ArtifactKind.UnihanProperties => new UnihanPropertyPhase(this, job.Path, batch),
            ArtifactKind.ArabicShaping => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch,
                ["Arabic_Shaping_Name", "Arabic_Shaping_Joining_Type", "Arabic_Shaping_Joining_Group"]),
            ArtifactKind.BidiBrackets => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch,
                ["Bidi_Paired_Bracket", "Bidi_Paired_Bracket_Type"],
                new HashSet<int> { 0 }),
            ArtifactKind.CaseFolding => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch,
                ["Case_Folding_Status", "Case_Folding_Mapping"],
                new HashSet<int> { 1 }),
            ArtifactKind.SpecialCasing => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch,
                ["Special_Lowercase_Mapping", "Special_Titlecase_Mapping",
                 "Special_Uppercase_Mapping", "Special_Casing_Condition"],
                new HashSet<int> { 0, 1, 2 }),
            ArtifactKind.Jamo => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch, ["Jamo_Short_Name"]),
            ArtifactKind.NormalizationCorrections => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch,
                ["Normalization_Old_Mapping", "Normalization_New_Mapping",
                 "Normalization_Correction_Version"],
                new HashSet<int> { 0, 1 }),
            ArtifactKind.EquivalentUnifiedIdeograph => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch, ["Equivalent_Unified_Ideograph"],
                new HashSet<int> { 0 }),
            ArtifactKind.DerivedName => new ContextualRangePropertyPhase(
                this, job.Path, batch, "Derived_Name"),
            ArtifactKind.DerivedDecompositionType => new ContextualRangePropertyPhase(
                this, job.Path, batch, "Decomposition_Type"),
            ArtifactKind.DerivedJoiningGroup => new ContextualRangePropertyPhase(
                this, job.Path, batch, "Joining_Group"),
            ArtifactKind.DerivedNumericValues => new ContextualRangePropertyPhase(
                this, job.Path, batch, "Numeric_Value"),
            ArtifactKind.CompositionExclusions => new CodepointListPropertyPhase(
                this, job.Path, batch, "Full_Composition_Exclusion"),
            ArtifactKind.NamedSequences => new NamedSequencePhase(job.Path, batch),
            ArtifactKind.StandardizedVariants => new SequenceMetadataPhase(
                this, job.Path, batch,
                ["Standardized_Variant_Description", "Standardized_Variant_Condition"]),
            ArtifactKind.EmojiVariationSequences => new SequenceMetadataPhase(
                this, job.Path, batch,
                ["Emoji_Variation_Style", "Emoji_Variation_Description"]),
            ArtifactKind.EmojiSources => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch,
                ["Emoji_Source_Docomo", "Emoji_Source_KDDI", "Emoji_Source_SoftBank"]),
            ArtifactKind.TabbedCodepointProperties => new UnihanPropertyPhase(
                this, job.Path, batch),
            ArtifactKind.CjkRadicals => new CjkRadicalPhase(this, job.Path, batch),
            ArtifactKind.DoNotEmit => new DoNotEmitPhase(this, job.Path, batch),
            ArtifactKind.PropertyAliases => new PropertyAliasPhase(
                this,
                _ucdXmlRecipe?.PropertyAliasRows
                    ?? throw new InvalidOperationException("UCDXML recipe aliases were not loaded."),
                batch),
            ArtifactKind.PropertyValueAliases => new PropertyValueAliasPhase(
                this,
                _ucdXmlRecipe?.PropertyValueAliasRows
                    ?? throw new InvalidOperationException("UCDXML recipe value aliases were not loaded."),
                batch),
            ArtifactKind.IndexTerms => new IndexTermPhase(this, job.Path, batch),
            ArtifactKind.USourceData => new USourceDataPhase(this, job.Path, batch),
            ArtifactKind.EmojiSequences => new SequenceMetadataPhase(
                this, job.Path, batch,
                ["Emoji_Sequence_Type", "Emoji_Sequence_Description"]),
            ArtifactKind.Idna2008 => new ContextualRangePropertyPhase(
                this, job.Path, batch, "IDNA2008_Category"),
            ArtifactKind.IdnaMapping => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch,
                ["IDNA_Status", "IDNA_Mapping", "IDNA2008_Status"],
                new HashSet<int> { 1 }),
            ArtifactKind.IntentionalConfusables => new SequencePairPhase(
                this, job.Path, batch, "Intentional_Confusable"),
            ArtifactKind.UcaDecompositions => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch,
                ["UCA_Decomposition_Tag", "UCA_Decomposition"],
                new HashSet<int> { 1 }),
            ArtifactKind.UcaCommonTemplate => new CttPhase(this, job.Path, batch),
            ArtifactKind.NamesList => new NamesListPhase(this, job.Path, batch),
            ArtifactKind.BoundaryTest => new BoundaryTestPhase(this, job.Path, batch),
            ArtifactKind.NormalizationTest => new NormalizationTestPhase(this, job.Path, batch),
            ArtifactKind.EmojiTest => new EmojiTestPhase(this, job.Path, batch),
            ArtifactKind.LinkBracket => new DelimitedCodepointPropertyPhase(
                this, job.Path, batch, ["Link_Bracket"], new HashSet<int> { 0 }),
            ArtifactKind.LinkEmail => new CodepointListPropertyPhase(
                this, job.Path, batch, "Link_Email"),
            ArtifactKind.LinkTerm => new CodepointListPropertyPhase(
                this, job.Path, batch, "Link_Term"),
            _ => throw new InvalidOperationException($"Unsupported Unicode artifact kind {job.Kind}."),
        };

    private IReadOnlyList<ArtifactJob> ResolveArtifactJobs(IDecomposerContext context)
    {
        string baseDir = Path.GetFullPath(context.EcosystemPath);
        string xml = Path.GetFullPath(
            _ucdxmlZip ?? Path.Combine(baseDir, "ucdxml", "ucd.all.flat.zip"));
        string ducet = Path.GetFullPath(
            _ducet ?? Path.Combine(baseDir, "uca", "allkeys.txt"));

        if (context.HasArtifactGraph)
        {
            var jobs = new List<ArtifactJob>(context.SelectedArtifacts.Count);
            var singletonKinds = new HashSet<ArtifactKind>();
            bool selectedCanonicalXml = context.SelectedArtifacts.Any(artifact =>
                string.Equals(
                    Path.GetFullPath(artifact.Path), xml, StringComparison.Ordinal));
            foreach (IngestArtifact artifact in context.SelectedArtifacts)
            {
                string path = Path.GetFullPath(artifact.Path);
                ArtifactKind kind = ClassifyArtifact(path, baseDir, xml, ducet);
                if (selectedCanonicalXml && IsRepertoireCompensation(kind))
                    throw new InvalidOperationException(
                        $"Unicode artifact graph admits '{artifact.Id}' even though its semantic fields "
                        + "are owned by the selected ucd.all.flat.xml recipe. Mark the artifact "
                        + "superseded (or select a source generation whose recipe does not cover it); "
                        + "duplicate Unicode testimony is not admitted.");
                if (IsSingletonArtifactRole(kind) && !singletonKinds.Add(kind))
                    throw new InvalidOperationException(
                        $"Unicode selected more than one admitted artifact for singleton role {kind}; "
                        + "equivalent/superseded packaging must not double-vote.");
                jobs.Add(new ArtifactJob(kind, path, artifact.FileLabel));
            }
            jobs.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
            return jobs;
        }

        var legacy = new List<ArtifactJob>();
        AddIfPresent(legacy, ArtifactKind.Ducet, ducet, "uca/allkeys.txt");
        AddIfPresent(legacy, ArtifactKind.UcdXml, xml, "ucdxml/ucd.all.flat.zip");
        AddIfPresent(legacy, ArtifactKind.UnicodeData,
            Path.Combine(baseDir, "ucd", "UnicodeData.txt"), "ucd/UnicodeData.txt");
        AddIfPresent(legacy, ArtifactKind.Scripts,
            Path.Combine(baseDir, "ucd", "Scripts.txt"), "ucd/Scripts.txt");
        AddIfPresent(legacy, ArtifactKind.Blocks,
            Path.Combine(baseDir, "ucd", "Blocks.txt"), "ucd/Blocks.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedAge,
            Path.Combine(baseDir, "ucd", "DerivedAge.txt"), "ucd/DerivedAge.txt");
        AddIfPresent(legacy, ArtifactKind.LineBreak,
            Path.Combine(baseDir, "ucd", "LineBreak.txt"), "ucd/LineBreak.txt");
        AddIfPresent(legacy, ArtifactKind.EastAsianWidth,
            Path.Combine(baseDir, "ucd", "EastAsianWidth.txt"), "ucd/EastAsianWidth.txt");
        AddFirstPresent(legacy, ArtifactKind.JoiningType,
            (Path.Combine(baseDir, "ucd", "extracted", "DerivedJoiningType.txt"),
             "ucd/extracted/DerivedJoiningType.txt"),
            (Path.Combine(baseDir, "ucd", "DerivedJoiningType.txt"),
             "ucd/DerivedJoiningType.txt"));
        AddFirstPresent(legacy, ArtifactKind.NumericType,
            (Path.Combine(baseDir, "ucd", "extracted", "DerivedNumericType.txt"),
             "ucd/extracted/DerivedNumericType.txt"),
            (Path.Combine(baseDir, "ucd", "DerivedNumericType.txt"),
             "ucd/DerivedNumericType.txt"));
        AddIfPresent(legacy, ArtifactKind.BidiMirroring,
            Path.Combine(baseDir, "ucd", "BidiMirroring.txt"), "ucd/BidiMirroring.txt");
        AddIfPresent(legacy, ArtifactKind.EmojiData,
            Path.Combine(baseDir, "ucd", "emoji", "emoji-data.txt"), "ucd/emoji/emoji-data.txt");
        AddIfPresent(legacy, ArtifactKind.NameAliases,
            Path.Combine(baseDir, "ucd", "NameAliases.txt"), "ucd/NameAliases.txt");
        AddIfPresent(legacy, ArtifactKind.Confusables,
            Path.Combine(baseDir, "security", "confusables.txt"), "security/confusables.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedNormalization,
            Path.Combine(baseDir, "ucd", "DerivedNormalizationProps.txt"),
            "ucd/DerivedNormalizationProps.txt");
        AddIfPresent(legacy, ArtifactKind.ScriptExtensions,
            Path.Combine(baseDir, "ucd", "ScriptExtensions.txt"), "ucd/ScriptExtensions.txt");
        AddIfPresent(legacy, ArtifactKind.BinaryProperties,
            Path.Combine(baseDir, "ucd", "PropList.txt"), "ucd/PropList.txt");
        AddIfPresent(legacy, ArtifactKind.BinaryProperties,
            Path.Combine(baseDir, "ucd", "DerivedCoreProperties.txt"), "ucd/DerivedCoreProperties.txt");
        AddIfPresent(legacy, ArtifactKind.GraphemeBreak,
            Path.Combine(baseDir, "ucd", "auxiliary", "GraphemeBreakProperty.txt"), "ucd/auxiliary/GraphemeBreakProperty.txt");
        AddIfPresent(legacy, ArtifactKind.WordBreak,
            Path.Combine(baseDir, "ucd", "auxiliary", "WordBreakProperty.txt"), "ucd/auxiliary/WordBreakProperty.txt");
        AddIfPresent(legacy, ArtifactKind.SentenceBreak,
            Path.Combine(baseDir, "ucd", "auxiliary", "SentenceBreakProperty.txt"), "ucd/auxiliary/SentenceBreakProperty.txt");
        AddIfPresent(legacy, ArtifactKind.BoundaryTest,
            Path.Combine(baseDir, "ucd", "auxiliary", "GraphemeBreakTest.txt"), "ucd/auxiliary/GraphemeBreakTest.txt");
        AddIfPresent(legacy, ArtifactKind.BoundaryTest,
            Path.Combine(baseDir, "ucd", "auxiliary", "WordBreakTest.txt"), "ucd/auxiliary/WordBreakTest.txt");
        AddIfPresent(legacy, ArtifactKind.BoundaryTest,
            Path.Combine(baseDir, "ucd", "auxiliary", "SentenceBreakTest.txt"), "ucd/auxiliary/SentenceBreakTest.txt");
        AddIfPresent(legacy, ArtifactKind.BoundaryTest,
            Path.Combine(baseDir, "ucd", "auxiliary", "LineBreakTest.txt"), "ucd/auxiliary/LineBreakTest.txt");
        AddIfPresent(legacy, ArtifactKind.NormalizationTest,
            Path.Combine(baseDir, "ucd", "NormalizationTest.txt"), "ucd/NormalizationTest.txt");
        AddIfPresent(legacy, ArtifactKind.EmojiTest,
            Path.Combine(baseDir, "emoji", "emoji-test.txt"), "emoji/emoji-test.txt");
        AddIfPresent(legacy, ArtifactKind.IndicConjunctBreak,
            Path.Combine(baseDir, "ucd", "auxiliary", "IndicConjunctBreak.txt"), "ucd/auxiliary/IndicConjunctBreak.txt");
        AddIfPresent(legacy, ArtifactKind.HangulSyllableType,
            Path.Combine(baseDir, "ucd", "HangulSyllableType.txt"), "ucd/HangulSyllableType.txt");
        AddIfPresent(legacy, ArtifactKind.VerticalOrientation,
            Path.Combine(baseDir, "ucd", "VerticalOrientation.txt"), "ucd/VerticalOrientation.txt");
        AddIfPresent(legacy, ArtifactKind.IndicPositionalCategory,
            Path.Combine(baseDir, "ucd", "IndicPositionalCategory.txt"), "ucd/IndicPositionalCategory.txt");
        AddIfPresent(legacy, ArtifactKind.IndicSyllabicCategory,
            Path.Combine(baseDir, "ucd", "IndicSyllabicCategory.txt"), "ucd/IndicSyllabicCategory.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedGeneralCategory,
            Path.Combine(baseDir, "ucd", "extracted", "DerivedGeneralCategory.txt"), "ucd/extracted/DerivedGeneralCategory.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedCombiningClass,
            Path.Combine(baseDir, "ucd", "extracted", "DerivedCombiningClass.txt"), "ucd/extracted/DerivedCombiningClass.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedBidiClass,
            Path.Combine(baseDir, "ucd", "extracted", "DerivedBidiClass.txt"), "ucd/extracted/DerivedBidiClass.txt");
        AddIfPresent(legacy, ArtifactKind.IdentifierStatus,
            Path.Combine(baseDir, "security", "IdentifierStatus.txt"), "security/IdentifierStatus.txt");
        AddIfPresent(legacy, ArtifactKind.IdentifierType,
            Path.Combine(baseDir, "security", "IdentifierType.txt"), "security/IdentifierType.txt");
        AddIfPresent(legacy, ArtifactKind.ArabicShaping,
            Path.Combine(baseDir, "ucd", "ArabicShaping.txt"), "ucd/ArabicShaping.txt");
        AddIfPresent(legacy, ArtifactKind.BidiBrackets,
            Path.Combine(baseDir, "ucd", "BidiBrackets.txt"), "ucd/BidiBrackets.txt");
        AddIfPresent(legacy, ArtifactKind.CaseFolding,
            Path.Combine(baseDir, "ucd", "CaseFolding.txt"), "ucd/CaseFolding.txt");
        AddIfPresent(legacy, ArtifactKind.SpecialCasing,
            Path.Combine(baseDir, "ucd", "SpecialCasing.txt"), "ucd/SpecialCasing.txt");
        AddIfPresent(legacy, ArtifactKind.Jamo,
            Path.Combine(baseDir, "ucd", "Jamo.txt"), "ucd/Jamo.txt");
        AddIfPresent(legacy, ArtifactKind.NormalizationCorrections,
            Path.Combine(baseDir, "ucd", "NormalizationCorrections.txt"), "ucd/NormalizationCorrections.txt");
        AddIfPresent(legacy, ArtifactKind.EquivalentUnifiedIdeograph,
            Path.Combine(baseDir, "ucd", "EquivalentUnifiedIdeograph.txt"), "ucd/EquivalentUnifiedIdeograph.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedName,
            Path.Combine(baseDir, "ucd", "extracted", "DerivedName.txt"), "ucd/extracted/DerivedName.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedDecompositionType,
            Path.Combine(baseDir, "ucd", "extracted", "DerivedDecompositionType.txt"), "ucd/extracted/DerivedDecompositionType.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedJoiningGroup,
            Path.Combine(baseDir, "ucd", "extracted", "DerivedJoiningGroup.txt"), "ucd/extracted/DerivedJoiningGroup.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedNumericValues,
            Path.Combine(baseDir, "ucd", "extracted", "DerivedNumericValues.txt"), "ucd/extracted/DerivedNumericValues.txt");
        AddIfPresent(legacy, ArtifactKind.CompositionExclusions,
            Path.Combine(baseDir, "ucd", "CompositionExclusions.txt"), "ucd/CompositionExclusions.txt");
        AddIfPresent(legacy, ArtifactKind.NamedSequences,
            Path.Combine(baseDir, "ucd", "NamedSequences.txt"), "ucd/NamedSequences.txt");
        AddIfPresent(legacy, ArtifactKind.NamedSequences,
            Path.Combine(baseDir, "ucd", "NamedSequencesProv.txt"), "ucd/NamedSequencesProv.txt");
        AddIfPresent(legacy, ArtifactKind.StandardizedVariants,
            Path.Combine(baseDir, "ucd", "StandardizedVariants.txt"), "ucd/StandardizedVariants.txt");
        AddIfPresent(legacy, ArtifactKind.EmojiVariationSequences,
            Path.Combine(baseDir, "ucd", "emoji", "emoji-variation-sequences.txt"), "ucd/emoji/emoji-variation-sequences.txt");
        AddIfPresent(legacy, ArtifactKind.EmojiSources,
            Path.Combine(baseDir, "ucd", "EmojiSources.txt"), "ucd/EmojiSources.txt");
        AddIfPresent(legacy, ArtifactKind.TabbedCodepointProperties,
            Path.Combine(baseDir, "ucd", "NushuSources.txt"), "ucd/NushuSources.txt");
        AddIfPresent(legacy, ArtifactKind.TabbedCodepointProperties,
            Path.Combine(baseDir, "ucd", "TangutSources.txt"), "ucd/TangutSources.txt");
        AddIfPresent(legacy, ArtifactKind.TabbedCodepointProperties,
            Path.Combine(baseDir, "ucd", "Unikemet.txt"), "ucd/Unikemet.txt");
        AddIfPresent(legacy, ArtifactKind.CjkRadicals,
            Path.Combine(baseDir, "ucd", "CJKRadicals.txt"), "ucd/CJKRadicals.txt");
        AddIfPresent(legacy, ArtifactKind.DoNotEmit,
            Path.Combine(baseDir, "ucd", "DoNotEmit.txt"), "ucd/DoNotEmit.txt");
        AddIfPresent(legacy, ArtifactKind.PropertyAliases,
            Path.Combine(baseDir, "ucd", "PropertyAliases.txt"), "ucd/PropertyAliases.txt");
        AddIfPresent(legacy, ArtifactKind.PropertyValueAliases,
            Path.Combine(baseDir, "ucd", "PropertyValueAliases.txt"), "ucd/PropertyValueAliases.txt");
        AddIfPresent(legacy, ArtifactKind.IndexTerms,
            Path.Combine(baseDir, "ucd", "Index.txt"), "ucd/Index.txt");
        AddIfPresent(legacy, ArtifactKind.USourceData,
            Path.Combine(baseDir, "ucd", "USourceData.txt"), "ucd/USourceData.txt");
        AddIfPresent(legacy, ArtifactKind.EmojiSequences,
            Path.Combine(baseDir, "emoji", "emoji-sequences.txt"), "emoji/emoji-sequences.txt");
        AddIfPresent(legacy, ArtifactKind.EmojiSequences,
            Path.Combine(baseDir, "emoji", "emoji-zwj-sequences.txt"), "emoji/emoji-zwj-sequences.txt");
        AddIfPresent(legacy, ArtifactKind.Idna2008,
            Path.Combine(baseDir, "idna", "Idna2008.txt"), "idna/Idna2008.txt");
        AddIfPresent(legacy, ArtifactKind.IdnaMapping,
            Path.Combine(baseDir, "idna", "IdnaMappingTable.txt"), "idna/IdnaMappingTable.txt");
        AddIfPresent(legacy, ArtifactKind.IntentionalConfusables,
            Path.Combine(baseDir, "security", "intentional.txt"), "security/intentional.txt");
        AddIfPresent(legacy, ArtifactKind.UcaDecompositions,
            Path.Combine(baseDir, "uca", "decomps.txt"), "uca/decomps.txt");
        AddIfPresent(legacy, ArtifactKind.UcaCommonTemplate,
            Path.Combine(baseDir, "uca", "ctt.txt"), "uca/ctt.txt");
        AddIfPresent(legacy, ArtifactKind.NamesList,
            Path.Combine(baseDir, "ucd", "NamesList.txt"), "ucd/NamesList.txt");
        AddIfPresent(legacy, ArtifactKind.LinkBracket,
            Path.Combine(baseDir, "linkification", "LinkBracket.txt"), "linkification/LinkBracket.txt");
        AddIfPresent(legacy, ArtifactKind.LinkEmail,
            Path.Combine(baseDir, "linkification", "LinkEmail.txt"), "linkification/LinkEmail.txt");
        AddIfPresent(legacy, ArtifactKind.LinkTerm,
            Path.Combine(baseDir, "linkification", "LinkTerm.txt"), "linkification/LinkTerm.txt");
        AddUnihanFiles(legacy, baseDir);
        legacy.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
        return legacy;
    }

    private static ArtifactKind ClassifyArtifact(
        string fullPath,
        string baseDir,
        string xml,
        string ducet)
    {
        if (TryClassifyArtifact(fullPath, baseDir, xml, ducet, out ArtifactKind kind))
            return kind;
        throw new InvalidOperationException(
            $"Selected Unicode artifact has no ingest disposition/handler: '{fullPath}'.");
    }

    private static bool TryClassifyArtifact(
        string fullPath,
        string baseDir,
        string xml,
        string ducet,
        out ArtifactKind kind)
    {
        if (string.Equals(fullPath, ducet, StringComparison.Ordinal))
        {
            kind = ArtifactKind.Ducet;
            return true;
        }
        if (string.Equals(fullPath, xml, StringComparison.Ordinal))
        {
            kind = ArtifactKind.UcdXml;
            return true;
        }

        string relative = Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
        kind = relative switch
        {
            "uca/allkeys.txt" => ArtifactKind.Ducet,
            "ucdxml/ucd.all.flat.zip" or "ucdxml/ucd.all.flat.xml" => ArtifactKind.UcdXml,
            "ucd/UnicodeData.txt" => ArtifactKind.UnicodeData,
            "ucd/Scripts.txt" => ArtifactKind.Scripts,
            "ucd/Blocks.txt" => ArtifactKind.Blocks,
            "ucd/DerivedAge.txt" => ArtifactKind.DerivedAge,
            "ucd/LineBreak.txt" => ArtifactKind.LineBreak,
            "ucd/EastAsianWidth.txt" => ArtifactKind.EastAsianWidth,
            "ucd/extracted/DerivedJoiningType.txt" or "ucd/DerivedJoiningType.txt" => ArtifactKind.JoiningType,
            "ucd/extracted/DerivedNumericType.txt" or "ucd/DerivedNumericType.txt" => ArtifactKind.NumericType,
            "ucd/BidiMirroring.txt" => ArtifactKind.BidiMirroring,
            "ucd/emoji/emoji-data.txt" => ArtifactKind.EmojiData,
            "ucd/NameAliases.txt" => ArtifactKind.NameAliases,
            "security/confusables.txt" => ArtifactKind.Confusables,
            "ucd/DerivedNormalizationProps.txt" => ArtifactKind.DerivedNormalization,
            "ucd/ScriptExtensions.txt" => ArtifactKind.ScriptExtensions,
            "ucd/PropList.txt" or "ucd/DerivedCoreProperties.txt"
                or "ucd/extracted/DerivedBinaryProperties.txt" => ArtifactKind.BinaryProperties,
            "ucd/auxiliary/GraphemeBreakProperty.txt" => ArtifactKind.GraphemeBreak,
            "ucd/auxiliary/WordBreakProperty.txt" => ArtifactKind.WordBreak,
            "ucd/auxiliary/SentenceBreakProperty.txt" => ArtifactKind.SentenceBreak,
            "ucd/auxiliary/GraphemeBreakTest.txt" or "ucd/auxiliary/WordBreakTest.txt"
                or "ucd/auxiliary/SentenceBreakTest.txt" or "ucd/auxiliary/LineBreakTest.txt"
                => ArtifactKind.BoundaryTest,
            "ucd/NormalizationTest.txt" => ArtifactKind.NormalizationTest,
            "emoji/emoji-test.txt" => ArtifactKind.EmojiTest,
            "ucd/auxiliary/IndicConjunctBreak.txt" => ArtifactKind.IndicConjunctBreak,
            "ucd/HangulSyllableType.txt" => ArtifactKind.HangulSyllableType,
            "ucd/VerticalOrientation.txt" => ArtifactKind.VerticalOrientation,
            "ucd/IndicPositionalCategory.txt" => ArtifactKind.IndicPositionalCategory,
            "ucd/IndicSyllabicCategory.txt" => ArtifactKind.IndicSyllabicCategory,
            "ucd/extracted/DerivedGeneralCategory.txt" => ArtifactKind.DerivedGeneralCategory,
            "ucd/extracted/DerivedCombiningClass.txt" => ArtifactKind.DerivedCombiningClass,
            "ucd/extracted/DerivedBidiClass.txt" => ArtifactKind.DerivedBidiClass,
            "security/IdentifierStatus.txt" => ArtifactKind.IdentifierStatus,
            "security/IdentifierType.txt" => ArtifactKind.IdentifierType,
            "ucd/ArabicShaping.txt" => ArtifactKind.ArabicShaping,
            "ucd/BidiBrackets.txt" => ArtifactKind.BidiBrackets,
            "ucd/CaseFolding.txt" => ArtifactKind.CaseFolding,
            "ucd/SpecialCasing.txt" => ArtifactKind.SpecialCasing,
            "ucd/Jamo.txt" => ArtifactKind.Jamo,
            "ucd/NormalizationCorrections.txt" => ArtifactKind.NormalizationCorrections,
            "ucd/EquivalentUnifiedIdeograph.txt" => ArtifactKind.EquivalentUnifiedIdeograph,
            "ucd/extracted/DerivedName.txt" => ArtifactKind.DerivedName,
            "ucd/extracted/DerivedDecompositionType.txt" => ArtifactKind.DerivedDecompositionType,
            "ucd/extracted/DerivedJoiningGroup.txt" => ArtifactKind.DerivedJoiningGroup,
            "ucd/extracted/DerivedNumericValues.txt" => ArtifactKind.DerivedNumericValues,
            "ucd/CompositionExclusions.txt" => ArtifactKind.CompositionExclusions,
            "ucd/NamedSequences.txt" or "ucd/NamedSequencesProv.txt" => ArtifactKind.NamedSequences,
            "ucd/StandardizedVariants.txt" => ArtifactKind.StandardizedVariants,
            "ucd/emoji/emoji-variation-sequences.txt" => ArtifactKind.EmojiVariationSequences,
            "ucd/EmojiSources.txt" => ArtifactKind.EmojiSources,
            "ucd/NushuSources.txt" or "ucd/TangutSources.txt" or "ucd/Unikemet.txt"
                => ArtifactKind.TabbedCodepointProperties,
            "ucd/CJKRadicals.txt" => ArtifactKind.CjkRadicals,
            "ucd/DoNotEmit.txt" => ArtifactKind.DoNotEmit,
            "ucd/PropertyAliases.txt" => ArtifactKind.PropertyAliases,
            "ucd/PropertyValueAliases.txt" => ArtifactKind.PropertyValueAliases,
            "ucd/Index.txt" => ArtifactKind.IndexTerms,
            "ucd/USourceData.txt" => ArtifactKind.USourceData,
            "emoji/emoji-sequences.txt" or "emoji/emoji-zwj-sequences.txt"
                => ArtifactKind.EmojiSequences,
            "idna/Idna2008.txt" => ArtifactKind.Idna2008,
            "idna/IdnaMappingTable.txt" => ArtifactKind.IdnaMapping,
            "security/intentional.txt" => ArtifactKind.IntentionalConfusables,
            "uca/decomps.txt" => ArtifactKind.UcaDecompositions,
            "uca/ctt.txt" => ArtifactKind.UcaCommonTemplate,
            "ucd/NamesList.txt" => ArtifactKind.NamesList,
            "linkification/LinkBracket.txt" => ArtifactKind.LinkBracket,
            "linkification/LinkEmail.txt" => ArtifactKind.LinkEmail,
            "linkification/LinkTerm.txt" => ArtifactKind.LinkTerm,
            _ when (relative.StartsWith("ucd/Unihan/", StringComparison.Ordinal)
                    || relative.StartsWith("ucd/Unihan_", StringComparison.Ordinal))
                && relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                => ArtifactKind.UnihanProperties,
            _ => ArtifactKind.Unknown,
        };
        return kind != ArtifactKind.Unknown;
    }

    private static void AddIfPresent(
        List<ArtifactJob> jobs,
        ArtifactKind kind,
        string path,
        string label)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path)) jobs.Add(new ArtifactJob(kind, path, label));
    }

    private static bool IsSingletonArtifactRole(ArtifactKind kind) =>
        kind is not ArtifactKind.BinaryProperties
            and not ArtifactKind.UnihanProperties
            and not ArtifactKind.NamedSequences
            and not ArtifactKind.TabbedCodepointProperties
            and not ArtifactKind.EmojiSequences
            and not ArtifactKind.BoundaryTest
            and not ArtifactKind.NormalizationTest
            and not ArtifactKind.EmojiTest;

    private static void AddUnihanFiles(List<ArtifactJob> jobs, string baseDir)
    {
        string ucd = Path.Combine(baseDir, "ucd");
        var paths = new List<string>();
        if (Directory.Exists(ucd))
            paths.AddRange(Directory.EnumerateFiles(
                ucd, "Unihan_*.txt", SearchOption.TopDirectoryOnly));
        string dir = Path.Combine(ucd, "Unihan");
        if (Directory.Exists(dir))
            paths.AddRange(Directory.EnumerateFiles(
                dir, "*.txt", SearchOption.TopDirectoryOnly));

        foreach (string path in paths.Distinct(StringComparer.Ordinal)
                                     .OrderBy(static p => p, StringComparer.Ordinal))
        {
            string label = Path.GetRelativePath(baseDir, path).Replace('\\', '/');
            AddIfPresent(jobs, ArtifactKind.UnihanProperties, path, label);
        }
    }

    private static void AddFirstPresent(
        List<ArtifactJob> jobs,
        ArtifactKind kind,
        params (string Path, string Label)[] candidates)
    {
        foreach ((string path, string label) in candidates)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) continue;
            jobs.Add(new ArtifactJob(kind, full, label));
            return;
        }
    }

    private enum ArtifactKind
    {
        Ducet = 0,
        UcdXml = 1,
        UnicodeData = 2,
        Scripts = 3,
        Blocks = 4,
        DerivedAge = 5,
        LineBreak = 6,
        EastAsianWidth = 7,
        JoiningType = 8,
        NumericType = 9,
        BidiMirroring = 10,
        EmojiData = 11,
        NameAliases = 12,
        Confusables = 13,
        DerivedNormalization = 14,
        ScriptExtensions = 15,
        BinaryProperties = 16,
        GraphemeBreak = 17,
        WordBreak = 18,
        SentenceBreak = 19,
        IndicConjunctBreak = 20,
        HangulSyllableType = 21,
        VerticalOrientation = 22,
        IndicPositionalCategory = 23,
        IndicSyllabicCategory = 24,
        DerivedGeneralCategory = 25,
        DerivedCombiningClass = 26,
        DerivedBidiClass = 27,
        IdentifierStatus = 28,
        IdentifierType = 29,
        UnihanProperties = 30,
        ArabicShaping = 31,
        BidiBrackets = 32,
        CaseFolding = 33,
        SpecialCasing = 34,
        Jamo = 35,
        NormalizationCorrections = 36,
        EquivalentUnifiedIdeograph = 37,
        DerivedName = 38,
        DerivedDecompositionType = 39,
        DerivedJoiningGroup = 40,
        DerivedNumericValues = 41,
        CompositionExclusions = 42,
        NamedSequences = 43,
        StandardizedVariants = 44,
        EmojiVariationSequences = 45,
        EmojiSources = 46,
        TabbedCodepointProperties = 47,
        CjkRadicals = 48,
        DoNotEmit = 49,
        PropertyAliases = 50,
        PropertyValueAliases = 51,
        IndexTerms = 52,
        USourceData = 53,
        EmojiSequences = 54,
        Idna2008 = 55,
        IdnaMapping = 56,
        IntentionalConfusables = 57,
        UcaDecompositions = 58,
        UcaCommonTemplate = 59,
        NamesList = 60,
        LinkBracket = 61,
        LinkEmail = 62,
        LinkTerm = 63,
        BoundaryTest = 64,
        NormalizationTest = 65,
        EmojiTest = 66,
        Unknown = int.MaxValue,
    }

    private static bool IsRepertoireCompensation(ArtifactKind kind) => kind is
        ArtifactKind.UnicodeData
        or ArtifactKind.Scripts
        or ArtifactKind.DerivedAge
        or ArtifactKind.LineBreak
        or ArtifactKind.EastAsianWidth
        or ArtifactKind.JoiningType
        or ArtifactKind.NumericType
        or ArtifactKind.BidiMirroring
        or ArtifactKind.EmojiData
        or ArtifactKind.NameAliases
        or ArtifactKind.DerivedNormalization
        or ArtifactKind.ScriptExtensions
        or ArtifactKind.BinaryProperties
        or ArtifactKind.GraphemeBreak
        or ArtifactKind.WordBreak
        or ArtifactKind.SentenceBreak
        or ArtifactKind.IndicConjunctBreak
        or ArtifactKind.HangulSyllableType
        or ArtifactKind.VerticalOrientation
        or ArtifactKind.IndicPositionalCategory
        or ArtifactKind.IndicSyllabicCategory
        or ArtifactKind.DerivedGeneralCategory
        or ArtifactKind.DerivedCombiningClass
        or ArtifactKind.DerivedBidiClass
        or ArtifactKind.UnihanProperties
        or ArtifactKind.BidiBrackets
        or ArtifactKind.CaseFolding
        or ArtifactKind.Jamo
        or ArtifactKind.DerivedName
        or ArtifactKind.DerivedDecompositionType
        or ArtifactKind.DerivedJoiningGroup
        or ArtifactKind.DerivedNumericValues
        or ArtifactKind.CompositionExclusions
        or ArtifactKind.TabbedCodepointProperties;

    private readonly record struct ArtifactJob(ArtifactKind Kind, string Path, string Label);

    private abstract class UnicodeComposePhase<T> : ComposeDecomposerPhase<T>
    {
        private readonly int _commitEpoch;
        private readonly int? _attestationCapacity;

        protected UnicodeComposePhase(int batch, int commitEpoch = 0, int? attestationCapacity = null)
        {
            _commitEpoch = commitEpoch;
            _attestationCapacity = attestationCapacity;
        }

        public override Hash128 SourceId => Source;
        public override string SourceName => "UnicodeDecomposer";
        public override int LayerOrder => 0;
        public override Hash128 TrustClassId => TrustClass;
        protected override double SourceTrust => TC.StandardsDerived;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(
            IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override IngestBatchConfig BuildPipelineConfig(
            IDecomposerContext context,
            DecomposerOptions options) =>
            IngestPipelineDefaults.ApplyMaxInputUnits(
                IngestPipelineDefaults.Compose(
                    SourceId,
                    BatchLabelPrefix,
                    options,
                    context.Reader,
                    IngestSourceProfile.Unicode,
                    attestationCapacity: _attestationCapacity,
                    commitEpoch: _commitEpoch),
                options);
    }

    private readonly record struct UnicodeSeedRange(int First, int Count);

    private sealed class UnicodeFloorPhase : UnicodeComposePhase<UnicodeSeedRange>
    {
        private readonly UnicodeSeedSnapshot _snapshot;
        private readonly int _rangeSize;

        public UnicodeFloorPhase(UnicodeSeedSnapshot snapshot, int rangeSize)
            : base(Math.Max(1, rangeSize), attestationCapacity: 0)
        {
            _snapshot = snapshot;
            _rangeSize = Math.Max(1, rangeSize);
        }

        protected override string PhaseLabel => "unicode-floor";

        protected override long UnitsPerRecord(UnicodeSeedRange range) => range.Count;

        protected override void Compose(UnicodeSeedRange range, SubstrateChangeBuilder builder)
        {
            _snapshot.StageRange(builder.ContentStage, range.First, range.Count, Source);

            int end = checked(range.First + range.Count);
            if (range.First == 0) EmitByteCatalog(builder);
            int byteFirst = Math.Max(range.First, ByteAtoms.First);
            int byteEnd = Math.Min(end, byte.MaxValue + 1);
            for (int cp = byteFirst; cp < byteEnd; ++cp)
                EmitByte(builder, checked((byte)cp));
        }

        protected override async IAsyncEnumerable<UnicodeSeedRange> ExtractRecordsAsync(
            string ecosystemPath,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            int remaining = options.MaxInputUnits > 0
                ? checked((int)Math.Min(options.MaxInputUnits, _snapshot.Count))
                : _snapshot.Count;
            for (int first = 0; first < remaining;)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(_rangeSize, remaining - first);
                yield return new UnicodeSeedRange(first, count);
                first += count;
                await Task.Yield();
            }
        }

        private static void EmitByteCatalog(SubstrateChangeBuilder builder)
        {
            Hash128 latin1 = SubstrateCanonicalIds.OfVersioned("encoding", "ISO-8859-1");
            Hash128 cp1252 = SubstrateCanonicalIds.OfVersioned("encoding", "windows-1252");
            Hash128 encodingType = EntityTypeRegistry.CharacterEncoding;
            Hash128 roleType = EntityTypeRegistry.Utf8Role;
            builder.AddEntity(new EntityRow(latin1, EntityTier.Word, encodingType, Source));
            builder.AddEntity(new EntityRow(cp1252, EntityTier.Word, encodingType, Source));
            foreach (string role in new[] { "continuation", "lead2", "lead3", "lead4", "invalid" })
            {
                Hash128 roleId = Hash128.OfCanonical($"substrate/utf8/{role}/v1");
                builder.AddEntity(new EntityRow(roleId, EntityTier.Word, roleType, Source));
            }
        }

        private static void EmitByte(SubstrateChangeBuilder builder, byte value)
        {
            Hash128 byteId = ByteAtoms.Id(value);
            builder.AddEntity(byteId, tier: 0, ByteAtoms.TypeId, firstObservedBy: Source);
            ReadOnlySpan<double> coord = ByteAtoms.Coord(value);
            Hash128 physicalityId = PhysicalityId.Compute(byteId, PhysicalityType.Content);
            builder.AddPhysicality(new PhysicalityRow(
                Id: physicalityId,
                EntityId: byteId,
                SourceId: Source,
                Type: PhysicalityType.Content,
                CoordX: coord[0],
                CoordY: coord[1],
                CoordZ: coord[2],
                CoordM: coord[3],
                HilbertIndex: ByteAtoms.Hilbert(value),
                TrajectoryXyzm: null,
                NConstituents: 0,
                AlignmentResidual: null,
                SourceDim: null,
                ObservedAtUnixUs: 0));

            Hash128 roleId = Hash128.OfCanonical($"substrate/utf8/{ByteAtoms.Utf8Role(value)}/v1");
            builder.AddAttestation(NativeAttestation.Categorical(
                byteId, "HAS_UTF8_ROLE", roleId, Source, TC.StandardsDerived));

            Hash128 latin1 = SubstrateCanonicalIds.OfVersioned("encoding", "ISO-8859-1");
            Hash128 cp1252 = SubstrateCanonicalIds.OfVersioned("encoding", "windows-1252");
            builder.AddAttestation(NativeAttestation.Categorical(
                byteId, "DECODES_TO", CodepointId(value), Source,
                TC.StandardsDerived, contextId: latin1));

            uint cp1252Target = value <= 0x9F
                ? ByteAtoms.Cp1252High[value - 0x80]
                : value;
            if (cp1252Target != 0)
                builder.AddAttestation(NativeAttestation.Categorical(
                    byteId, "DECODES_TO", CodepointId(cp1252Target), Source,
                    TC.StandardsDerived, contextId: cp1252));
        }
    }

    private enum UcdXmlSemanticKind
    {
        Repertoire,
        Block,
        NamedSequence,
        StandardizedVariant,
        CjkRadical,
        DoNotEmit,
    }

    private readonly record struct UcdXmlSemanticRecord(
        XmlRecordNode Node,
        UcdXmlSemanticKind Kind,
        uint Start,
        uint End,
        bool CountsSourceRow,
        long EstimatedRows);

    private sealed class UcdXmlSemanticPhase : UnicodeComposePhase<UcdXmlSemanticRecord>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly UnicodeSeedSnapshot _snapshot;
        private readonly UcdXmlRecipe _recipe;
        private readonly LaplaceRecipeInterpreter<UcdXmlSemanticRecord> _interpreter;
        private bool _ownsSnapshot = true;

        public UcdXmlSemanticPhase(
            UnicodeDecomposer owner,
            UnicodeSeedSnapshot snapshot,
            UcdXmlRecipe recipe,
            int batch)
            : base(batch, commitEpoch: 1)
        {
            _owner = owner;
            _snapshot = snapshot;
            _recipe = recipe;
            _interpreter = new LaplaceRecipeInterpreter<UcdXmlSemanticRecord>(recipe.Recipe);
        }

        protected override string PhaseLabel => "ucdxml/uax42";

        protected override long UnitsPerRecord(UcdXmlSemanticRecord row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override long EstimatedOutputRows(UcdXmlSemanticRecord row) =>
            row.EstimatedRows;

        protected override void Compose(
            UcdXmlSemanticRecord row,
            SubstrateChangeBuilder builder)
        {
            if (row.Kind != UcdXmlSemanticKind.Repertoire)
            {
                ComposeStructure(row, builder);
                return;
            }

            var target = new UcdXmlLoweringTarget(_owner, builder);
            foreach (XmlRecordAttribute attribute in row.Node.Attributes)
            {
                if (attribute.Name is "cp" or "first-cp" or "last-cp") continue;
                _interpreter.Lower(
                    new SourceRecipeAssertion<UcdXmlSemanticRecord>(
                        row, $"repertoire/*/@{attribute.Name}", attribute.Value),
                    target);
            }

            foreach (XmlRecordNode alias in row.Node.ChildrenNamed("name-alias"))
            {
                string value = alias.Attribute("alias");
                if (value.Length == 0) continue;
                Hash128? aliasId = ContentEmitter.Emit(builder, value, Source);
                if (aliasId is null) continue;
                string aliasType = alias.Attribute("type", "unknown");
                Hash128 context = _owner.ClassifierEntity(
                    builder, "unicode/name_alias_type", aliasType);
                target.Add(
                    RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS"),
                    aliasId.Value, context);
            }
            target.Flush(row.Start, row.End);
        }

        private void ComposeStructure(
            UcdXmlSemanticRecord row,
            SubstrateChangeBuilder builder)
        {
            string identity = row.Kind switch
            {
                UcdXmlSemanticKind.Block => row.Node.Attribute("name"),
                UcdXmlSemanticKind.NamedSequence => row.Node.Attribute("cps"),
                UcdXmlSemanticKind.StandardizedVariant => row.Node.Attribute("cps"),
                UcdXmlSemanticKind.CjkRadical => row.Node.Attribute("number"),
                UcdXmlSemanticKind.DoNotEmit => row.Node.Attribute("of"),
                _ => throw new InvalidOperationException($"Unexpected UCDXML structure {row.Kind}."),
            };
            if (identity.Length == 0)
                throw new InvalidDataException(
                    $"UCDXML {row.Node.Name} record is missing its recipe identity field.");

            Hash128? subject = row.Kind switch
            {
                UcdXmlSemanticKind.Block => _owner.ClassifierEntity(
                    builder, "unicode/block",
                    _recipe.CanonicalValue("Block", identity)),
                UcdXmlSemanticKind.NamedSequence or UcdXmlSemanticKind.StandardizedVariant
                    or UcdXmlSemanticKind.DoNotEmit => EmitXmlCodepointSequence(builder, identity),
                UcdXmlSemanticKind.CjkRadical => _owner.ClassifierEntity(
                    builder, "unicode/cjk_radical", identity),
                _ => null,
            };
            if (subject is null) return;

            string prefix = row.Kind switch
            {
                UcdXmlSemanticKind.Block => "blocks/block",
                UcdXmlSemanticKind.NamedSequence => "named-sequences/named-sequence",
                UcdXmlSemanticKind.StandardizedVariant =>
                    "standardized-variants/standardized-variant",
                UcdXmlSemanticKind.CjkRadical => "cjk-radicals/cjk-radical",
                UcdXmlSemanticKind.DoNotEmit => "do-not-emit/instead",
                _ => throw new InvalidOperationException($"Unexpected UCDXML structure {row.Kind}."),
            };
            var target = new UcdXmlEntityLoweringTarget(_owner, builder, subject.Value);
            foreach (XmlRecordAttribute attribute in row.Node.Attributes)
                _interpreter.Lower(
                    new SourceRecipeAssertion<UcdXmlSemanticRecord>(
                        row, $"{prefix}/@{attribute.Name}", attribute.Value),
                    target);

            if (row.Kind == UcdXmlSemanticKind.Block)
            {
                RelationTypeRegistry.RelationTypeResolution relation =
                    _owner.PropertyRelation(builder, "Block");
                NativeAttestation.AddCodepointRange(
                    builder.ContentStage, row.Start, row.End, relation.Id,
                    subject.Value, Source, contextId: null,
                    sourceTrust: TC.StandardsDerived);
            }
        }

        protected override async IAsyncEnumerable<UcdXmlSemanticRecord> ExtractRecordsAsync(
            string ecosystemPath,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await using Stream xml = _snapshot.OpenUcdXmlStream();
            await foreach (XmlRecordFrame frame in XmlRecordReader.ReadAsync(
                               xml, recordDepth: 2, ct: ct).ConfigureAwait(false))
            {
                if (frame.Kind != XmlRecordFrameKind.Record) continue;
                UcdXmlSemanticKind? kind = frame.Node.Name switch
                {
                    "char" or "reserved" or "noncharacter" or "surrogate" =>
                        UcdXmlSemanticKind.Repertoire,
                    "block" => UcdXmlSemanticKind.Block,
                    "named-sequence" => UcdXmlSemanticKind.NamedSequence,
                    "standardized-variant" => UcdXmlSemanticKind.StandardizedVariant,
                    "cjk-radical" => UcdXmlSemanticKind.CjkRadical,
                    "instead" => UcdXmlSemanticKind.DoNotEmit,
                    _ => null,
                };
                if (kind is null) continue;

                if (kind != UcdXmlSemanticKind.Repertoire)
                {
                    uint structureStart = 0;
                    uint structureEnd = 0;
                    if (kind == UcdXmlSemanticKind.Block
                        && !TryRange(frame.Node, out structureStart, out structureEnd))
                        throw new InvalidDataException(
                            "UCDXML block has no valid first-cp/last-cp identity.");
                    yield return new UcdXmlSemanticRecord(
                        frame.Node, kind.Value, structureStart, structureEnd,
                        CountsSourceRow: true,
                        EstimatedRows: EstimateStructureRows(frame.Node));
                    continue;
                }

                if (!TryRange(frame.Node, out uint start, out uint end))
                    throw new InvalidDataException(
                        $"UCDXML {frame.Node.Name} record has no valid cp or first-cp/last-cp identity.");

                long rowsPerCodepoint = EstimateRowsPerCodepoint(frame.Node, _recipe.Recipe);
                int transactionRows = IngestSizing.ResolveApplyTransactionRows();
                uint window = checked((uint)Math.Max(
                    1L, (transactionRows - Math.Min(transactionRows - 1L, rowsPerCodepoint))
                        / Math.Max(1L, rowsPerCodepoint)));
                bool first = true;
                for (uint cursor = start; cursor <= end;)
                {
                    uint chunkEnd = (uint)Math.Min(
                        (ulong)end, (ulong)cursor + window - 1UL);
                    long estimated = checked(
                        ((long)chunkEnd - cursor + 1L) * rowsPerCodepoint);
                    yield return new UcdXmlSemanticRecord(
                        frame.Node, kind.Value, cursor, chunkEnd, first, estimated);
                    first = false;
                    if (chunkEnd == uint.MaxValue) break;
                    cursor = chunkEnd + 1;
                }
            }
        }

        public override ValueTask DisposeAsync()
        {
            if (_ownsSnapshot)
            {
                _ownsSnapshot = false;
                _snapshot.Dispose();
            }
            return ValueTask.CompletedTask;
        }

        private static bool TryRange(XmlRecordNode node, out uint start, out uint end)
        {
            start = 0;
            end = 0;
            string cp = node.Attribute("cp");
            if (uint.TryParse(cp, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out start))
            {
                end = start;
                return end <= 0x10FFFFu;
            }
            return uint.TryParse(
                       node.Attribute("first-cp"), NumberStyles.HexNumber,
                       CultureInfo.InvariantCulture, out start)
                && uint.TryParse(
                       node.Attribute("last-cp"), NumberStyles.HexNumber,
                       CultureInfo.InvariantCulture, out end)
                && start <= end && end <= 0x10FFFFu;
        }

        private static long EstimateRowsPerCodepoint(
            XmlRecordNode node,
            SemanticSourceRecipe recipe)
        {
            long rows = 0;
            foreach (XmlRecordAttribute attribute in node.Attributes)
            {
                if (attribute.Name is "cp" or "first-cp" or "last-cp") continue;
                SourceRecipeField field = recipe.Field($"repertoire/*/@{attribute.Name}");
                if (!field.Disposition.HasFlag(SourceFieldDisposition.Testimony)
                    || (field.AbsentSentinel is not null
                        && attribute.Value.Equals(field.AbsentSentinel, StringComparison.Ordinal)))
                    continue;
                long valueRows = field.ValueKind switch
                {
                    SourceValueKind.CodepointSequence or SourceValueKind.EnumeratedSequence =>
                        Math.Max(1, attribute.Value.Split(
                            field.SequenceSeparator ?? " ",
                            StringSplitOptions.RemoveEmptyEntries).LongLength),
                    SourceValueKind.Text or SourceValueKind.StructuredText =>
                        Math.Max(1, Encoding.UTF8.GetByteCount(attribute.Value) * 4L),
                    _ => 2,
                };
                rows = checked(rows + valueRows);
            }
            foreach (XmlRecordNode alias in node.ChildrenNamed("name-alias"))
                rows = checked(rows + Math.Max(2, Encoding.UTF8.GetByteCount(alias.Attribute("alias")) * 4L));
            return Math.Max(1, rows);
        }

        private static long EstimateStructureRows(XmlRecordNode node)
        {
            long rows = 1;
            foreach (XmlRecordAttribute attribute in node.Attributes)
                rows = checked(rows + Math.Max(
                    2L, Encoding.UTF8.GetByteCount(attribute.Value) * 4L));
            return rows;
        }
    }

    private static Hash128? EmitXmlCodepointSequence(
        SubstrateChangeBuilder builder,
        string raw)
    {
        string[] tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = new StringBuilder(tokens.Length);
        foreach (string token in tokens)
        {
            if (!uint.TryParse(
                    token, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out uint codepoint)
                || !Rune.TryCreate(codepoint, out Rune rune))
                throw new InvalidDataException(
                    $"UCDXML has invalid sequence codepoint '{token}' in '{raw}'.");
            text.Append(rune.ToString());
        }
        return ContentEmitter.Emit(builder, text.ToString(), Source);
    }

    private sealed class UcdXmlEntityLoweringTarget(
        UnicodeDecomposer owner,
        SubstrateChangeBuilder builder,
        Hash128 subjectId)
        : ILaplaceRecipeLoweringTarget<UcdXmlSemanticRecord>
    {
        public void Lower(
            in SourceRecipeAssertion<UcdXmlSemanticRecord> assertion,
            SourceRecipeField field,
            in SourceRecipeValue value)
        {
            if (value.IsAbsent || value.Raw.Length == 0
                || !field.Disposition.HasFlag(SourceFieldDisposition.Testimony))
                return;

            RelationTypeRegistry.RelationTypeResolution relation =
                owner.PropertyRelation(builder, field.PropertyName);
            if (field.ValueKind == SourceValueKind.Boolean)
            {
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    subjectId, relation.Id, obj: null, Source, contextId: null,
                    witnessWeight: RelationTypeRank.StandardsStructural * TC.StandardsDerived,
                    confirm: value.Boolean
                        ?? throw new InvalidDataException(
                            $"Boolean recipe value missing for {field.SyntaxPath}.")));
                return;
            }

            if (field.ValueKind == SourceValueKind.EnumeratedSequence)
            {
                foreach (string item in value.Sequence)
                    Emit(owner.PropertyValueEntity(builder, field.PropertyName, item));
                return;
            }

            Hash128? objectId = field.ValueKind switch
            {
                SourceValueKind.Codepoint => ParseXmlCodepoint(field, value.Raw),
                SourceValueKind.CodepointSequence => EmitXmlCodepointSequence(builder, value.Raw),
                SourceValueKind.Text or SourceValueKind.StructuredText =>
                    ContentEmitter.Emit(builder, value.Raw, Source),
                _ => owner.PropertyValueEntity(builder, field.PropertyName, value.Raw),
            };
            if (objectId is { } resolved) Emit(resolved);

            void Emit(Hash128 resolved) =>
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    subjectId, relation.Id, resolved, Source, contextId: null,
                    witnessWeight: RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }
    }

    private static Hash128 ParseXmlCodepoint(SourceRecipeField field, string raw)
    {
        if (!uint.TryParse(
                raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out uint codepoint) || codepoint > 0x10FFFFu)
            throw new InvalidDataException(
                $"UCDXML field '{field.SyntaxPath}' has invalid codepoint '{raw}'.");
        return CodepointId(codepoint);
    }

    private sealed class UcdXmlLoweringTarget(
        UnicodeDecomposer owner,
        SubstrateChangeBuilder builder)
        : ILaplaceRecipeLoweringTarget<UcdXmlSemanticRecord>
    {
        private readonly List<NativeAttestation.CodepointRangeRelation> _relations = [];

        internal void Add(
            Hash128 typeId,
            Hash128? objectId,
            Hash128? contextId = null,
            bool confirm = true) =>
            _relations.Add(new NativeAttestation.CodepointRangeRelation(
                typeId, objectId, contextId, confirm));

        internal void Flush(uint start, uint end)
        {
            NativeAttestation.AddCodepointRangeRelations(
                builder.ContentStage, start, end,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_relations),
                Source, TC.StandardsDerived);
            _relations.Clear();
        }

        public void Lower(
            in SourceRecipeAssertion<UcdXmlSemanticRecord> assertion,
            SourceRecipeField field,
            in SourceRecipeValue value)
        {
            if (value.IsAbsent || value.Raw.Length == 0) return;
            RelationTypeRegistry.RelationTypeResolution relation =
                owner.PropertyRelation(builder, field.PropertyName);
            UcdXmlSemanticRecord subject = assertion.Subject;
            string rawValue = value.Raw;

            if (field.ValueKind == SourceValueKind.Boolean)
            {
                Add(
                    relation.Id, objectId: null, contextId: null,
                    confirm: value.Boolean
                        ?? throw new InvalidDataException($"Boolean recipe value missing for {field.SyntaxPath}."));
                return;
            }

            if (field.ValueKind == SourceValueKind.StructuredText
                && field.Disposition.HasFlag(SourceFieldDisposition.Reference))
            {
                EmitStructuredReferences();
                return;
            }

            if (field.ValueKind == SourceValueKind.EnumeratedSequence)
            {
                foreach (string item in value.Sequence)
                    EmitObject(owner.PropertyValueEntity(builder, field.PropertyName, item));
                return;
            }

            Hash128? objectId = field.ValueKind switch
            {
                SourceValueKind.Codepoint => ParseXmlCodepoint(field, value.Raw),
                SourceValueKind.CodepointSequence => EmitXmlCodepointSequence(builder, value.Raw),
                SourceValueKind.Text or SourceValueKind.StructuredText =>
                    ContentEmitter.Emit(builder, value.Raw, Source),
                _ => owner.PropertyValueEntity(builder, field.PropertyName, value.Raw),
            };
            if (objectId is { } resolved) EmitObject(resolved);

            void EmitStructuredReferences()
            {
                Hash128? exact = ContentEmitter.Emit(builder, rawValue, Source);
                if (exact is { } exactId)
                {
                    RelationTypeRegistry.RelationTypeResolution lexical =
                        owner.PropertyRelation(builder, $"{field.PropertyName}_Source_Text");
                    Add(lexical.Id, exactId);
                }

                foreach (UcdXmlRecipe.StructuredCodepointReference reference
                         in UcdXmlRecipe.ParseStructuredReferences(field.SyntaxPath, rawValue))
                {
                    Hash128? context = reference.Qualifier is null
                        ? null
                        : ContentEmitter.Emit(builder, reference.Qualifier, Source);
                    Add(relation.Id, CodepointId(reference.Codepoint), context);
                }
            }

            void EmitObject(Hash128 resolved) => Add(relation.Id, resolved);

        }
    }

    private sealed class UnicodeDataPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.UnicodeDataRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public UnicodeDataPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "ucd/UnicodeData";

        protected override void Compose(
            UnicodePhysicalArtifactParser.UnicodeDataRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 entityId = CodepointId(row.Codepoint);
            double structural = RelationTypeRank.StandardsStructural * TC.StandardsDerived;

            if (row.Name is { Length: > 0 } name
                && ContentEmitter.Emit(builder, name, Source) is { } nameId)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasName, nameId,
                    Source, null, structural));

            if (row.GeneralCategory.Length > 0)
            {
                Hash128 categoryId = _owner.ClassifierEntity(
                    builder, "unicode/category", row.GeneralCategory);
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasGeneralCategory, categoryId,
                    Source, null, structural));
            }

            if (row.CombiningClass > 0)
            {
                Hash128 ccId = _owner.ClassifierEntity(
                    builder, "unicode/combining_class",
                    row.CombiningClass.ToString(CultureInfo.InvariantCulture));
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasCombiningClass, ccId,
                    Source, null, structural));
            }

            if (row.BidiClass.Length > 0)
            {
                Hash128 bidiId = _owner.ClassifierEntity(
                    builder, "unicode/bidi_class", row.BidiClass);
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasBidiClass, bidiId,
                    Source, null, structural));
            }

            if (row.NumericValue is { Length: > 0 } numeric)
            {
                Hash128 numericId = _owner.ClassifierEntity(
                    builder, "unicode/numeric", numeric);
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasNumericValue, numericId,
                    Source, null, RelationTypeRank.ScalarValued * TC.StandardsDerived));
            }

            if (row.UppercaseMapping != 0)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasUppercaseMapping,
                    CodepointId(row.UppercaseMapping), Source, null, structural));
            if (row.LowercaseMapping != 0)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasLowercaseMapping,
                    CodepointId(row.LowercaseMapping), Source, null, structural));
            if (row.TitlecaseMapping != 0)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasTitlecaseMapping,
                    CodepointId(row.TitlecaseMapping), Source, null, structural));

            EmitDecomposition(
                builder, entityId, row.CanonicalDecomposition,
                UcdProperties.RelTypeCanonDecomposesTo, structural);
            EmitDecomposition(
                builder, entityId, row.CompatibilityDecomposition,
                UcdProperties.RelTypeCompatDecomposesTo, structural);
        }

        private static void EmitDecomposition(
            SubstrateChangeBuilder builder,
            Hash128 subject,
            uint[]? decomposition,
            Hash128 relation,
            double weight)
        {
            if (decomposition is null || decomposition.Length == 0) return;
            EnsureOrdinalContexts(builder);
            for (int index = 0; index < decomposition.Length; ++index)
            {
                Hash128 context = index == 0
                    ? UcdProperties.OrdinalCtx0
                    : UcdProperties.OrdinalCtx1;
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    subject, relation, CodepointId(decomposition[index]),
                    Source, context, weight));
            }
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.UnicodeDataRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.UnicodeDataAsync(_path, ct);
    }

    private sealed class RangePropertyPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.RangeRecord>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        private readonly string _label;
        private readonly Hash128 _relation;
        private readonly string _canonicalPrefix;
        private readonly HashSet<string>? _allowed;

        public RangePropertyPhase(
            UnicodeDecomposer owner,
            string path,
            string label,
            Hash128 relation,
            string canonicalPrefix,
            int batch,
            HashSet<string>? allowed = null)
            : base(batch)
        {
            _owner = owner;
            _path = path;
            _label = label;
            _relation = relation;
            _canonicalPrefix = canonicalPrefix;
            _allowed = allowed;
        }

        protected override string PhaseLabel => _label;

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.RangeRecord row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override long EstimatedOutputRows(UnicodePhysicalArtifactParser.RangeRecord row) =>
            _allowed is not null && !_allowed.Contains(row.Value)
                ? 0
                : checked((long)row.End - row.Start + 2); // classifier + one edge/codepoint

        protected override void Compose(
            UnicodePhysicalArtifactParser.RangeRecord row,
            SubstrateChangeBuilder builder)
        {
            if (_allowed is not null && !_allowed.Contains(row.Value)) return;
            Hash128 valueId = _owner.ClassifierEntity(
                builder, _canonicalPrefix, row.Value);
            NativeAttestation.AddCodepointRange(
                builder.ContentStage, row.Start, row.End,
                _relation, valueId, Source, contextId: null,
                sourceTrust: TC.StandardsDerived);
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.RangeRecord>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.RangeRecordsAsync(_path, ct);
    }


    private sealed class BinaryPropertyPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.BinaryPropertyRange>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public BinaryPropertyPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => $"binary-properties/{Path.GetFileNameWithoutExtension(_path)}";

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.BinaryPropertyRange row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override long EstimatedOutputRows(
            UnicodePhysicalArtifactParser.BinaryPropertyRange row) =>
            checked((long)row.End - row.Start + 2); // property relation type + edge range

        protected override void Compose(
            UnicodePhysicalArtifactParser.BinaryPropertyRange row,
            SubstrateChangeBuilder builder)
        {
            RelationTypeRegistry.RelationTypeResolution relation =
                _owner.PropertyRelation(builder, row.Property);
            NativeAttestation.AddCodepointRange(
                builder.ContentStage, row.Start, row.End,
                relation.Id, objectId: null, sourceId: Source,
                contextId: null, sourceTrust: TC.StandardsDerived,
                confirm: true);
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.BinaryPropertyRange>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.BinaryPropertyRangesAsync(_path, ct);
    }

    private sealed class ContextualRangePropertyPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.RangeRecord>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        private readonly string _property;

        public ContextualRangePropertyPhase(
            UnicodeDecomposer owner,
            string path,
            int batch,
            string property)
            : base(batch) => (_owner, _path, _property) = (owner, path, property);

        protected override string PhaseLabel => $"property/{_property}";

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.RangeRecord row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override long EstimatedOutputRows(UnicodePhysicalArtifactParser.RangeRecord row) =>
            checked((long)row.End - row.Start + 2); // value + one edge/codepoint

        protected override void Compose(
            UnicodePhysicalArtifactParser.RangeRecord row,
            SubstrateChangeBuilder builder)
        {
            RelationTypeRegistry.RelationTypeResolution relation =
                _owner.PropertyRelation(builder, _property);
            Hash128 valueId = _owner.ClassifierEntity(
                builder, $"unicode/property_value/{_property}", row.Value);
            NativeAttestation.AddCodepointRange(
                builder.ContentStage, row.Start, row.End,
                relation.Id, valueId, Source,
                contextId: null, sourceTrust: TC.StandardsDerived);
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.RangeRecord>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.RangeRecordsAsync(_path, ct);
    }

    private sealed class ScriptExtensionsPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.RangeRecord>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public ScriptExtensionsPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "script-extensions";

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.RangeRecord row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override long EstimatedOutputRows(UnicodePhysicalArtifactParser.RangeRecord row)
        {
            int values = row.Value.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return checked(values * ((long)row.End - row.Start + 2));
        }

        protected override void Compose(
            UnicodePhysicalArtifactParser.RangeRecord row,
            SubstrateChangeBuilder builder)
        {
            foreach (string script in row.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Hash128 scriptId = _owner.ClassifierEntity(
                    builder, "unicode/script", script);
                NativeAttestation.AddCodepointRange(
                    builder.ContentStage, row.Start, row.End,
                    UcdProperties.RelTypeUsesScriptExtension, scriptId, Source,
                    contextId: null, sourceTrust: TC.StandardsDerived);
            }
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.RangeRecord>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.RangeRecordsAsync(
                _path,
                ct,
                outputRowsPerCodepoint: static value => value.Split(
                    ' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private sealed class UnihanPropertyPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.UnihanPropertyRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public UnihanPropertyPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => $"unihan/{Path.GetFileNameWithoutExtension(_path)}";

        protected override void Compose(
            UnicodePhysicalArtifactParser.UnihanPropertyRow row,
            SubstrateChangeBuilder builder)
        {
            RelationTypeRegistry.RelationTypeResolution relation =
                _owner.PropertyRelation(builder, row.Property);
            Hash128? valueId = ContentEmitter.Emit(builder, row.Value, Source);
            if (valueId is null) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), relation.Id,
                valueId.Value, Source, null, TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.UnihanPropertyRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.UnihanPropertiesAsync(_path, ct);
    }

    private sealed class DelimitedCodepointPropertyPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.DelimitedCodepointPropertyRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        private readonly string[] _propertyNames;
        private readonly HashSet<int>? _sequenceFields;

        public DelimitedCodepointPropertyPhase(
            UnicodeDecomposer owner,
            string path,
            int batch,
            string[] propertyNames,
            HashSet<int>? sequenceFields = null)
            : base(batch, commitEpoch: 1)
            => (_owner, _path, _propertyNames, _sequenceFields)
                = (owner, path, propertyNames, sequenceFields);

        protected override string PhaseLabel =>
            $"structured/{Path.GetFileNameWithoutExtension(_path)}";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.DelimitedCodepointPropertyRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.DelimitedCodepointPropertyRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 keyId = _owner.ClassifierEntity(
                builder, "unicode/property_key", row.Property);
            Hash128? valueId;
            if (row.ValueIsUnicodeSequence)
            {
                int first = char.ConvertToUtf32(row.Value, 0);
                int firstLength = char.IsSurrogatePair(row.Value, 0) ? 2 : 1;
                valueId = row.Value.Length == firstLength
                    ? CodepointId((uint)first)
                    : ContentEmitter.Emit(builder, row.Value, Source);
            }
            else
            {
                valueId = ContentEmitter.Emit(builder, row.Value, Source);
            }
            if (valueId is null) return;

            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasProperty,
                valueId.Value, Source, keyId,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.DelimitedCodepointPropertyRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.DelimitedCodepointPropertiesAsync(
                _path, _propertyNames, _sequenceFields, ct);
    }

    private sealed class CodepointListPropertyPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.CodepointListRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        private readonly string _property;

        public CodepointListPropertyPhase(
            UnicodeDecomposer owner,
            string path,
            int batch,
            string property)
            : base(batch) => (_owner, _path, _property) = (owner, path, property);

        protected override string PhaseLabel => $"property/{_property}";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.CodepointListRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.CodepointListRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 propertyId = _owner.ClassifierEntity(
                builder, "unicode/property", _property);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasProperty,
                propertyId, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.CodepointListRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.CodepointListAsync(_path, ct);
    }

    private sealed class NamedSequencePhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.NamedSequenceRow>
    {
        private readonly string _path;

        public NamedSequencePhase(string path, int batch)
            : base(batch, commitEpoch: 1) => _path = path;

        protected override string PhaseLabel =>
            $"named-sequences/{Path.GetFileNameWithoutExtension(_path)}";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.NamedSequenceRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.NamedSequenceRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128? subject = ContentEmitter.Emit(builder, row.Sequence, Source);
            Hash128? name = ContentEmitter.Emit(builder, row.Name, Source);
            if (subject is null || name is null) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                subject.Value, UcdProperties.RelTypeHasName, name.Value,
                Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.NamedSequenceRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.NamedSequencesAsync(_path, ct);
    }

    private sealed class SequenceMetadataPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.SequenceMetadataRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        private readonly string[] _propertyNames;

        public SequenceMetadataPhase(
            UnicodeDecomposer owner,
            string path,
            int batch,
            string[] propertyNames)
            : base(batch, commitEpoch: 1)
            => (_owner, _path, _propertyNames) = (owner, path, propertyNames);

        protected override string PhaseLabel =>
            $"sequence-metadata/{Path.GetFileNameWithoutExtension(_path)}";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.SequenceMetadataRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.SequenceMetadataRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128? subject = ContentEmitter.Emit(builder, row.Sequence, Source);
            Hash128? value = ContentEmitter.Emit(builder, row.Value, Source);
            if (subject is null || value is null) return;
            Hash128 keyId = _owner.ClassifierEntity(
                builder, "unicode/property_key", row.Property);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                subject.Value, UcdProperties.RelTypeHasProperty,
                value.Value, Source, keyId,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.SequenceMetadataRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.SequenceMetadataAsync(
                _path, _propertyNames, ct);
    }

    private sealed class CjkRadicalPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.CjkRadicalRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public CjkRadicalPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "cjk-radicals";

        protected override void Compose(
            UnicodePhysicalArtifactParser.CjkRadicalRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 radicalId = _owner.ClassifierEntity(
                builder, "unicode/cjk_radical", row.Radical);
            Hash128 radicalKey = _owner.ClassifierEntity(
                builder, "unicode/property_key", "CJK_Radical_Character");
            Hash128 unifiedKey = _owner.ClassifierEntity(
                builder, "unicode/property_key", "CJK_Unified_Ideograph");
            double weight = RelationTypeRank.StandardsStructural * TC.StandardsDerived;

            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                radicalId, UcdProperties.RelTypeHasProperty,
                CodepointId(row.RadicalCodepoint), Source, radicalKey, weight));
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                radicalId, UcdProperties.RelTypeHasProperty,
                CodepointId(row.UnifiedIdeograph), Source, unifiedKey, weight));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.CjkRadicalRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.CjkRadicalsAsync(_path, ct);
    }

    private sealed class DoNotEmitPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.DoNotEmitRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public DoNotEmitPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "do-not-emit";

        protected override void Compose(
            UnicodePhysicalArtifactParser.DoNotEmitRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128? subject = ContentEmitter.Emit(builder, row.Sequence, Source);
            Hash128? replacement = ContentEmitter.Emit(builder, row.Replacement, Source);
            if (subject is null || replacement is null) return;
            Hash128 kind = _owner.ClassifierEntity(
                builder, "unicode/do_not_emit_type", row.Kind);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                subject.Value, UcdProperties.RelTypeHasProperty,
                replacement.Value, Source, kind,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.DoNotEmitRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.DoNotEmitAsync(_path, ct);
    }

    private sealed class PropertyAliasPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.PropertyAliasRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly IReadOnlyList<UnicodePhysicalArtifactParser.PropertyAliasRow> _rows;

        public PropertyAliasPhase(
            UnicodeDecomposer owner,
            IReadOnlyList<UnicodePhysicalArtifactParser.PropertyAliasRow> rows,
            int batch)
            : base(batch, commitEpoch: 1) => (_owner, _rows) = (owner, rows);

        protected override string PhaseLabel => "property-aliases";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.PropertyAliasRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.PropertyAliasRow row,
            SubstrateChangeBuilder builder)
        {
            RelationTypeRegistry.RelationTypeResolution property =
                _owner.PropertyRelation(builder, row.CanonicalProperty);
            Hash128? aliasId = ContentEmitter.Emit(builder, row.Alias, Source);
            if (aliasId is null) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                property.Id, RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS"),
                aliasId.Value, Source, contextId: null,
                witnessWeight: RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override async IAsyncEnumerable<UnicodePhysicalArtifactParser.PropertyAliasRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (UnicodePhysicalArtifactParser.PropertyAliasRow row in _rows)
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }
            await Task.CompletedTask;
        }
    }

    private sealed class PropertyValueAliasPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.PropertyValueAliasRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly IReadOnlyList<UnicodePhysicalArtifactParser.PropertyValueAliasRow> _rows;

        public PropertyValueAliasPhase(
            UnicodeDecomposer owner,
            IReadOnlyList<UnicodePhysicalArtifactParser.PropertyValueAliasRow> rows,
            int batch)
            : base(batch, commitEpoch: 1) => (_owner, _rows) = (owner, rows);

        protected override string PhaseLabel => "property-value-aliases";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.PropertyValueAliasRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.PropertyValueAliasRow row,
            SubstrateChangeBuilder builder)
        {
            RelationTypeRegistry.RelationTypeResolution property =
                _owner.PropertyRelation(builder, row.Property);
            Hash128 valueId = _owner.PropertyValueEntity(
                builder, property.Canonical, row.CanonicalValue);
            Hash128? aliasId = ContentEmitter.Emit(builder, row.Alias, Source);
            if (aliasId is null) return;
            double weight = RelationTypeRank.StandardsStructural * TC.StandardsDerived;

            if (row.CountsSourceRow)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    property.Id, RelationTypeRegistry.RelationTypeId("HAS_MEMBER"),
                    valueId, Source, null, weight));
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                valueId, RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS"),
                aliasId.Value, Source, contextId: null, witnessWeight: weight));
        }

        protected override async IAsyncEnumerable<UnicodePhysicalArtifactParser.PropertyValueAliasRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (UnicodePhysicalArtifactParser.PropertyValueAliasRow row in _rows)
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }
            await Task.CompletedTask;
        }
    }

    private sealed class IndexTermPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.IndexTermRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public IndexTermPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "ucd-index";

        protected override void Compose(
            UnicodePhysicalArtifactParser.IndexTermRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128? termId = ContentEmitter.Emit(builder, row.Term, Source);
            if (termId is null) return;
            Hash128 context = _owner.ClassifierEntity(
                builder, "unicode/property_key", "Index_Term");
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasProperty,
                termId.Value, Source, context,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.IndexTermRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.IndexTermsAsync(_path, ct);
    }

    private sealed class USourceDataPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.USourcePropertyRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public USourceDataPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "u-source-data";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.USourcePropertyRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.USourcePropertyRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 sourceEntry = _owner.ClassifierEntity(
                builder, "unicode/u_source", row.SourceId);
            Hash128 key = _owner.ClassifierEntity(
                builder, "unicode/property_key", row.Property);
            Hash128? value = ContentEmitter.Emit(builder, row.Value, Source);
            if (value is not null)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    sourceEntry, UcdProperties.RelTypeHasProperty,
                    value.Value, Source, key,
                    RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            if (row.CountsSourceRow && row.Codepoint is { } cp)
            {
                Hash128 cpKey = _owner.ClassifierEntity(
                    builder, "unicode/property_key", "USource_Codepoint_Reference");
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    sourceEntry, UcdProperties.RelTypeHasProperty,
                    CodepointId(cp), Source, cpKey,
                    RelationTypeRank.StandardsStructural * TC.StandardsDerived));
            }
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.USourcePropertyRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.USourceDataAsync(_path, ct);
    }

    private sealed class SequencePairPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.SequencePairRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        private readonly string _contextName;

        public SequencePairPhase(
            UnicodeDecomposer owner,
            string path,
            int batch,
            string contextName)
            : base(batch, commitEpoch: 1)
            => (_owner, _path, _contextName) = (owner, path, contextName);

        protected override string PhaseLabel =>
            $"sequence-pairs/{Path.GetFileNameWithoutExtension(_path)}";

        protected override void Compose(
            UnicodePhysicalArtifactParser.SequencePairRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128? left = ContentEmitter.Emit(builder, row.Left, Source);
            Hash128? right = ContentEmitter.Emit(builder, row.Right, Source);
            if (left is null || right is null) return;
            Hash128 context = _owner.ClassifierEntity(
                builder, "unicode/property_key", _contextName);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                left.Value, UcdProperties.RelTypeConfusableWith,
                right.Value, Source, context,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.SequencePairRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.SequencePairsAsync(_path, ct);
    }

    private sealed class CttPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.CttRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public CttPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "uca/ctt";

        protected override void Compose(
            UnicodePhysicalArtifactParser.CttRow row,
            SubstrateChangeBuilder builder)
        {
            Emit("UCA_CTT_Primary", row.Primary);
            Emit("UCA_CTT_Secondary", row.Secondary);
            Emit("UCA_CTT_Tertiary", row.Tertiary);
            Emit("UCA_CTT_Quaternary", row.Quaternary);

            void Emit(string property, string value)
            {
                if (value.Length == 0) return;
                Hash128? valueId = ContentEmitter.Emit(builder, value, Source);
                if (valueId is null) return;
                Hash128 key = _owner.ClassifierEntity(
                    builder, "unicode/property_key", property);
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    CodepointId(row.Codepoint), UcdProperties.RelTypeHasProperty,
                    valueId.Value, Source, key,
                    RelationTypeRank.StandardsStructural * TC.StandardsDerived));
            }
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.CttRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.CttAsync(_path, ct);
    }

    private sealed class NamesListPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.NamesListRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public NamesListPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "ucd/names-list";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.NamesListRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.NamesListRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128? value = ContentEmitter.Emit(builder, row.Value, Source);
            if (value is null) return;
            if (row.Kind == "Name")
            {
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    CodepointId(row.Codepoint), UcdProperties.RelTypeHasName,
                    value.Value, Source, null,
                    RelationTypeRank.StandardsStructural * TC.StandardsDerived));
                return;
            }

            Hash128 key = _owner.ClassifierEntity(
                builder, "unicode/names_list_property", row.Kind);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasProperty,
                value.Value, Source, key,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.NamesListRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.NamesListAsync(_path, ct);
    }

    private sealed class BoundaryTestPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.BoundaryTestRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        private readonly string _property;

        public BoundaryTestPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1)
        {
            _owner = owner;
            _path = path;
            _property = Path.GetFileNameWithoutExtension(path) switch
            {
                "GraphemeBreakTest" => "Grapheme_Break_Test",
                "WordBreakTest" => "Word_Break_Test",
                "SentenceBreakTest" => "Sentence_Break_Test",
                "LineBreakTest" => "Line_Break_Test",
                string name => throw new InvalidOperationException(
                    $"Unknown Unicode boundary-test artifact '{name}'."),
            };
        }

        protected override string PhaseLabel => $"ucd/{_property}";

        protected override long EstimatedOutputRows(
            UnicodePhysicalArtifactParser.BoundaryTestRow row) =>
            checked(Math.Max(4L,
                (Encoding.UTF8.GetByteCount(row.Sequence)
                 + Encoding.UTF8.GetByteCount(row.Boundaries)
                 + Encoding.UTF8.GetByteCount(row.Description)) * 4L));

        protected override void Compose(
            UnicodePhysicalArtifactParser.BoundaryTestRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128? sequence = ContentEmitter.Emit(builder, row.Sequence, Source);
            Hash128? boundaries = ContentEmitter.Emit(builder, row.Boundaries, Source);
            if (sequence is null || boundaries is null) return;
            RelationTypeRegistry.RelationTypeResolution expectation =
                _owner.PropertyRelation(builder, _property);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                sequence.Value, expectation.Id, boundaries.Value,
                Source, contextId: null,
                witnessWeight: RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            if (row.Description.Length == 0) return;
            Hash128? description = ContentEmitter.Emit(builder, row.Description, Source);
            if (description is null) return;
            RelationTypeRegistry.RelationTypeResolution descriptionRelation =
                _owner.PropertyRelation(builder, $"{_property}_Description");
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                sequence.Value, descriptionRelation.Id, description.Value,
                Source, contextId: boundaries.Value,
                witnessWeight: RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.BoundaryTestRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.BoundaryTestsAsync(_path, ct);
    }

    private sealed class NormalizationTestPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.NormalizationTestRow>
    {
        private static readonly string[] Forms = ["NFC", "NFD", "NFKC", "NFKD"];
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public NormalizationTestPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "ucd/NormalizationTest";

        protected override long EstimatedOutputRows(
            UnicodePhysicalArtifactParser.NormalizationTestRow row) =>
            checked(Math.Max(8L,
                (Encoding.UTF8.GetByteCount(row.Source)
                 + Encoding.UTF8.GetByteCount(row.Nfc)
                 + Encoding.UTF8.GetByteCount(row.Nfd)
                 + Encoding.UTF8.GetByteCount(row.Nfkc)
                 + Encoding.UTF8.GetByteCount(row.Nfkd)
                 + Encoding.UTF8.GetByteCount(row.Description)) * 4L));

        protected override void Compose(
            UnicodePhysicalArtifactParser.NormalizationTestRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128? source = ContentEmitter.Emit(builder, row.Source, Source);
            if (source is null) return;
            string[] expected = [row.Nfc, row.Nfd, row.Nfkc, row.Nfkd];
            Hash128? description = row.Description.Length == 0
                ? null
                : ContentEmitter.Emit(builder, row.Description, Source);
            for (int i = 0; i < Forms.Length; ++i)
            {
                Hash128? target = ContentEmitter.Emit(builder, expected[i], Source);
                if (target is null) continue;
                RelationTypeRegistry.RelationTypeResolution relation =
                    _owner.PropertyRelation(builder, $"Normalization_Test_{Forms[i]}");
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    source.Value, relation.Id, target.Value, Source,
                    contextId: description,
                    witnessWeight: RelationTypeRank.StandardsStructural * TC.StandardsDerived));
            }
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.NormalizationTestRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.NormalizationTestsAsync(_path, ct);
    }

    private sealed class EmojiTestPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.EmojiTestRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public EmojiTestPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "emoji/emoji-test";

        protected override long EstimatedOutputRows(
            UnicodePhysicalArtifactParser.EmojiTestRow row) =>
            checked(Math.Max(8L,
                (Encoding.UTF8.GetByteCount(row.Sequence)
                 + Encoding.UTF8.GetByteCount(row.Name)
                 + Encoding.UTF8.GetByteCount(row.Group)
                 + Encoding.UTF8.GetByteCount(row.Subgroup)) * 4L));

        protected override void Compose(
            UnicodePhysicalArtifactParser.EmojiTestRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128? sequence = ContentEmitter.Emit(builder, row.Sequence, Source);
            if (sequence is null) return;
            EmitValue("Emoji_Test_Status", row.Status, content: false);
            EmitValue("Emoji_Test_Version", row.Version, content: false);
            EmitValue("Emoji_Test_Name", row.Name, content: true);
            EmitValue("Emoji_Test_Group", row.Group, content: true);
            EmitValue("Emoji_Test_Subgroup", row.Subgroup, content: true);

            void EmitValue(string property, string raw, bool content)
            {
                if (raw.Length == 0) return;
                Hash128? value = content
                    ? ContentEmitter.Emit(builder, raw, Source)
                    : _owner.PropertyValueEntity(builder, property, raw);
                if (value is null) return;
                RelationTypeRegistry.RelationTypeResolution relation =
                    _owner.PropertyRelation(builder, property);
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    sequence.Value, relation.Id, value.Value, Source,
                    contextId: null,
                    witnessWeight: RelationTypeRank.StandardsStructural * TC.StandardsDerived));
            }
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.EmojiTestRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.EmojiTestsAsync(_path, ct);
    }

    private sealed class MirrorPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.MirrorRow>
    {
        private readonly string _path;
        public MirrorPhase(string path, int batch) : base(batch) => _path = path;
        protected override string PhaseLabel => "bidi-mirroring";

        protected override void Compose(
            UnicodePhysicalArtifactParser.MirrorRow row,
            SubstrateChangeBuilder builder)
        {
            if (row.Codepoint > row.Mirror) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasMirror,
                CodepointId(row.Mirror), Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.MirrorRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.MirrorsAsync(_path, ct);
    }

    private sealed class AliasPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.AliasRow>
    {
        private readonly string _path;
        public AliasPhase(string path, int batch) : base(batch, commitEpoch: 1) => _path = path;
        protected override string PhaseLabel => "name-aliases";

        protected override void Compose(
            UnicodePhysicalArtifactParser.AliasRow row,
            SubstrateChangeBuilder builder)
        {
            if (ContentEmitter.Emit(builder, row.Alias, Source) is not { } aliasId) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasNameAlias,
                aliasId, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.AliasRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.AliasesAsync(_path, ct);
    }

    private sealed class ConfusablePhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.ConfusableRow>
    {
        private readonly string _path;
        public ConfusablePhase(string path, int batch) : base(batch, commitEpoch: 1) => _path = path;
        protected override string PhaseLabel => "confusables";

        protected override void Compose(
            UnicodePhysicalArtifactParser.ConfusableRow row,
            SubstrateChangeBuilder builder)
        {
            string target = row.Target;
            int first = char.ConvertToUtf32(target, 0);
            int firstLength = char.IsSurrogatePair(target, 0) ? 2 : 1;
            Hash128? targetId = target.Length == firstLength
                ? CodepointId((uint)first)
                : ContentEmitter.Emit(builder, target, Source);
            if (targetId is null) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeConfusableWith,
                targetId.Value, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.ConfusableRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.ConfusablesAsync(_path, ct);
    }

    private sealed class NormalizationPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.NormalizationRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        public NormalizationPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch) => (_owner, _path) = (owner, path);
        protected override string PhaseLabel => "normalization";

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.NormalizationRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.NormalizationRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 formId = _owner.ClassifierEntity(
                builder, "unicode/normalization_form", row.Form);
            double weight = RelationTypeRank.StandardsStructural * TC.StandardsDerived;
            Hash128 subject = CodepointId(row.Codepoint);
            builder.AddAttestation(row.Maybe
                ? NativeAttestation.ResolvedScored(
                    subject, UcdProperties.RelTypeHasNormalizationForm,
                    formId, Source, null, weight,
                    signedMagnitude: 0.0, arenaScale: 1.0)
                : NativeAttestation.CategoricalResolved(
                    subject, UcdProperties.RelTypeHasNormalizationForm,
                    formId, Source, null, weight, confirm: false));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.NormalizationRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.NormalizationAsync(_path, ct);
    }
}
