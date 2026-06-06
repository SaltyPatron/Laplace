using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.VerbNet;

/// <summary>
/// Emits VerbNet 3.4 (Palmer et al.) into the substrate as "content + attestations".
///
/// VerbNet groups verbs into Levin-style classes (give-13.1, …), each a verb-sense
/// inventory with thematic roles, syntactic frames, and per-frame example sentences.
/// Classes nest into subclasses (give-13.1-1) recursively.
///
/// <para><b>The law applied:</b> verb member LEMMAS, thematic-role NAMES (Agent / Theme /
/// Recipient / …), frame DESCRIPTIONS (the "NP V NP PP.recipient" primary string), and
/// frame EXAMPLE sentences are CONTENT entities (<see cref="ContentEmitter"/>) so they
/// co-assert with WordNet, PropBank, FrameNet, and every prose/model witness for the same
/// text. Only the abstract CLASS construct (no canonical text form) keeps a content-addressed
/// meta id (<c>verbnet/class/&lt;id&gt;</c>) — the EXACT convention SemLink references back.</para>
///
/// <para>Coverage: member lemma —IS_A→ class; class —IS_A→ parent class (subclass nesting);
/// class —HAS_THEMATIC_ROLE→ role name (selectional restriction set carried as a named-skip,
/// see below); frame description —HAS_VERB_FRAME→ class (the SAME arena WordNet's verb frames
/// use — deliberate co-assertion; RelationTypeRegistry orients subject=class, object=frame content via
/// the registry's direction handling — emitted class→frame, asymmetric); frame example
/// —HAS_EXAMPLE→ class (context = class); member <c>wn=</c> sense keys —CORRESPONDS_TO→ the
/// WordNet sense entity (<c>wordnet/sense/&lt;key&gt;</c>, the WordNetDecomposer convention).</para>
///
/// <para>Single XML pass per class file (subclasses recursed in-file): each batch is
/// self-contained — the class meta entity + every referenced content/sense entity ride the
/// same intent as the attestations (the writer orders entities before attestations), so the
/// FK floor always holds and batches commit in any order (ON CONFLICT idempotent).</para>
/// </summary>
public sealed class VerbNetDecomposer : IDecomposer
{
    /// <summary>Meta-entity canonical names — registered post-ingest so
    /// render() answers in names, never hex (2026-06-05).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MetaNames = new();

    public IReadOnlyCollection<string> CanonicalNamesForReadback
        => System.Linq.Enumerable.ToList(MetaNames.Keys);

    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/VerbNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 ClassTypeId =
        Hash128.OfCanonical("substrate/type/VerbNet_Class/v1");
    private static readonly Hash128 WordNetSenseTypeId =
        Hash128.OfCanonical("substrate/type/WordNet_Sense/v1");

    // Meta-entity id conventions (the LAW: classes get meta ids; SemLink + PropBank
    // reference these EXACT strings). A class id is keyed on the BARE NUMERIC form
    // (give-13.1 → 13.1, give-13.1-1 → 13.1-1): VerbNet's XML ID carries the lemma
    // prefix, PropBank rolelinks carry it too, but SemLink's instance JSONs use the
    // bare numeric form — stripping the prefix is what makes all three resources
    // co-assert on ONE class entity instead of forking parallel near-duplicates.
    internal static Hash128 ClassId(string classId)
    {
        string name = $"verbnet/class/{NumericClassId(classId)}";
        MetaNames.TryAdd(name, 0);
        return Hash128.OfCanonical(name);
    }
    internal static Hash128 SenseId(string senseKey)  => Hash128.OfCanonical($"wordnet/sense/{senseKey}");

    /// <summary>Strip a VerbNet class id's lemma prefix to its bare numeric form:
    /// <c>give-13.1</c> → <c>13.1</c>; <c>give-13.1-1</c> → <c>13.1-1</c>;
    /// <c>break_down-45.8</c> → <c>45.8</c>; <c>13.1-1</c> (already bare, as
    /// PropBank/SemLink emit) → <c>13.1-1</c>. A class id either starts with a
    /// digit (already bare) or with an alphabetic lemma the first
    /// <c>-&lt;digit&gt;</c> separates from the numeric class.</summary>
    internal static string NumericClassId(string classId)
    {
        if (classId.Length == 0 || char.IsDigit(classId[0])) return classId;  // already bare numeric
        for (int i = classId.IndexOf('-'); i >= 0 && i + 1 < classId.Length; i = classId.IndexOf('-', i + 1))
            if (char.IsDigit(classId[i + 1])) return classId[(i + 1)..];
        return classId;   // no numeric class found (defensive)
    }

    public Hash128 SourceId     => Source;
    public string  SourceName   => "VerbNetDecomposer";
    public int     LayerOrder   => 2;   // needs only unicode(0)+iso(1); WordNet senses are
                                         // referenced by id and emitted idempotently here
    public Hash128 TrustClassId => TrustClass;

    private const long EstimatedClasses = 329L;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("VerbNet_Class");
        boot.AddType("WordNet_Sense");        // matches WordNetDecomposer's sense-entity type
        // Rank/trust live ONLY in RelationTypeRegistry; AddRelationType(name) just guarantees the
        // kind entity exists (SeedCanonical in Build() seeds every canonical arena).
        boot.AddRelationType("IS_A");
        boot.AddRelationType("HAS_THEMATIC_ROLE");
        boot.AddRelationType("HAS_VERB_FRAME");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("CORRESPONDS_TO");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string classDir = ResolveClassDir(context.EcosystemPath);
        int batch = options.BatchSize > 1 ? options.BatchSize : 64;

        var b = NewBuilder("verbnet/batch-0", batch);
        int n = 0, bn = 0;

        foreach (var file in EnumerateClassFiles(classDir))
        {
            ct.ThrowIfCancellationRequested();
            var doc = new XmlDocument();
            try { doc.Load(file); }
            catch (XmlException) { continue; }   // tolerate a malformed file, keep ingesting
            var root = doc.DocumentElement;
            if (root is null || !root.Name.Equals("VNCLASS", StringComparison.Ordinal)) continue;

            EmitClass(b, root, parentClassId: null);

            if (++n >= batch)
            {
                if (!options.DryRun) yield return b.Build();
                b = NewBuilder($"verbnet/batch-{++bn}", batch);
                n = 0; await Task.Yield();
            }
        }
        if (n > 0 && !options.DryRun) yield return b.Build();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedClasses);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── emission ──────────────────────────────────────────────────────────────

    /// <summary>Emit one VNCLASS or VNSUBCLASS element (and recurse into its
    /// SUBCLASSES). <paramref name="el"/>'s ID attribute is the class id; for a
    /// subclass, <paramref name="parentClassId"/> is the enclosing class.</summary>
    private static void EmitClass(SubstrateChangeBuilder b, XmlElement el, string? parentClassId)
    {
        string? classId = el.GetAttribute("ID");
        if (string.IsNullOrEmpty(classId)) return;

        Hash128 classEntity = ClassId(classId);
        b.AddEntity(new EntityRow(classEntity, (byte)MetaTier.Meta, ClassTypeId, Source));

        // class —IS_A→ parent class (subclass nesting). The parent meta entity
        // also rides this batch so the FK holds standalone (idempotent).
        if (parentClassId is not null)
        {
            Hash128 parentEntity = ClassId(parentClassId);
            b.AddEntity(new EntityRow(parentEntity, (byte)MetaTier.Meta, ClassTypeId, Source));
            b.AddAttestation(RelationTypeRegistry.Attest(
                classEntity, "IS_A", parentEntity, Source, TC.AcademicCurated));
        }

        // MEMBERS: lemma (content) —IS_A→ class; wn= sense keys —CORRESPONDS_TO→ WN sense.
        foreach (var member in ChildElements(el, "MEMBERS", "MEMBER"))
        {
            string name = member.GetAttribute("name").Replace('_', ' ').Trim();
            if (name.Length == 0) continue;
            var lemmaId = ContentEmitter.Emit(b, name, Source);
            if (lemmaId is null) continue;
            b.AddAttestation(RelationTypeRegistry.Attest(
                lemmaId.Value, "IS_A", classEntity, Source, TC.AcademicCurated));

            // wn="give%2:40:03 give%2:40:00 …" — WordNet sense keys. Normalize to the
            // index.sense canonical form (strip an uncertainty '?'/'!' marker; append
            // the '::' the data drops) so the id matches WordNetDecomposer's exactly.
            string wn = member.GetAttribute("wn");
            if (wn.Length > 0)
                foreach (var raw in wn.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string? key = NormalizeSenseKey(raw);
                    if (key is null) continue;
                    Hash128 senseEntity = SenseId(key);
                    b.AddEntity(new EntityRow(senseEntity, /*tier*/ 2, WordNetSenseTypeId, Source));
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        lemmaId.Value, "CORRESPONDS_TO", senseEntity, Source, TC.AcademicCurated));
                }
        }

        // THEMROLES: class —HAS_THEMATIC_ROLE→ role NAME (content). The selectional
        // restriction set (SELRESTRS, e.g. +animate ∨ +organization) is a named-skip
        // here — it is a logic tree over restriction TYPES, not a single classifier
        // value, so cheap context-id attachment would falsify it; recorded as a
        // skip rather than mangled (see coverage report).
        foreach (var role in ChildElements(el, "THEMROLES", "THEMROLE"))
        {
            string type = role.GetAttribute("type").Trim();
            if (type.Length == 0) continue;
            var roleId = ContentEmitter.Emit(b, type, Source);
            if (roleId is null) continue;
            b.AddAttestation(RelationTypeRegistry.Attest(
                classEntity, "HAS_THEMATIC_ROLE", roleId.Value, Source, TC.AcademicCurated));
        }

        // FRAMES: frame DESCRIPTION primary (content) —HAS_VERB_FRAME→ class;
        // each EXAMPLE (content) —HAS_EXAMPLE→ class (context = class).
        foreach (var frame in ChildElements(el, "FRAMES", "FRAME"))
        {
            string primary = "";
            foreach (XmlNode d in frame.GetElementsByTagName("DESCRIPTION"))
            {
                if (d is XmlElement de) primary = de.GetAttribute("primary").Trim();
                break;
            }
            if (primary.Length > 0)
            {
                var frameId = ContentEmitter.Emit(b, primary, Source);
                if (frameId is not null)
                    // SAME arena as WordNet verb frames; registry orients subject=class,
                    // object=frame content (HAS_VERB_FRAME is asymmetric, no flip).
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        classEntity, "HAS_VERB_FRAME", frameId.Value, Source, TC.AcademicCurated));
            }

            foreach (XmlNode exNode in frame.GetElementsByTagName("EXAMPLE"))
            {
                string ex = exNode.InnerText.Trim();
                if (ex.Length == 0) continue;
                var exId = ContentEmitter.Emit(b, ex, Source);
                if (exId is not null)
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        classEntity, "HAS_EXAMPLE", exId.Value, Source, TC.AcademicCurated,
                        contextId: classEntity));
            }
        }

        // SUBCLASSES (recursive): each VNSUBCLASS is a class with this class as parent.
        foreach (var subWrap in DirectChildren(el, "SUBCLASSES"))
            foreach (var sub in DirectChildren(subWrap, "VNSUBCLASS"))
                EmitClass(b, sub, parentClassId: classId);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 64,
            physicalityCapacity: batch * 64,
            attestationCapacity: batch * 32);

    /// <summary>Normalize a VerbNet <c>wn=</c> sense key to the WordNet
    /// <c>index.sense</c> canonical form so its content-addressed id matches the
    /// WordNetDecomposer's. VerbNet drops the trailing <c>::</c> (the
    /// lemma_id:head_word:head_id suffix) and prefixes uncertain senses with
    /// <c>?</c>/<c>!</c>; both are reconciled here. Returns null if the token is
    /// not a sense key (no <c>%</c>).</summary>
    internal static string? NormalizeSenseKey(string raw)
    {
        string k = raw.Trim().TrimStart('?', '!');
        int pct = k.IndexOf('%');
        if (pct <= 0 || pct + 1 >= k.Length) return null;
        // Collapse any trailing colons, then restore the canonical "::" the
        // index.sense key always carries.
        return k.TrimEnd(':') + "::";
    }

    /// <summary>The 329 class XMLs. The data root may be the repo root (contains
    /// <c>verbnet3.4/</c>) or the class dir itself.</summary>
    private static string ResolveClassDir(string ecosystemPath)
    {
        foreach (var c in new[]
                 {
                     Path.Combine(ecosystemPath, "verbnet-master", "verbnet3.4"),
                     Path.Combine(ecosystemPath, "verbnet3.4"),
                     ecosystemPath,
                 })
            if (Directory.Exists(c) &&
                Directory.EnumerateFiles(c, "*.xml").Any())
                return c;
        return ecosystemPath;
    }

    private static IEnumerable<string> EnumerateClassFiles(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.xml")
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }

    /// <summary>Direct child elements of <paramref name="el"/> named
    /// <paramref name="name"/> (no descendant recursion — VNSUBCLASS nesting means
    /// GetElementsByTagName would leak a subclass's MEMBERS into its parent).</summary>
    private static IEnumerable<XmlElement> DirectChildren(XmlElement el, string name)
    {
        foreach (XmlNode child in el.ChildNodes)
            if (child is XmlElement ce && ce.Name.Equals(name, StringComparison.Ordinal))
                yield return ce;
    }

    /// <summary>Items named <paramref name="item"/> inside the single direct-child
    /// wrapper <paramref name="wrapper"/> (e.g. MEMBER inside MEMBERS) — scoped to
    /// THIS class, never a nested subclass's.</summary>
    private static IEnumerable<XmlElement> ChildElements(XmlElement el, string wrapper, string item)
    {
        foreach (var w in DirectChildren(el, wrapper))
            foreach (var it in DirectChildren(w, item))
                yield return it;
    }
}
