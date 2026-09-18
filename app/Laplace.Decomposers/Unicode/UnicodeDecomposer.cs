using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Unicode;

/// <summary>
/// Unicode/UCD ingestion at physical-artifact grain. No source is preloaded during
/// Initialize: every selected file is claimed exactly once by the shared multi-phase
/// artifact executor and streamed through parse → compose → shared apply. DUCET owns
/// tier-0 UCA geometry; UCD XML is independently parsed/validated; property tables own
/// only the claims they physically state.
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

        foreach (ArtifactJob job in jobs)
        {
            IDecomposer phase = BuildArtifactPhase(job, batch);
            await foreach (SubstrateChange change in RunPhaseAsync(
                phase, context, options, job.Label, job.Path, ct))
            {
                yield return change;
            }
        }
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
        long declared = jobs.Any(static job => job.Kind == ArtifactKind.Ducet)
            ? UnicodeSeed.CodepointCount
            : 0L;
        if (options.MaxInputUnits > 0) declared = Math.Min(declared, options.MaxInputUnits);

        var files = jobs.Select(job => new IngestFileSpec(
            job.Label,
            job.Path,
            job.Kind == ArtifactKind.Ducet ? UnicodeSeed.CodepointCount : 0L)).ToArray();
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
            _ucdxmlZip ?? Path.Combine(root, "ucdxml", "ucd.nounihan.flat.zip"));
        string ducet = Path.GetFullPath(
            _ducet ?? Path.Combine(root, "uca", "allkeys.txt"));

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
            else if (TryClassifyArtifact(full, root, xml, ducet, out _))
            {
                disposition = IngestArtifactDisposition.Admitted;
                notes = "";
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
            ArtifactKind.Ducet => new DucetTier0Phase(job.Path, batch),
            ArtifactKind.UcdXml => new UcdXmlValidationPhase(job.Path, batch),
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
            ArtifactKind.PropertyAliases => new PropertyAliasPhase(this, job.Path, batch),
            ArtifactKind.PropertyValueAliases => new PropertyValueAliasPhase(this, job.Path, batch),
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
            _ucdxmlZip ?? Path.Combine(baseDir, "ucdxml", "ucd.nounihan.flat.zip"));
        string ducet = Path.GetFullPath(
            _ducet ?? Path.Combine(baseDir, "uca", "allkeys.txt"));

        if (context.HasArtifactGraph)
        {
            var jobs = new List<ArtifactJob>(context.SelectedArtifacts.Count);
            var singletonKinds = new HashSet<ArtifactKind>();
            foreach (IngestArtifact artifact in context.SelectedArtifacts)
            {
                string path = Path.GetFullPath(artifact.Path);
                ArtifactKind kind = ClassifyArtifact(path, baseDir, xml, ducet);
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
        AddIfPresent(legacy, ArtifactKind.UcdXml, xml, "ucdxml/ucd.nounihan.flat.zip");
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
            "ucdxml/ucd.nounihan.flat.zip" or "ucdxml/ucd.nounihan.flat.xml" => ArtifactKind.UcdXml,
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
            and not ArtifactKind.EmojiSequences;

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
        Unknown = int.MaxValue,
    }

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

    private sealed class DucetTier0Phase : UnicodeComposePhase<int>
    {
        private readonly string _path;

        public DucetTier0Phase(string path, int batch)
            : base(batch, attestationCapacity: 0) => _path = path;

        protected override string PhaseLabel => "uca/allkeys";

        protected override void Compose(int cp, SubstrateChangeBuilder builder)
        {
            ReadOnlySpan<CodepointRecord> records = CodepointPerfcache.Records;
            ref readonly CodepointRecord record = ref records[cp];
            Hash128 entityId = record.Hash;
            builder.AddEntity(entityId, tier: 0, CodepointType, firstObservedBy: Source);
            Hash128 physicalityId = PhysicalityId.Compute(entityId, PhysicalityType.Content);
            builder.AddPhysicality(new PhysicalityRow(
                Id: physicalityId,
                EntityId: entityId,
                SourceId: Source,
                Type: PhysicalityType.Content,
                CoordX: record.CoordX,
                CoordY: record.CoordY,
                CoordZ: record.CoordZ,
                CoordM: record.CoordM,
                HilbertIndex: record.Hilbert,
                TrajectoryXyzm: null,
                NConstituents: 0,
                AlignmentResidual: null,
                SourceDim: null,
                ObservedAtUnixUs: 0));

            if (cp == 0) EmitByteCatalog(builder);
            if (cp >= ByteAtoms.First && cp <= byte.MaxValue)
                EmitByte(builder, (byte)cp);
        }

        protected override async IAsyncEnumerable<int> ExtractRecordsAsync(
            string ecosystemPath,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            // The generated mmap is the single Tier-0 geometry authority.
            // allkeys.txt remains the declared physical source input, but ingest must
            // never independently recompute coordinates/Hilbert and create a second
            // placement law beside the installed perfcache.
            if (!File.Exists(_path))
                throw new FileNotFoundException("DUCET source is required for Unicode floor provenance.", _path);
            await using (FileStream source = File.OpenRead(_path))
            {
                if (!source.CanRead)
                    throw new IOException($"DUCET source is not readable: {_path}");
            }

            CodepointPerfcache.LoadDefault();
            if (CodepointPerfcache.Count != UnicodeSeed.CodepointCount)
                throw new InvalidOperationException(
                    $"Tier-0 perfcache has {CodepointPerfcache.Count} records; expected {UnicodeSeed.CodepointCount}.");

            await Task.CompletedTask;
            for (int cp = 0; cp < CodepointPerfcache.Count; ++cp)
            {
                ct.ThrowIfCancellationRequested();
                yield return cp;
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

    private sealed class UcdXmlValidationPhase : UnicodeComposePhase<int>
    {
        private readonly string _path;

        public UcdXmlValidationPhase(string path, int batch)
            : base(batch, attestationCapacity: 0) => _path = path;

        protected override string PhaseLabel => "ucdxml";
        protected override void Compose(int record, SubstrateChangeBuilder builder) { }

        protected override async IAsyncEnumerable<int> ExtractRecordsAsync(
            string ecosystemPath,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            UnicodeSeed.ValidateUcdXml(_path);
            await Task.CompletedTask;
            yield return 0;
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
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.RangePoint>
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

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.RangePoint row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.RangePoint row,
            SubstrateChangeBuilder builder)
        {
            if (_allowed is not null && !_allowed.Contains(row.Value)) return;
            Hash128 valueId = _owner.ClassifierEntity(
                builder, _canonicalPrefix, row.Value);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), _relation, valueId, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.RangePoint>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.RangePointsAsync(_path, ct);
    }


    private sealed class BinaryPropertyPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.BinaryPropertyPoint>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public BinaryPropertyPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => $"binary-properties/{Path.GetFileNameWithoutExtension(_path)}";

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.BinaryPropertyPoint row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.BinaryPropertyPoint row,
            SubstrateChangeBuilder builder)
        {
            Hash128 propertyId = _owner.ClassifierEntity(
                builder, "unicode/property", row.Property);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasProperty,
                propertyId, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.BinaryPropertyPoint>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.BinaryPropertiesAsync(_path, ct);
    }

    private sealed class ContextualRangePropertyPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.RangePoint>
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

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.RangePoint row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.RangePoint row,
            SubstrateChangeBuilder builder)
        {
            Hash128 keyId = _owner.ClassifierEntity(builder, "unicode/property_key", _property);
            Hash128 valueId = _owner.ClassifierEntity(
                builder, $"unicode/property_value/{_property}", row.Value);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasProperty,
                valueId, Source, keyId,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.RangePoint>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.RangePointsAsync(_path, ct);
    }

    private sealed class ScriptExtensionsPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.RangePoint>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public ScriptExtensionsPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "script-extensions";

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.RangePoint row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.RangePoint row,
            SubstrateChangeBuilder builder)
        {
            foreach (string script in row.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Hash128 scriptId = _owner.ClassifierEntity(
                    builder, "unicode/script", script);
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    CodepointId(row.Codepoint), UcdProperties.RelTypeUsesScriptExtension,
                    scriptId, Source, null,
                    RelationTypeRank.StandardsStructural * TC.StandardsDerived));
            }
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.RangePoint>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.RangePointsAsync(_path, ct);
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
            Hash128 keyId = _owner.ClassifierEntity(
                builder, "unicode/unihan_property", row.Property);
            Hash128? valueId = ContentEmitter.Emit(builder, row.Value, Source);
            if (valueId is null) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasProperty,
                valueId.Value, Source, keyId,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
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
        private readonly string _path;

        public PropertyAliasPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "property-aliases";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.PropertyAliasRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.PropertyAliasRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 propertyId = _owner.ClassifierEntity(
                builder, "unicode/property_key", row.CanonicalProperty);
            Hash128? aliasId = ContentEmitter.Emit(builder, row.Alias, Source);
            if (aliasId is null) return;
            Hash128 context = _owner.ClassifierEntity(
                builder, "unicode/property_key", "Property_Alias");
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                propertyId, UcdProperties.RelTypeHasProperty,
                aliasId.Value, Source, context,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.PropertyAliasRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.PropertyAliasesAsync(_path, ct);
    }

    private sealed class PropertyValueAliasPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.PropertyValueAliasRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public PropertyValueAliasPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch, commitEpoch: 1) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "property-value-aliases";

        protected override long UnitsPerRecord(
            UnicodePhysicalArtifactParser.PropertyValueAliasRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.PropertyValueAliasRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 propertyKey = _owner.ClassifierEntity(
                builder, "unicode/property_key", row.Property);
            Hash128 valueId = _owner.ClassifierEntity(
                builder, $"unicode/property_value/{row.Property}", row.CanonicalValue);
            Hash128? aliasId = ContentEmitter.Emit(builder, row.Alias, Source);
            if (aliasId is null) return;
            double weight = RelationTypeRank.StandardsStructural * TC.StandardsDerived;

            if (row.CountsSourceRow)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    propertyKey, UcdProperties.RelTypeHasProperty,
                    valueId, Source, null, weight));
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                valueId, UcdProperties.RelTypeHasProperty,
                aliasId.Value, Source, propertyKey, weight));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.PropertyValueAliasRow>
            ExtractRecordsAsync(
                string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
            UnicodePhysicalArtifactParser.PropertyValueAliasesAsync(_path, ct);
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
