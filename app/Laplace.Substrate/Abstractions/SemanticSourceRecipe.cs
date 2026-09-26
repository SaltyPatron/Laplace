using System.Text;
using System.Globalization;
using System.Numerics;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

[Flags]
public enum SourceFieldDisposition
{
    None = 0,
    Identity = 1 << 0,
    Content = 1 << 1,
    Physicality = 1 << 2,
    Trajectory = 1 << 3,
    Occurrence = 1 << 4,
    Reference = 1 << 5,
    Testimony = 1 << 6,
    Provenance = 1 << 7,
    Calculation = 1 << 8,
    Packaging = 1 << 9,
    Excluded = 1 << 10,
}

public enum SourceValueKind
{
    Identity,
    Boolean,
    Enumerated,
    EnumeratedSequence,
    Integer,
    ExactNumber,
    Codepoint,
    CodepointSequence,
    Text,
    StructuredText,
}

public enum SourceReferenceCodec
{
    None,
    UnicodeCodepoint,
    UnicodeCodepointSequence,
    UPlusCodepointWithQualifier,
    // Existing governed ids such as EntityTypeRegistry nodes are BLAKE3 over
    // their canonical UTF-8 name. The recipe VM preserves that id while
    // projecting it onto the same canonical content as a real physicality.
    CanonicalNameHash,
}

/// <summary>
/// Declarative lowering of one concrete syntax field into the universal substrate.
/// PropertyName is the canonical semantic identity after source alias resolution;
/// SyntaxPath is provider-owned concrete syntax.  One value may deliberately have
/// several dispositions (for example exact sentence content plus attributed testimony).
/// </summary>
public sealed record SourceRecipeField(
    string SyntaxPath,
    string PropertyName,
    SourceValueKind ValueKind,
    SourceFieldDisposition Disposition,
    string? AbsentSentinel = null,
    string? SequenceSeparator = null,
    string? RelationName = null,
    string? ObjectNamespace = null,
    string ObjectEntityType = "Recipe_Value",
    SourceReferenceCodec ReferenceCodec = SourceReferenceCodec.None,
    bool PreserveLexicalValue = false,
    string? RelationParent = null,
    double? RelationRank = null,
    string? LexicalRelationName = null,
    string? ValueAliasProperty = null,
    string? ContextField = null,
    string? DefaultValue = null,
    bool OmitDefaultTestimony = false,
    SourceSubjectMode SubjectMode = SourceSubjectMode.Record,
    SourcePairMode PairMode = SourcePairMode.None,
    SourceRelationResolver RelationResolver = SourceRelationResolver.None,
    string? RelationField = null,
    string? TrunkField = null,
    string? PairValueSeparator = null,
    bool OmitWhenEqualsSubject = false,
    bool GroupOnce = false,
    string? IdentityTable = null,
    // The claim's object when the value itself is not the object: a binary property's
    // value (Y/N) confirms or refutes "subject HAS_PROPERTY <ObjectLiteral>".
    string? ObjectLiteral = null,
    // A fixed context qualifying every claim of the field (the source property the
    // value belongs to, when the governed relation is shared by several properties).
    string? ContextLiteral = null,
    // This integer is how many times the named sibling claim (its syntax path) was
    // observed: games on that attestation, not a claim of its own.
    string? ObservationOf = null,
    // This value in [0,1] is the named sibling claim's score (a draw is 0.5).
    string? ScoreOf = null,
    // A governed vocabulary the value resolves through ("pos/wordnet": WordNet ss_type
    // n -> NOUN per engine/manifest/vocabulary/pos_alias.tsv). A value the vocabulary does not map
    // stays the source's own value.
    string? Vocabulary = null,
    // Claims of this field accumulate across the artifact by identity and are staged
    // once at its end as graded games: "dogs HAS_POS NOUN @eng" observed n times.
    bool Aggregate = false,
    // Governed qualifiers asserted by every claim of this field ("identifier/iso639-1"),
    // multi-select flags on the attestation (engine/manifest/qualifiers.toml).
    IReadOnlyList<string>? Qualifiers = null,
    // A qualifier read from the record: the attribute QualifierField's value names a
    // value of QualifierFamily (a UCD name alias's type -> name/correction).
    string? QualifierFamily = null,
    string? QualifierField = null,
    // The object is [record subject, value]: the same identity the record's child
    // subject named value has (a FrameNet requiresFE names an FE of the same frame).
    bool ObjectScopedToRecord = false,
    // The object is [content(record attribute ObjectScopePath), value] typed
    // ObjectEntityType ([@frame, FE] in a lexical-unit file).
    string? ObjectScopePath = null,
    // The object is the record's subject (an annotated target word FORM_OF its LU).
    bool ObjectIsRecordSubject = false,
    // SubjectMode Span: attributes naming the inclusive codepoint offsets of the span
    // of TrunkField's text that is the subject.
    string? SpanStartField = null,
    string? SpanEndField = null,
    // The object is the composition of these parts read from the lowering attributes;
    // "value()" names the field's own value (each item of a separated sequence).
    IReadOnlyList<SourceIdentityPart>? ObjectParts = null,
    // The claim is refuted when attribute OutcomeField reads RefuteValue (a VerbNet
    // selectional restriction Value="-", a negated predicate bool="!").
    string? OutcomeField = null,
    string? RefuteValue = null,
    // A value starting with DrawPrefix is the source's uncertain claim: the prefix is
    // stripped and the claim is a draw (VerbNet wn="?sprawl%2:38:00").
    string? DrawPrefix = null,
    // KeyValueComposition: a key's value list ("activity_type: walk, travel" -> one
    // [key, value] per value), a relation claiming items without a key ("+manner"),
    // and values/flags signed by a leading + (confirm) or - (refute).
    string? ValueListSeparator = null,
    string? FlagRelation = null,
    bool SignedValues = false,
    // The claim is made only in its ContextField's context: the source states the
    // context-free claim elsewhere (SpecialCasing's unconditional lines are the UCD XML's
    // full case mappings; its conditional ones are its own).
    bool RequireContext = false);

/// <summary>Which entity a grouped testimony field speaks about.</summary>
public enum SourceSubjectMode
{
    /// <summary>The record's subject; the field value is the object.</summary>
    Record = 0,
    /// <summary>The field value is the subject; the record's subject is the object.</summary>
    Value = 1,
    /// <summary>The group trunk (for example a sentence's text) is the subject.</summary>
    Trunk = 2,
    /// <summary>A span of the trunk text between two offsets is the subject.</summary>
    Span = 3,
}

/// <summary>How a field carrying several items is split.</summary>
public enum SourcePairMode
{
    None = 0,
    /// <summary>Items "key&lt;separator&gt;value": the key names the relation, the value is the object.</summary>
    RelationKeyObjectValue = 1,
    /// <summary>Resolved reference items: the referenced row is the subject, the value names the relation.</summary>
    SubjectReferenceRelationValue = 2,
    /// <summary>Items "key&lt;separator&gt;value": the object is the ordered composition [key, value] under the field's relation.</summary>
    KeyValueComposition = 3,
}

/// <summary>The relation vocabulary a source label resolves through.</summary>
public enum SourceRelationResolver
{
    None = 0,
    Deprel = 1,
    EnhancedDeprel = 2,
    Feature = 3,
    /// <summary>A governed relation canonical or alias surface (for example WN-LMF relType).</summary>
    Surface = 4,
}

/// <summary>An in-group pointer column resolved by the provider to the referenced row's value.</summary>
public sealed record SourceDelimitedReference(
    string Column,
    string KeyColumn,
    string TargetColumn,
    string RootValue = "",
    string PairSeparator = "",
    string PairValueSeparator = "");

public sealed record SourceRecipeStructure(
    string SyntaxPath,
    string SemanticType,
    SourceFieldDisposition Disposition);

public enum SourceSubjectBindingKind
{
    // Legacy expansion: one source range is lowered onto every codepoint subject.
    CodepointRange,
    ContentField,
    ClassifierField,
    // Canonical interval subject: singleton collapses to its codepoint; a true span
    // becomes one Range entity/physicality over the two canonical endpoints.
    CodepointInterval,
    // The subject is the ordered composition of IdentityParts (RCP8): a FrameNet
    // lexical-unit file's subject is [@frame, lexemes' text, UPOS].
    Composition,
}

public sealed record SourceRecipeSubjectBinding(
    SourceSubjectBindingKind Kind,
    string IdentityField,
    string? RangeStartField = null,
    string? LastField = null,
    string? EntityNamespace = null,
    string EntityType = "Recipe_Subject",
    string? SequenceSeparator = null,
    string? IdentityTable = null,
    IReadOnlyList<SourceIdentityPart>? IdentityParts = null);

/// <summary>
/// A source's own identifier table, collected from the artifact before lowering. A
/// source that names things by packaging ids (a WN-LMF synset "oewn-02084071-n", a
/// sense "oewn-dog__1.05.00..") also states what those ids denote (the synset's ILI,
/// the sense's word). References resolve through that statement so every lexicon
/// converges on the same concepts and words; an id whose value is absent stands for
/// itself. KeyPath/ValuePath are record-relative ("@id", "Sense/@id", "Lemma/@writtenForm").
/// </summary>
public sealed record SourceIdentityTable(
    string Name,
    string RecordName,
    string KeyPath,
    string ValuePath,
    IReadOnlyList<string>? AbsentValues = null);

public sealed record SourceRecipeProviderRoute(
    string RecordName,
    string NamespaceUri,
    string FieldPrefix,
    SourceRecipeSubjectBinding Subject,
    IReadOnlyList<string> StructurePaths,
    IReadOnlyDictionary<string, string>? ChildFieldPrefixes = null,
    string? RangeRelationProperty = null,
    string? RangeRelationName = null,
    string? RangeStartField = null,
    string? RangeEndField = null,
    bool InheritParentAttributes = false,
    SourceParseStructure? ParseStructure = null,
    // This record states its own witness: the content composition of these fields
    // ([id, version] of a WN-LMF Lexicon). Claims of the record and of every record
    // within its scope are that witness's observations.
    IReadOnlyList<string>? WitnessFields = null,
    // Nested elements that are their own subjects (RCP8): each is the ordered
    // composition [record subject, content(identity attribute)].
    IReadOnlyList<SourceChildSubject>? ChildSubjects = null,
    IReadOnlyList<SourceElementComposition>? ElementCompositions = null,
    IReadOnlyList<SourceConditionalPrefix>? ConditionalPrefixes = null);

/// <summary>
/// A child path's field prefix chosen by an attribute condition over the element and its
/// ancestors, whose attributes are also addressable qualified ("layer@name") and whose
/// text children as "name()" ("text()").
/// </summary>
public sealed record SourceConditionalPrefix(
    string ChildPath,
    string Field,
    string Value,
    string Prefix);

/// <summary>
/// A nested element that is its own subject: a FrameNet frame element is the
/// composition [frame, element name]. Its fields and descendants are claims about it;
/// ParentRelation, when declared, links the record's subject to it.
/// </summary>
public sealed record SourceChildSubject(
    string ChildPath,
    string IdentityField,
    string? ParentRelation = null,
    string EntityType = "Recipe_Subject",
    // The link runs child -> parent (a lexical unit EVOKES_FRAME its frame).
    bool ChildIsSubject = false,
    // Several identity parts instead of IdentityField: [record subject, part...].
    IReadOnlyList<SourceIdentityPart>? IdentityParts = null,
    // The child exists apart from the record (a lemma's lexeme is the lexeme anywhere):
    // its identity is its own parts, without the enclosing subject.
    bool Standalone = false,
    // The parent link is refuted when the child's OutcomeField reads RefuteValue.
    string? OutcomeField = null,
    string? RefuteValue = null);

/// <summary>
/// One part of a composed child identity: a child-relative path ("lexeme/@name", "@POS")
/// whose values in source order are joined (Join, default one space) and optionally
/// resolved through a governed vocabulary ("pos/framenet": V -> VERB).
/// </summary>
public sealed record SourceIdentityPart(
    string Path,
    string? Vocabulary = null,
    string? Join = null,
    // The part is [content(record attribute ScopePath), value], typed ScopeEntityType:
    // a valence unit's FE is the frame's element [@frame, FE].
    string? ScopePath = null,
    string? ScopeEntityType = null,
    // The part is the ordered compositions of the child elements of this name, each
    // composed by its own element composition (a pattern's valence units).
    string? Children = null,
    // The source's own identifier syntax, decoded: the value before or after the last
    // SplitLast separator characters ("December.n" -> lemma "December", POS code "n").
    // Side Each: every piece between any of the SplitLast characters is its own
    // component, in order ("run-51.3.2" split on "-." -> [run, 51, 3, 2]).
    string? SplitLast = null,
    SourceIdentitySide Side = SourceIdentitySide.Whole,
    // Characters the source writes for a space inside a word ("goose_step").
    string? SpaceMark = null,
    // A fixed value the recipe states instead of reading one: the property a UCD value
    // belongs to ([General_Category, Uppercase_Letter]).
    string? Literal = null,
    // The part's pieces compose one inner composition typed ScopeEntityType (a VerbNet
    // class [conduct, 111, 1] inside the role key [class, Agent]).
    bool Nested = false,
    // The value resolves through the value aliases of the enclosing route's identity or
    // the field (UCD "Basic Latin" -> Basic_Latin, as every blk value names it).
    bool Aliased = false,
    // The value is code points (hex, space-separated): the part is their text, a single
    // character being its own atom ([Bidi_Paired_Bracket, ")"]).
    bool Codepoints = false);

public enum SourceIdentitySide { Whole = 0, Before = 1, After = 2, Each = 3 }

/// <summary>
/// A nested element composed from its parts (RCP8) and, when Relation is declared,
/// claimed from the record's subject with the element's ObservationField as games:
/// "walk.v HAS_VALENCE_PATTERN [[Self_mover, Ext, NP], [Goal, Dep, PP[into]]]" x total.
/// </summary>
public sealed record SourceElementComposition(
    string Path,
    IReadOnlyList<SourceIdentityPart> Parts,
    string EntityType,
    string? Relation = null,
    string? ObservationField = null,
    // Composed only where attribute WhenField (the element's or an ancestor's) reads
    // WhenValue; elsewhere the element lowers as fields (a VerbNet restriction group is
    // one formula when logic="or", independent claims otherwise).
    string? WhenField = null,
    string? WhenValue = null);

/// <summary>
/// A grouped delimited record (a CoNLL-U sentence) lowered to its trunk's parse
/// physicality: the token forms in order, each vertex carrying governed codes (UPOS
/// index, universal deprel code, head ordinal) in its metadata rather than one
/// testimony row per token occurrence.
/// </summary>
public sealed record SourceParseStructure(
    string TrunkField,
    string IdColumn,
    string FormColumn,
    string UposColumn,
    string HeadColumn,
    string DeprelColumn,
    string UposVocabulary = "pos/upos");

public enum SourceArtifactDisposition
{
    Admitted,
    EquivalentPackaging,
    Superseded,
    Excluded,
    Unsupported,
    Absent,
}

/// <summary>One member of the complete physical artifact graph bound by a source generation.</summary>
public sealed record SourceRecipeArtifact(
    string Selector,
    string Provider,
    string Syntax,
    SourceArtifactDisposition Disposition,
    bool Required,
    string? Reason = null,
    IReadOnlyList<string>? DependsOn = null,
    IReadOnlyList<string>? ProviderRoutes = null);

/// <summary>Concrete delimited syntax; field meanings remain ordinary recipe rules.</summary>
public sealed record SourceDelimitedSyntax(
    string RecordName,
    IReadOnlyList<string> Columns,
    string Separator = ";",
    string CommentPrefix = "#",
    bool TrimFields = true,
    string NamespaceUri = "",
    string DirectivePrefix = "",
    string DirectiveRecordName = "",
    IReadOnlyList<string>? DirectiveColumns = null,
    string RangeColumn = "",
    string RangeSeparator = "..",
    string RangeFirstField = "first",
    string RangeLastField = "last",
    int MinimumColumns = 0,
    bool AllowTrailingEmptyColumn = false,
    bool GroupBlankLines = false,
    string GroupAttributeSeparator = "",
    string SkipKeyColumn = "",
    string SkipKeyCharacters = "",
    IReadOnlyList<SourceDelimitedReference>? References = null,
    IReadOnlyDictionary<string, string>? Constants = null,
    // Leading lines that name the columns (a TSV header row), not records.
    int HeaderLines = 0,
    // A line whose first field is a key here is read with that key's columns instead
    // (PropertyValueAliases: "ccc" lines carry a numeric value before the names).
    IReadOnlyDictionary<string, IReadOnlyList<string>>? KeyedColumns = null,
    // Data kept in comments (UTS 51 emoji-test): the trailing comment as a column, fields
    // captured from it by CommentPattern's groups, and comment lines "key: value" whose
    // value holds for the records that follow (StateKeys, StateSeparator).
    string? CommentColumn = null,
    string? CommentPattern = null,
    IReadOnlyList<string>? CommentGroups = null,
    IReadOnlyList<string>? StateKeys = null,
    string StateSeparator = ":")
{
    public bool IsGrouped => GroupBlankLines || (References?.Count ?? 0) != 0 || (Constants?.Count ?? 0) != 0;
}

/// <summary>RDF 1.1 Turtle syntax: one record per statement subject. Attributes are the
/// subject ("about"), rdf:type ("a") and each predicate by its compact name, with
/// "#ns", "#lang" and "#type" companions; field meanings remain ordinary recipe rules.</summary>
public sealed record SourceTurtleSyntax(
    string RecordName = "resource",
    string NamespaceUri = "",
    // rdf:type IRIs whose resources describe the file itself (a vocabulary header,
    // class declarations): declared packaging, never records.
    IReadOnlyList<string>? ExcludeTypes = null);

/// <summary>
/// Versioned, deterministic semantic recipe.  It is independent of batching,
/// concurrency and storage tuning: those change execution, not what an assertion means.
/// </summary>
public sealed class SemanticSourceRecipe
{
    private readonly IReadOnlyDictionary<string, SourceRecipeField> _fields;
    private readonly IReadOnlyList<SourceRecipeField> _fieldList;
    private readonly IReadOnlyDictionary<string, SourceRecipeStructure> _structures;
    private readonly IReadOnlyDictionary<string, string> _valueAliases;
    private readonly IReadOnlyDictionary<string, SourceRecipeProviderRoute> _providerRoutes;
    private readonly IReadOnlyDictionary<string, string> _propertyAliases;
    private readonly IReadOnlyDictionary<string, SourceRecipeArtifact> _artifacts;

    public SemanticSourceRecipe(
        string authority,
        string release,
        string provider,
        string syntax,
        IEnumerable<SourceRecipeField> fields,
        IEnumerable<SourceRecipeStructure>? structures = null,
        IReadOnlyDictionary<string, string>? valueAliases = null,
        IEnumerable<SourceRecipeProviderRoute>? providerRoutes = null,
        IEnumerable<SourceRecipeArtifact>? artifacts = null,
        SourceDelimitedSyntax? delimitedSyntax = null,
        IEnumerable<SourceIdentityTable>? identityTables = null,
        IReadOnlyDictionary<string, string>? attributeVocabularies = null,
        SourceTurtleSyntax? turtleSyntax = null)
    {
        Authority = Required(authority, nameof(authority));
        Release = Required(release, nameof(release));
        Provider = Required(provider, nameof(provider));
        Syntax = Required(syntax, nameof(syntax));
        DelimitedSyntax = delimitedSyntax;
        TurtleSyntax = turtleSyntax;
        if (delimitedSyntax is not null && turtleSyntax is not null)
            throw new ArgumentException("A recipe declares one concrete syntax.", nameof(turtleSyntax));
        if (turtleSyntax is { } turtle) Required(turtle.RecordName, nameof(turtleSyntax));
        if (delimitedSyntax is { } delimited)
        {
            Required(delimited.RecordName, nameof(delimitedSyntax));
            // A tab is a real separator; only an absent separator is invalid.
            if (string.IsNullOrEmpty(delimited.Separator))
                throw new ArgumentException("Delimited syntax requires a separator.", nameof(delimitedSyntax));
            if (delimited.Columns.Count == 0 || delimited.Columns.Any(string.IsNullOrWhiteSpace)
                || delimited.Columns.Distinct(StringComparer.Ordinal).Count() != delimited.Columns.Count
                || delimited.Separator.IndexOfAny(['\r', '\n']) >= 0
                || delimited.MinimumColumns < 0 || delimited.MinimumColumns > delimited.Columns.Count)
                throw new ArgumentException("Delimited syntax requires distinct columns and a non-line separator.", nameof(delimitedSyntax));
            if (delimited.DirectivePrefix.Length != 0 && (delimited.DirectiveRecordName.Length == 0
                || delimited.DirectiveColumns is not { Count: > 0 }))
                throw new ArgumentException("Directive syntax requires a record route and columns.", nameof(delimitedSyntax));
            if (delimited.RangeColumn.Length != 0 && (delimited.RangeSeparator.Length == 0
                || delimited.RangeFirstField.Length == 0 || delimited.RangeLastField.Length == 0))
                throw new ArgumentException("Range syntax requires separator and endpoint fields.", nameof(delimitedSyntax));
        }

        SourceRecipeField[] fieldArray = fields?.ToArray()
            ?? throw new ArgumentNullException(nameof(fields));
        if (fieldArray.Length == 0)
            throw new ArgumentException("A semantic recipe must account for at least one field.", nameof(fields));

        var map = new Dictionary<string, SourceRecipeField>(StringComparer.Ordinal);
        foreach (SourceRecipeField field in fieldArray)
        {
            Validate(field);
            if (!map.TryAdd(field.SyntaxPath, field))
                throw new ArgumentException($"Duplicate recipe syntax path '{field.SyntaxPath}'.", nameof(fields));
        }
        _fields = map;
        _fieldList = fieldArray.OrderBy(static f => f.SyntaxPath, StringComparer.Ordinal).ToArray();
        var propertyAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SourceRecipeField field in fieldArray)
        {
            int attribute = field.SyntaxPath.LastIndexOf("/@", StringComparison.Ordinal);
            if (attribute < 0) continue;
            string alias = field.SyntaxPath[(attribute + 2)..];
            if (propertyAliases.TryGetValue(alias, out string? existing)
                && existing != field.PropertyName)
                continue;
            propertyAliases[alias] = field.PropertyName;
        }
        _propertyAliases = propertyAliases;

        Structures = (structures ?? []).OrderBy(static s => s.SyntaxPath, StringComparer.Ordinal).ToArray();
        var structureMap = new Dictionary<string, SourceRecipeStructure>(StringComparer.Ordinal);
        foreach (SourceRecipeStructure structure in Structures)
        {
            Required(structure.SyntaxPath, nameof(structures));
            Required(structure.SemanticType, nameof(structures));
            ValidateDisposition(structure.Disposition, structure.SyntaxPath);
            if (!structureMap.TryAdd(structure.SyntaxPath, structure))
                throw new ArgumentException(
                    $"Duplicate recipe structure path '{structure.SyntaxPath}'.", nameof(structures));
        }
        _structures = structureMap;

        _valueAliases = valueAliases is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(valueAliases, StringComparer.Ordinal);

        ProviderRoutes = (providerRoutes ?? []).OrderBy(
            static route => route.RecordName, StringComparer.Ordinal).ToArray();
        _providerRoutes = ProviderRoutes.ToDictionary(
            static route => route.RecordName, StringComparer.Ordinal);

        Artifacts = (artifacts ?? []).OrderBy(
            static artifact => artifact.Selector, StringComparer.Ordinal).ToArray();
        _artifacts = Artifacts.ToDictionary(
            static artifact => artifact.Selector, StringComparer.Ordinal);
        foreach (SourceRecipeArtifact artifact in Artifacts)
        {
            Required(artifact.Selector, nameof(artifacts));
            Required(artifact.Provider, nameof(artifacts));
            Required(artifact.Syntax, nameof(artifacts));
            if (artifact.Disposition is SourceArtifactDisposition.Excluded
                or SourceArtifactDisposition.Unsupported
                or SourceArtifactDisposition.Absent
                && string.IsNullOrWhiteSpace(artifact.Reason))
                throw new ArgumentException(
                    $"Artifact '{artifact.Selector}' requires an explicit disposition reason.",
                    nameof(artifacts));
        }

        IdentityTables = (identityTables ?? []).ToArray();
        AttributeVocabularies = new SortedDictionary<string, string>(
            (IDictionary<string, string>?)attributeVocabularies ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (SourceIdentityTable table in IdentityTables)
        {
            Required(table.Name, nameof(identityTables));
            Required(table.RecordName, nameof(identityTables));
            Required(table.KeyPath, nameof(identityTables));
            Required(table.ValuePath, nameof(identityTables));
            tableNames.Add(table.Name);
        }
        foreach (string? used in fieldArray.Select(static f => f.IdentityTable)
                     .Concat(ProviderRoutes.Select(static r => r.Subject.IdentityTable)))
            if (used is not null && !tableNames.Contains(used))
                throw new ArgumentException($"Recipe references undeclared identity table '{used}'.",
                    nameof(identityTables));

        _canonicalForm = new Lazy<string>(() =>
        {
            using var document = new MemoryStream();
            LaplaceCookbookJson.WriteJson(this, document);
            return System.Text.Encoding.UTF8.GetString(document.ToArray());
        });
        // The recipe is a document; its identity is that document's content.
        _recipeId = new Lazy<Hash128>(() => ContentEmitter.RootId(CanonicalForm)
            ?? throw new InvalidOperationException(
                $"Recipe {Authority}/{Release} could not be composed as content."));
    }

    private readonly Lazy<string> _canonicalForm;
    private readonly Lazy<Hash128> _recipeId;

    /// <summary>The same recipe over a different concrete delimited syntax (for example
    /// with per-artifact record constants).</summary>
    public SemanticSourceRecipe WithDelimitedSyntax(SourceDelimitedSyntax syntax) =>
        new(Authority, Release, Provider, Syntax, Fields, Structures, ValueAliases,
            ProviderRoutes, Artifacts, syntax, IdentityTables, AttributeVocabularies, TurtleSyntax);

    public string Authority { get; }
    public string Release { get; }
    public string Provider { get; }
    public string Syntax { get; }
    public SourceDelimitedSyntax? DelimitedSyntax { get; }
    public SourceTurtleSyntax? TurtleSyntax { get; }
    public IReadOnlyList<SourceRecipeField> Fields => _fieldList;
    public IReadOnlyList<SourceRecipeStructure> Structures { get; }
    public IReadOnlyDictionary<string, string> ValueAliases => _valueAliases;
    public IReadOnlyList<SourceRecipeProviderRoute> ProviderRoutes { get; }
    public IReadOnlyList<SourceRecipeArtifact> Artifacts { get; }
    public IReadOnlyList<SourceIdentityTable> IdentityTables { get; }
    /// <summary>Source attributes normalized once at parse through a governed vocabulary
    /// (a WN-LMF lexicon's "language" -> ISO 639-3), so every value and context read
    /// from them carries the Laplace-standard key.</summary>
    public IReadOnlyDictionary<string, string> AttributeVocabularies { get; }
    /// <summary>The recipe document in its canonical JSON form.</summary>
    public string CanonicalForm => _canonicalForm.Value;
    public Hash128 RecipeId => _recipeId.Value;

    public SourceRecipeField Field(string syntaxPath) =>
        _fields.TryGetValue(syntaxPath, out SourceRecipeField? field)
            ? field
            : throw new KeyNotFoundException(
                $"Recipe {Authority}/{Release} has no disposition for syntax field '{syntaxPath}'.");

    public bool TryField(string syntaxPath, out SourceRecipeField field) =>
        _fields.TryGetValue(syntaxPath, out field!);

    public SourceRecipeStructure Structure(string syntaxPath) =>
        _structures.TryGetValue(syntaxPath, out SourceRecipeStructure? structure)
            ? structure
            : throw new KeyNotFoundException(
                $"Recipe {Authority}/{Release} has no disposition for syntax structure '{syntaxPath}'.");

    public bool TryStructure(string syntaxPath, out SourceRecipeStructure structure) =>
        _structures.TryGetValue(syntaxPath, out structure!);

    public string CanonicalValue(string propertyName, string value)
    {
        string aliasProperty = Fields.FirstOrDefault(field =>
            field.PropertyName == propertyName && field.ValueAliasProperty is not null)
            ?.ValueAliasProperty ?? propertyName;
        return _valueAliases.TryGetValue(ValueAliasKey(aliasProperty, value), out string? canonical)
            ? canonical : value;
    }

    public string CanonicalProperty(string alias) =>
        _propertyAliases.TryGetValue(alias, out string? canonical) ? canonical : alias;

    public SourceRecipeProviderRoute ProviderRoute(string recordName) =>
        _providerRoutes.TryGetValue(recordName, out SourceRecipeProviderRoute? route)
            ? route
            : throw new KeyNotFoundException(
                $"Recipe {Authority}/{Release} has no provider route for record '{recordName}'.");

    public SourceRecipeArtifact Artifact(string selector) =>
        _artifacts.TryGetValue(selector, out SourceRecipeArtifact? artifact)
            ? artifact
            : throw new KeyNotFoundException(
                $"Recipe {Authority}/{Release} has no artifact disposition for '{selector}'.");

    private static string Required(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Recipe values cannot be empty.", parameter);
        return value;
    }

    private static void Validate(SourceRecipeField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        Required(field.SyntaxPath, nameof(field.SyntaxPath));
        Required(field.PropertyName, nameof(field.PropertyName));
        ValidateDisposition(field.Disposition, field.SyntaxPath);
        if (field.Disposition.HasFlag(SourceFieldDisposition.Excluded)
            && field.Disposition != SourceFieldDisposition.Excluded)
            throw new ArgumentException(
                $"Excluded field '{field.SyntaxPath}' cannot also be admitted.", nameof(field));
        if (field.OmitDefaultTestimony && field.DefaultValue is null)
            throw new ArgumentException(
                $"Field '{field.SyntaxPath}' cannot omit default testimony without declaring its semantic default.",
                nameof(field));
        if (field.OmitDefaultTestimony
            && !field.Disposition.HasFlag(SourceFieldDisposition.Testimony))
            throw new ArgumentException(
                $"Field '{field.SyntaxPath}' cannot omit default testimony when it is not testimony.",
                nameof(field));
        if (field.DefaultValue is not null && field.AbsentSentinel is not null
            && string.Equals(field.DefaultValue, field.AbsentSentinel, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Field '{field.SyntaxPath}' cannot use the same lexical value for absence and semantic default.",
                nameof(field));
    }

    private static void ValidateDisposition(SourceFieldDisposition disposition, string syntaxPath)
    {
        if (disposition == SourceFieldDisposition.None)
            throw new ArgumentException($"Field or structure '{syntaxPath}' has no disposition.");
    }

    public static string ValueAliasKey(string propertyName, string value)
    {
        var normalized = new StringBuilder(value.Length);
        foreach (char c in value)
            if (c is not ('_' or '-' or ' ' or '\t'))
                normalized.Append(char.ToUpperInvariant(c));
        return $"{propertyName}\0{normalized}";
    }
}

public readonly record struct LaplaceRecipeKey(
    string Authority,
    string Release,
    string Provider,
    string Syntax);

public enum LaplaceRecipeFieldChangeKind
{
    Added,
    Removed,
    Changed,
}

public sealed record LaplaceRecipeFieldChange(
    string SyntaxPath,
    LaplaceRecipeFieldChangeKind Kind,
    SourceRecipeField? Before,
    SourceRecipeField? After);

public sealed record LaplaceRecipeSchemaReport(
    IReadOnlyList<string> UnknownProviderFields,
    IReadOnlyList<string> MissingRecipeFields)
{
    public bool IsComplete => UnknownProviderFields.Count == 0 && MissingRecipeFields.Count == 0;
}

/// <summary>
/// Process-wide catalog of immutable Laplace Recipes. Providers register one
/// authority/release/provider/syntax generation; execution resolves the exact generation rather
/// than switching on a decomposer class name.
/// </summary>
public sealed class LaplaceCookbook
{
    public static LaplaceCookbook Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<LaplaceRecipeKey, SemanticSourceRecipe> _recipes = [];
    private readonly Dictionary<Hash128, SemanticSourceRecipe> _byId = [];

    public void Register(SemanticSourceRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var key = new LaplaceRecipeKey(
            recipe.Authority, recipe.Release, recipe.Provider, recipe.Syntax);
        lock (_gate)
        {
            if (_recipes.TryGetValue(key, out SemanticSourceRecipe? existing))
            {
                if (existing.RecipeId == recipe.RecipeId) return;
                throw new InvalidOperationException(
                    $"Recipe generation {key.Authority}/{key.Release}/{key.Provider}/{key.Syntax} "
                    + $"is already registered as {existing.RecipeId}; refusing {recipe.RecipeId}.");
            }
            if (_byId.TryGetValue(recipe.RecipeId, out SemanticSourceRecipe? collision))
                throw new InvalidOperationException(
                    $"Recipe id {recipe.RecipeId} is already registered for "
                    + $"{collision.Authority}/{collision.Release}/{collision.Syntax}.");
            _recipes.Add(key, recipe);
            _byId.Add(recipe.RecipeId, recipe);
        }
    }

    public SemanticSourceRecipe Resolve(LaplaceRecipeKey key)
    {
        lock (_gate)
            return _recipes.TryGetValue(key, out SemanticSourceRecipe? recipe)
                ? recipe
                : throw new KeyNotFoundException(
                    $"No Laplace Recipe is registered for "
                    + $"{key.Authority}/{key.Release}/{key.Provider}/{key.Syntax}.");
    }

    public SemanticSourceRecipe Resolve(Hash128 recipeId)
    {
        lock (_gate)
            return _byId.TryGetValue(recipeId, out SemanticSourceRecipe? recipe)
                ? recipe
                : throw new KeyNotFoundException($"No Laplace Recipe is registered as {recipeId}.");
    }

    public IReadOnlyList<SemanticSourceRecipe> Recipes
    {
        get
        {
            lock (_gate)
                return _recipes.Values
                    .OrderBy(static recipe => recipe.Authority, StringComparer.Ordinal)
                    .ThenBy(static recipe => recipe.Release, StringComparer.Ordinal)
                    .ThenBy(static recipe => recipe.Provider, StringComparer.Ordinal)
                    .ThenBy(static recipe => recipe.Syntax, StringComparer.Ordinal)
                    .ToArray();
        }
    }

    public static LaplaceRecipeSchemaReport ValidateProviderSchema(
        SemanticSourceRecipe recipe,
        IEnumerable<string> providerSyntaxPaths)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(providerSyntaxPaths);
        var provider = providerSyntaxPaths.ToHashSet(StringComparer.Ordinal);
        string[] unknown = provider
            .Where(path => !recipe.TryField(path, out _))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] missing = recipe.Fields
            .Select(static field => field.SyntaxPath)
            .Where(path => !provider.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new LaplaceRecipeSchemaReport(unknown, missing);
    }

    public static IReadOnlyList<LaplaceRecipeFieldChange> Diff(
        SemanticSourceRecipe before,
        SemanticSourceRecipe after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var paths = before.Fields.Select(static field => field.SyntaxPath)
            .Concat(after.Fields.Select(static field => field.SyntaxPath))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var changes = new List<LaplaceRecipeFieldChange>();
        foreach (string path in paths)
        {
            bool had = before.TryField(path, out SourceRecipeField oldField);
            bool has = after.TryField(path, out SourceRecipeField newField);
            if (!had)
                changes.Add(new LaplaceRecipeFieldChange(
                    path, LaplaceRecipeFieldChangeKind.Added, null, newField));
            else if (!has)
                changes.Add(new LaplaceRecipeFieldChange(
                    path, LaplaceRecipeFieldChangeKind.Removed, oldField, null));
            else if (oldField != newField)
                changes.Add(new LaplaceRecipeFieldChange(
                    path, LaplaceRecipeFieldChangeKind.Changed, oldField, newField));
        }
        return changes;
    }
}

public readonly record struct SourceRecipeAssertion<TSubject>(
    TSubject Subject,
    string SyntaxPath,
    string RawValue);

public readonly record struct SourceRecipeValue(
    string Raw,
    bool IsAbsent,
    bool? Boolean,
    BigInteger? Integer,
    IReadOnlyList<string> Sequence,
    bool IsDefault);

public interface ILaplaceRecipeLoweringTarget<TSubject>
{
    void Lower(
        in SourceRecipeAssertion<TSubject> assertion,
        SourceRecipeField field,
        in SourceRecipeValue value);
}

/// <summary>
/// Shared fail-closed recipe interpreter. Syntax providers emit assertions; this
/// interpreter resolves the selected recipe, preserves the exact lexical value, and
/// supplies typed boolean/integer/sequence views to a substrate lowering target.
/// </summary>
public sealed class LaplaceRecipeInterpreter<TSubject>
{
    public LaplaceRecipeInterpreter(SemanticSourceRecipe recipe) =>
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));

    public SemanticSourceRecipe Recipe { get; }

    public void Lower(
        in SourceRecipeAssertion<TSubject> assertion,
        ILaplaceRecipeLoweringTarget<TSubject> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        SourceRecipeField field = Recipe.Field(assertion.SyntaxPath);
        if (field.Disposition == SourceFieldDisposition.Excluded) return;
        SourceRecipeValue value = Parse(field, assertion.RawValue);
        target.Lower(assertion, field, value);
    }

    public void LowerBatch(
        IEnumerable<SourceRecipeAssertion<TSubject>> assertions,
        ILaplaceRecipeLoweringTarget<TSubject> target)
    {
        ArgumentNullException.ThrowIfNull(assertions);
        foreach (SourceRecipeAssertion<TSubject> assertion in assertions)
            Lower(assertion, target);
    }

    private static SourceRecipeValue Parse(SourceRecipeField field, string raw)
    {
        raw ??= string.Empty;
        bool absent = field.AbsentSentinel is not null
            && string.Equals(raw, field.AbsentSentinel, StringComparison.Ordinal);
        bool isDefault = field.DefaultValue is not null
            && string.Equals(raw, field.DefaultValue, StringComparison.Ordinal);
        bool? boolean = null;
        BigInteger? integer = null;
        IReadOnlyList<string> sequence = [];
        if (!absent)
        {
            if (field.ValueKind == SourceValueKind.Boolean)
                boolean = raw switch
                {
                    "Y" or "Yes" or "true" or "True" or "1" => true,
                    "N" or "No" or "false" or "False" or "0" => false,
                    _ => throw new InvalidDataException(
                        $"Recipe field '{field.SyntaxPath}' received invalid boolean '{raw}'."),
                };
            if (field.ValueKind == SourceValueKind.Integer)
            {
                if (!BigInteger.TryParse(
                        raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                        out BigInteger parsed))
                    throw new InvalidDataException(
                        $"Recipe field '{field.SyntaxPath}' received invalid integer '{raw}'.");
                integer = parsed;
            }
            if (field.ValueKind is SourceValueKind.CodepointSequence
                or SourceValueKind.EnumeratedSequence)
            {
                string separator = field.SequenceSeparator
                    ?? throw new InvalidDataException(
                        $"Recipe sequence field '{field.SyntaxPath}' has no separator.");
                sequence = raw.Split(
                    separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
        return new SourceRecipeValue(raw, absent, boolean, integer, sequence, isDefault);
    }
}
