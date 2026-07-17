using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

[assembly: InternalsVisibleTo("Laplace.Decomposers.FrameNet.Tests")]

namespace Laplace.Decomposers.FrameNet;

/// <summary>
/// Emits Berkeley FrameNet 1.7 into the substrate as "normal content + attestations".
///
/// <para>THE LAW: content is identity. Frame names, frame-element (FE) names, lexical-unit
/// (LU) lemmas, definitions and example sentences are emitted as content-addressed CONTENT
/// (<see cref="ContentEmitter"/>) — the same entity any other source produces for the same
/// bytes — so FrameNet co-asserts with WordNet, the model, prose, every witness. The
/// FrameNet-specific STRUCTURES (a frame as an object, an FE as an object, an LU as an
/// object) have no canonical text form, so they keep content-addressed external-id meta
/// entities.</para>
///
/// <para>One pass per file class (completeness is a property of the emitter):</para>
/// <list type="bullet">
///   <item><b>frame/*.xml</b> — the spine. Each file carries everything FrameNet testifies
///   about one frame in one document: the frame (name content + meta entity + HAS_DEFINITION);
///   its FEs (name content + meta entity + HAS_FRAME_ELEMENT with the coreness Core /
///   Peripheral / Extra-Thematic / Core-Unexpressed as the context_id classifier, + the FE's
///   HAS_DEFINITION); its embedded LUs (lemma content + HAS_POS via PosReference + the LU
///   EVOKES_FRAME the frame); and its directional frameRelation edges (Inherits from → IS_A,
///   Uses → FRAME_USES, Perspective on → PERSPECTIVE_ON, Subframe of → HAS_SUBEVENT-flipped,
///   Is Causative of → CAUSATIVE_OF, Is Inchoative of → INCHOATIVE_OF, Precedes → PRECEDES,
///   See also → RELATED_TO).</item>
///   <item><b>fulltext/*.xml</b> — running-text annotation. Each sentence is content; each
///   manually-annotated target attests target-wordform EVOKES_FRAME(frame) with the sentence
///   as context_id (the attested occurrence in situ).</item>
/// </list>
///
/// <para>Two passes (FK-safe): pass 1 emits entities (frame/FE/LU meta entities + all content);
/// pass 2 emits attestations. The frame directory is walked once per pass.</para>
/// </summary>
public sealed class FrameNetDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/FrameNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    // Frame / FE / LU are abstract FrameNet constructs (no canonical text form) — meta
    // entities keyed by their FrameNet identity. The FE coreness classes are the context_id
    // values for HAS_FRAME_ELEMENT (a classifier on the edge, NOT a separate kind).
    private static readonly Hash128 FrameTypeId    = Hash128.OfCanonical("substrate/type/FrameNet_Frame/v1");
    private static readonly Hash128 FrameElemTypeId = Hash128.OfCanonical("substrate/type/FrameNet_FE/v1");
    private static readonly Hash128 LexUnitTypeId  = Hash128.OfCanonical("substrate/type/FrameNet_LU/v1");
    private static readonly Hash128 CorenessTypeId = Hash128.OfCanonical("substrate/type/FrameNet_Coreness/v1");

    /// <summary>Frame meta-entity id, keyed by frame name (the FrameNet identity, also the
    /// content-addressed text — the meta id is the structural object, the content is the name).</summary>
    private static Hash128 FrameId(string name)
    {
        string canonical = $"framenet/frame/{name}";
        MetaNames.TryAdd(canonical, 0);
        return Hash128.OfCanonical(canonical);
    }

    /// <summary>FE meta-entity id, scoped to its frame (an FE "Agent" of Giving ≠ "Agent" of
    /// Abandonment — same NAME content, distinct structural roles).</summary>
    private static Hash128 FeId(string frame, string fe) => Hash128.OfCanonical($"framenet/fe/{frame}/{fe}");

    /// <summary>LU meta-entity id, keyed by FrameNet LU id (lemma+POS+frame triple — stable).</summary>
    private static Hash128 LuId(int luId) => Hash128.OfCanonical($"framenet/lu/{luId}");

    /// <summary>Coreness classifier entity (the HAS_FRAME_ELEMENT context_id value).</summary>
    private static Hash128 CorenessId(string coreType) =>
        Hash128.OfCanonical($"framenet/coreness/{coreType}");

    // The four coreType values FrameNet uses on FEs (built + counted from the v1.7 data).
    private static readonly string[] CorenessValues =
        ["Core", "Peripheral", "Extra-Thematic", "Core-Unexpressed"];

    // FrameNet POS tag → UPOS, resolved through PosReference (the omni-glottal POS index).
    // Confident mappings only; the long tail (IDIO, ART, C, SCON, NUM, …) routes to the
    // namespaced probationary value via PosReference.Resolve, never guessed or dropped.
    private static readonly Dictionary<string, string> PosToUpos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["N"]    = "NOUN", ["V"]   = "VERB", ["A"]    = "ADJ",   ["ADV"]  = "ADV",
        ["PREP"] = "ADP",  ["NUM"] = "NUM",  ["INTJ"] = "INTJ",  ["PRON"] = "PRON",
        ["ART"]  = "DET",  ["SCON"] = "SCONJ", ["C"]   = "CCONJ",
        // IDIO (idiomatic multi-word) has no UPOS — left out so PosReference logs it
        // probationary rather than a wrong guess.
    };

    /// <summary>FrameNet directional frameRelation type → canonical kind name. ONLY the
    /// canonical-direction member of each inverse pair is mapped: the registry flips
    /// endpoints for the reverse query (rule 3 — one arena), so emitting the inverse
    /// ("Is Inherited by", "Is Used by", "Has Subframe(s)", "Is Perspectivized in",
    /// "Is Preceded by", "Is Causative of"⁻¹) would DOUBLE the testimony. See also is
    /// symmetric → RELATED_TO. The subject is the frame whose file declares the relation.</summary>
    private static readonly Dictionary<string, string> RelationKinds = new(StringComparer.Ordinal)
    {
        ["Inherits from"]   = "INHERITS_FROM",   // → IS_A (registry alias)
        ["Uses"]            = "FRAME_USES",
        ["Perspective on"]  = "PERSPECTIVE_ON",
        ["Subframe of"]     = "SUBFRAME_OF",      // → HAS_SUBEVENT, flipped (registry alias)
        ["Is Causative of"] = "CAUSATIVE_OF",
        ["Is Inchoative of"] = "INCHOATIVE_OF",
        ["Precedes"]        = "PRECEDES",
        ["See also"]        = "RELATED_TO",
    };

    private const string Ns = "http://framenet.icsi.berkeley.edu";

    public Hash128 SourceId     => Source;
    public string  SourceName   => "FrameNetDecomposer";
    // FrameNet sits a tier later than WordNet (layer 2): its LU lemmas converge onto the
    // wordform content WordNet/Wiktionary already seeded, and EVOKES_FRAME co-asserts with
    // the predicate-semantic arena. Independent of the frame structures themselves, but the
    // ladder orders it after the layer-2 lexical corpora.
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("FrameNet_Frame");
        boot.AddType("FrameNet_FE");
        boot.AddType("FrameNet_LU");
        boot.AddType("FrameNet_Coreness");

        // Every kind name this source Attests. Rank/trust live in KindRegistry — AddKind(name)
        // only. Canonical names (registry resolves the source aliases INHERITS_FROM / SUBFRAME_OF
        // to their arenas; seeding the canonical entity is what satisfies the type_id FK floor).
        boot.AddKind("EVOKES_FRAME");
        boot.AddKind("HAS_FRAME_ELEMENT");
        boot.AddKind("HAS_DEFINITION");
        boot.AddKind("HAS_POS");
        boot.AddKind("HAS_EXAMPLE");
        boot.AddKind("FRAME_USES");
        boot.AddKind("PERSPECTIVE_ON");
        boot.AddKind("CAUSATIVE_OF");
        boot.AddKind("INCHOATIVE_OF");
        boot.AddKind("PRECEDES");
        boot.AddKind("IS_A");          // INHERITS_FROM resolves here
        boot.AddKind("HAS_SUBEVENT");  // SUBFRAME_OF resolves here
        boot.AddKind("RELATED_TO");    // See also

        await context.Writer.ApplyAsync(boot.Build(), ct);

        // Seed THE canonical POS inventory (PosReference) + the coreness classifier
        // entities (context_id values for HAS_FRAME_ELEMENT). Idempotent — content-addressed.
        var seed = new SubstrateChangeBuilder(
            Source, "bootstrap/framenet-vocab", null,
            entityCapacity: PosReference.Canonical.Length + 1 + CorenessValues.Length + 1,
            physicalityCapacity: 0, attestationCapacity: 0);
        PosReference.SeedCanonical(seed, Source);
        seed.AddEntity(new EntityRow(CorenessTypeId, (byte)MetaTier.Meta,
            BootstrapIntentBuilder.TypeMetaTypeId, Source));
        foreach (var c in CorenessValues)
            seed.AddEntity(new EntityRow(CorenessId(c), (byte)MetaTier.Meta, CorenessTypeId, Source));
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string frameDir    = Path.Combine(context.EcosystemPath, "frame");
        string fulltextDir = Path.Combine(context.EcosystemPath, "fulltext");
        int batch = options.BatchSize > 1 ? options.BatchSize : 256;

        // Pass 1: entities — frame/FE/LU meta entities + all content (frame names, FE names,
        // LU lemmas, definitions, examples). Pass 2: attestations (every referent exists now).
        await foreach (var change in StreamFramesAsync(frameDir, batch, entitiesOnly: true, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
        await foreach (var change in StreamFramesAsync(frameDir, batch, entitiesOnly: false, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }

        // Fulltext annotation: sentence content (pass 1) + target-evokes-frame (pass 2),
        // each batch self-contained (sentence + target wordform ride the same intent the
        // writer orders entities-before-attestations within).
        await foreach (var change in StreamFulltextAsync(fulltextDir, batch, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long frames = 0, lus = 0;
        string frameDir = Path.Combine(context.EcosystemPath, "frame");
        string luDir    = Path.Combine(context.EcosystemPath, "lu");
        if (Directory.Exists(frameDir)) frames = Directory.EnumerateFiles(frameDir, "*.xml").LongCount();
        if (Directory.Exists(luDir))    lus    = Directory.EnumerateFiles(luDir, "lu*.xml").LongCount();
        return Task.FromResult<long?>(frames + lus);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The data-derived classifier canonical names this source mints (so
    /// <c>render()</c> answers them in names, not hex): the four coreness context values.
    /// The relation kinds are canonical-registry names, already render-resolvable.</summary>
    /// <summary>Coreness classifiers + every frame meta-entity name minted
    /// during parse — registered post-ingest so render() answers
    /// "framenet/frame/Giving", never hex (2026-06-05).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MetaNames = new();

    public IReadOnlyCollection<string> CanonicalNamesForReadback =>
        [.. CorenessValues.Select(c => $"framenet/coreness/{c}"), .. MetaNames.Keys];

    // ── frame/*.xml streaming ─────────────────────────────────────────────────

    private static async IAsyncEnumerable<SubstrateChange> StreamFramesAsync(
        string frameDir, int batch, bool entitiesOnly,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(frameDir)) yield break;
        string suffix = entitiesOnly ? "entities" : "attestations";
        var b = NewBuilder($"framenet/frame-0/{suffix}", entitiesOnly, batch);
        int count = 0, batchNum = 0;

        // Ordinal sort so intent ids are deterministic across runs.
        foreach (var path in Directory.EnumerateFiles(frameDir, "*.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            Frame? frame = ParseFrame(path);
            if (frame is null) continue;

            if (entitiesOnly) EmitFrameEntities(b, frame);
            else              EmitFrameAttestations(b, frame);

            if (++count >= batch)
            {
                yield return b.Build();
                b = NewBuilder($"framenet/frame-{++batchNum}/{suffix}", entitiesOnly, batch);
                count = 0;
                await Task.Yield();
            }
        }
        if (count > 0) yield return b.Build();
    }

    private static void EmitFrameEntities(SubstrateChangeBuilder b, Frame frame)
    {
        b.AddEntity(new EntityRow(FrameId(frame.Name), (byte)MetaTier.Meta, FrameTypeId, Source));
        ContentEmitter.Emit(b, frame.Name, Source);
        if (frame.Definition.Length > 0) ContentEmitter.Emit(b, frame.Definition, Source);
        foreach (var ex in frame.Examples) ContentEmitter.Emit(b, ex, Source);

        foreach (var fe in frame.Elements)
        {
            b.AddEntity(new EntityRow(FeId(frame.Name, fe.Name), (byte)MetaTier.Meta, FrameElemTypeId, Source));
            ContentEmitter.Emit(b, fe.Name, Source);
            if (fe.Definition.Length > 0) ContentEmitter.Emit(b, fe.Definition, Source);
        }

        foreach (var lu in frame.LexUnits)
        {
            b.AddEntity(new EntityRow(LuId(lu.Id), (byte)MetaTier.Meta, LexUnitTypeId, Source));
            ContentEmitter.Emit(b, lu.Lemma, Source);
        }

        // Related frame NAMES are content too: the target frame's name entity is the FK for
        // the relation attestation. The target frame's own file emits its meta entity; the
        // name content here just guarantees the wordform exists (idempotent dedup).
        foreach (var rel in frame.Relations)
            ContentEmitter.Emit(b, rel.TargetFrame, Source);
    }

    private static void EmitFrameAttestations(SubstrateChangeBuilder b, Frame frame)
    {
        Hash128 frameId = FrameId(frame.Name);

        // frame → definition.
        if (frame.Definition.Length > 0)
        {
            var defId = ContentEmitter.RootId(frame.Definition);
            if (defId is not null)
                b.AddAttestation(KindRegistry.Attest(
                    frameId, "HAS_DEFINITION", defId.Value, Source, SourceTrust.AcademicCurated));
        }
        foreach (var ex in frame.Examples)
        {
            var exId = ContentEmitter.RootId(ex);
            if (exId is not null)
                b.AddAttestation(KindRegistry.Attest(
                    frameId, "HAS_EXAMPLE", exId.Value, Source, SourceTrust.AcademicCurated));
        }

        // frame → FE (FE name content as the object; coreness as the context_id classifier),
        // + FE → definition.
        foreach (var fe in frame.Elements)
        {
            var feNameId = ContentEmitter.RootId(fe.Name);
            if (feNameId is null) continue;
            Hash128? coreCtx = CorenessValues.Contains(fe.CoreType) ? CorenessId(fe.CoreType) : null;
            b.AddAttestation(KindRegistry.Attest(
                frameId, "HAS_FRAME_ELEMENT", feNameId.Value, Source, SourceTrust.AcademicCurated,
                contextId: coreCtx));

            if (fe.Definition.Length > 0)
            {
                var feDefId = ContentEmitter.RootId(fe.Definition);
                if (feDefId is not null)
                    b.AddAttestation(KindRegistry.Attest(
                        FeId(frame.Name, fe.Name), "HAS_DEFINITION", feDefId.Value,
                        Source, SourceTrust.AcademicCurated));
            }
        }

        // LU lemma → POS, and LU lemma → EVOKES_FRAME → frame. The lemma WORDFORM is the
        // subject (the omni-glottal tie: "give" evokes Giving, and "give" is the same content
        // WordNet/the model emit). The LU's POS rides HAS_POS on that wordform.
        foreach (var lu in frame.LexUnits)
        {
            var lemmaId = ContentEmitter.RootId(lu.Lemma);
            if (lemmaId is null) continue;

            Hash128 posId = ResolvePos(lu.Pos);
            b.AddAttestation(KindRegistry.Attest(
                lemmaId.Value, "HAS_POS", posId, Source, SourceTrust.AcademicCurated));
            b.AddAttestation(KindRegistry.Attest(
                lemmaId.Value, "EVOKES_FRAME", frameId, Source, SourceTrust.AcademicCurated));
        }

        // frame → related frame. The registry resolves alias → canonical arena, applies the
        // direction flip (SUBFRAME_OF ⇒ HAS_SUBEVENT swapped) and supplies the rank.
        foreach (var rel in frame.Relations)
        {
            if (!RelationKinds.TryGetValue(rel.Type, out var kindName)) continue;
            Hash128 tgt = FrameId(rel.TargetFrame);
            b.AddAttestation(KindRegistry.Attest(
                frameId, kindName, tgt, Source, SourceTrust.AcademicCurated));
        }
    }

    // ── fulltext/*.xml streaming ──────────────────────────────────────────────

    private static async IAsyncEnumerable<SubstrateChange> StreamFulltextAsync(
        string fulltextDir, int batch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(fulltextDir)) yield break;
        var b = NewBuilder("framenet/fulltext-0", entitiesOnly: false, batch);
        int count = 0, batchNum = 0;

        foreach (var path in Directory.EnumerateFiles(fulltextDir, "*.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var ann in ParseFulltextAsync(path, ct))
            {
                // Self-contained: the sentence content, the target wordform content, and the
                // EVOKES_FRAME attestation all ride one intent (writer orders entities first).
                var sentId = ContentEmitter.Emit(b, ann.Sentence, Source);
                var targetId = ContentEmitter.Emit(b, ann.TargetText, Source);
                if (sentId is not null && targetId is not null)
                    b.AddAttestation(KindRegistry.Attest(
                        targetId.Value, "EVOKES_FRAME", FrameId(ann.FrameName),
                        Source, SourceTrust.AcademicCurated, contextId: sentId.Value));

                if (++count >= batch)
                {
                    yield return b.Build();
                    b = NewBuilder($"framenet/fulltext-{++batchNum}", entitiesOnly: false, batch);
                    count = 0;
                    await Task.Yield();
                }
            }
        }
        if (count > 0) yield return b.Build();
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static SubstrateChangeBuilder NewBuilder(string unit, bool entitiesOnly, int batch) =>
        new(Source, unit, null,
            entityCapacity:      entitiesOnly ? batch * 32 : batch * 4,
            physicalityCapacity: entitiesOnly ? batch * 32 : batch * 4,
            attestationCapacity: entitiesOnly ? 0          : batch * 32);

    /// <summary>FrameNet POS → canonical UPOS value id (through PosReference). Mapped tags
    /// resolve to the universal value; unmapped tags (IDIO, …) route to the namespaced
    /// probationary value (logged, never silent — PosReference owns the miss counter).</summary>
    private static Hash128 ResolvePos(string fnPos)
    {
        if (PosToUpos.TryGetValue(fnPos, out var upos))
            return PosReference.Resolve(upos, PosReference.PosTagset.Upos);
        // Route the raw FrameNet tag through the probationary path (Upos tagset rejects it,
        // so it namespaces + counts it) — the mapping table grows from observed data.
        return PosReference.Resolve(fnPos, PosReference.PosTagset.Upos);
    }

    // ── parsers ──────────────────────────────────────────────────────────────

    /// <summary>Parse one frame/*.xml into its frame, FEs, embedded LUs and directional
    /// relations. Frames are small (≤ a few hundred KB) so XDocument is appropriate.</summary>
    internal static Frame? ParseFrame(string path)
    {
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (XmlException) { return null; }
        return ParseFrame(doc);
    }

    internal static Frame? ParseFrame(XDocument doc)
    {
        XNamespace ns = Ns;
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "frame") return null;
        string? name = (string?)root.Attribute("name");
        if (string.IsNullOrEmpty(name)) return null;

        // Frame definition: the element's value is HTML-entity-decoded def-root markup.
        var (frameDef, frameExamples) = ParseDefRoot((string?)root.Element(ns + "definition") ?? "");

        var elements = new List<FrameElement>();
        foreach (var fe in root.Elements(ns + "FE"))
        {
            string? feName = (string?)fe.Attribute("name");
            if (string.IsNullOrEmpty(feName)) continue;
            string coreType = (string?)fe.Attribute("coreType") ?? "";
            var (feDef, _) = ParseDefRoot((string?)fe.Element(ns + "definition") ?? "");
            elements.Add(new FrameElement(feName, coreType, feDef));
        }

        var lus = new List<LexUnit>();
        foreach (var lu in root.Elements(ns + "lexUnit"))
        {
            string? luName = (string?)lu.Attribute("name");   // "lemma.pos"
            string? pos    = (string?)lu.Attribute("POS");
            if (string.IsNullOrEmpty(luName) || string.IsNullOrEmpty(pos)) continue;
            if (!int.TryParse((string?)lu.Attribute("ID"), out int id)) continue;
            string lemma = LemmaOf(luName);
            if (lemma.Length == 0) continue;
            lus.Add(new LexUnit(id, lemma, pos));
        }

        var relations = new List<FrameRel>();
        foreach (var fr in root.Elements(ns + "frameRelation"))
        {
            string type = (string?)fr.Attribute("type") ?? "";
            if (!RelationKinds.ContainsKey(type)) continue;   // skip inverse-direction members
            foreach (var rf in fr.Elements(ns + "relatedFrame"))
            {
                string target = ((string?)rf)?.Trim() ?? "";
                if (target.Length > 0) relations.Add(new FrameRel(type, target));
            }
        }

        return new Frame(name, frameDef, frameExamples, elements, lus, relations);
    }

    /// <summary>LU name "give.v" → lemma "give"; multi-word "give up.v" → "give up". The POS
    /// suffix after the LAST dot is dropped (the POS attribute is authoritative).</summary>
    private static string LemmaOf(string luName)
    {
        int dot = luName.LastIndexOf('.');
        return (dot > 0 ? luName[..dot] : luName).Trim();
    }

    /// <summary>Streaming fulltext parse: each sentence's text + each MANUAL annotationSet's
    /// target span (the LU's frame), yielding (sentence, target-substring, frameName). The
    /// target span is the &lt;label name="Target"&gt; start/end into the sentence text. UNANN
    /// (auto POS/NER) annotationSets carry no frame and are skipped.</summary>
    internal static async IAsyncEnumerable<FulltextAnno> ParseFulltextAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var settings = new XmlReaderSettings { Async = true, IgnoreWhitespace = false };
        using var reader = XmlReader.Create(path, settings);

        string sentence = "";
        // Per-annotationSet state.
        string? frameName = null;
        int targetStart = -1, targetEnd = -1;
        bool inTargetLayer = false;

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "sentence":
                        sentence = "";
                        break;
                    case "text":
                        sentence = await reader.ReadElementContentAsStringAsync();
                        break;
                    case "annotationSet":
                        frameName = reader.GetAttribute("frameName");
                        targetStart = targetEnd = -1;
                        inTargetLayer = false;
                        break;
                    case "layer":
                        inTargetLayer = reader.GetAttribute("name") == "Target";
                        break;
                    case "label":
                        if (inTargetLayer && reader.GetAttribute("name") == "Target")
                        {
                            // First Target label of the set is the predicate span.
                            if (targetStart < 0)
                            {
                                int.TryParse(reader.GetAttribute("start"), out targetStart);
                                if (!int.TryParse(reader.GetAttribute("end"), out targetEnd)) targetEnd = -1;
                            }
                        }
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "annotationSet")
            {
                if (!string.IsNullOrEmpty(frameName) && !string.IsNullOrEmpty(sentence)
                    && targetStart >= 0 && targetEnd >= targetStart && targetEnd < sentence.Length)
                {
                    // FrameNet offsets are inclusive character indices into the sentence text.
                    string target = sentence.Substring(targetStart, targetEnd - targetStart + 1).Trim();
                    if (target.Length > 0)
                        yield return new FulltextAnno(sentence, target, frameName!);
                }
                frameName = null;
                targetStart = targetEnd = -1;
                inTargetLayer = false;
            }
        }
    }

    /// <summary>Split FrameNet def-root markup (the entity-decoded value of a &lt;definition&gt;)
    /// into clean definition text + example sentences. The markup wraps the body in
    /// &lt;def-root&gt; with inline &lt;fex&gt;/&lt;fen&gt;/&lt;t&gt; (kept as plain text) and
    /// &lt;ex&gt; example blocks (extracted as HAS_EXAMPLE content). The leading "COD:"/"FN:"
    /// source tags some defs carry are preserved as-is (observed bytes, no normalization).</summary>
    internal static (string Def, List<string> Examples) ParseDefRoot(string raw)
    {
        var examples = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return ("", examples);

        // The value is decoded text that itself contains markup. Wrap + parse as a fragment.
        // It may also be plain text (e.g. LU defs like "COD: about, concerning") with no tags.
        string wrapped = raw.Contains('<') ? raw : $"<def-root>{System.Security.SecurityElement.Escape(raw)}</def-root>";
        XElement el;
        try
        {
            el = XElement.Parse(wrapped, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            // Malformed inline markup (rare unbalanced tag) — fall back to the raw text minus
            // angle-bracket tags, never dropping the definition.
            return (StripTags(raw).Trim(), examples);
        }

        var defBody = new StringBuilder();
        CollectText(el, defBody, examples, insideExample: false);
        return (CollapseWs(defBody.ToString()), examples);
    }

    /// <summary>Walk the def-root tree: text outside &lt;ex&gt; accretes into the definition;
    /// each &lt;ex&gt; block's text is one example. &lt;fex&gt;/&lt;fen&gt;/&lt;t&gt; are
    /// transparent (their text belongs to whichever bucket the node sits in).</summary>
    private static void CollectText(XElement el, StringBuilder def, List<string> examples, bool insideExample)
    {
        foreach (var node in el.Nodes())
        {
            if (node is XText t)
            {
                (insideExample ? null : def)?.Append(t.Value);
            }
            else if (node is XElement child)
            {
                if (child.Name.LocalName == "ex")
                {
                    string ex = CollapseWs(InnerText(child));
                    if (ex.Length > 0) examples.Add(ex);
                }
                else
                {
                    CollectText(child, def, examples, insideExample);
                }
            }
        }
    }

    private static string InnerText(XElement el)
    {
        var sb = new StringBuilder();
        foreach (var n in el.DescendantNodes())
            if (n is XText t) sb.Append(t.Value);
        return sb.ToString();
    }

    private static string StripTags(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (char c in s)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CollapseWs(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool ws = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { ws = true; continue; }
            if (ws && sb.Length > 0) sb.Append(' ');
            ws = false;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    // ── records ──────────────────────────────────────────────────────────────

    internal sealed record Frame(
        string Name, string Definition, List<string> Examples,
        List<FrameElement> Elements, List<LexUnit> LexUnits, List<FrameRel> Relations);

    internal sealed record FrameElement(string Name, string CoreType, string Definition);

    internal sealed record LexUnit(int Id, string Lemma, string Pos);

    internal sealed record FrameRel(string Type, string TargetFrame);

    internal sealed record FulltextAnno(string Sentence, string TargetText, string FrameName);
}
