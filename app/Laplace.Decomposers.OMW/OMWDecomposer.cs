using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

public sealed class OMWDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OMWDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 LanguageTypeId = Hash128.OfCanonical("substrate/type/Language/v1");
    private static readonly Hash128 SynsetTypeId   = Hash128.OfCanonical("substrate/type/WordNet_Synset/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "OMWDecomposer";
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_EXAMPLE");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 8192;

        var b = NewBuilder("omw/batch-0", batch);
        int count = 0, batchNum = 0;

        foreach (string tabFile in Directory.EnumerateFiles(wnsDir, "wn-data-*.tab", SearchOption.AllDirectories))
        {
            string fileLang = FileLang(tabFile);
            await foreach (var row in ParseFileAsync(tabFile, fileLang, ct))
            {
                ct.ThrowIfCancellationRequested();
                EmitRow(b, row);
                if (++count >= batch)
                {
                    if (!options.DryRun) yield return b.Build();
                    b = NewBuilder($"omw/batch-{++batchNum}", batch);
                    count = 0;
                    await Task.Yield();
                }
            }
        }
        if (count > 0 && !options.DryRun) yield return b.Build();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(2_676_800L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void EmitRow(SubstrateChangeBuilder b, OmwRow row)
    {
        var contentId = ContentEmitter.Emit(b, row.Value, Source);
        if (contentId is null) return;

        Hash128 langId = LanguageReference.Resolve(row.Lang);
        b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, LanguageTypeId, Source));
        b.AddEntity(new EntityRow(row.SynsetId, EntityTier.Vocabulary, SynsetTypeId, Source));

        switch (row.Type)
        {
            case OmwType.Lemma:
                b.AddAttestation(RelationTypeRegistry.Attest(
                    contentId.Value, "IS_TRANSLATION_OF", row.SynsetId, Source, SourceTrust.AcademicCurated));
                b.AddAttestation(RelationTypeRegistry.Attest(
                    contentId.Value, "HAS_LANGUAGE", langId, Source, SourceTrust.AcademicCurated));
                break;
            case OmwType.Def:
                b.AddAttestation(RelationTypeRegistry.Attest(
                    row.SynsetId, "HAS_DEFINITION", contentId.Value, Source, SourceTrust.AcademicCurated,
                    contextId: langId));
                break;
            case OmwType.Exe:
                b.AddAttestation(RelationTypeRegistry.Attest(
                    row.SynsetId, "HAS_EXAMPLE", contentId.Value, Source, SourceTrust.AcademicCurated,
                    contextId: langId));
                break;
        }
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 6,
            physicalityCapacity: batch * 6,
            attestationCapacity: batch * 2);

    private static string FileLang(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int dash = name.LastIndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : "und";
    }

    private static async IAsyncEnumerable<OmwRow> ParseFileAsync(
        string path, string fileLang, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (line.Length == 0 || line[0] == '#') continue;

            var cols = line.Split('\t');
            if (cols.Length < 3) continue;

            string synKey = cols[0];
            string typeField = cols[1];

            string lang = fileLang;
            string type;
            var tf = typeField.Split(':');
            if (tf.Length == 1) { type = tf[0]; }
            else { lang = tf[0]; type = tf[1]; }

            OmwType kind;
            string value;
            switch (type)
            {
                case "lemma": kind = OmwType.Lemma; value = cols.Length > 2 ? cols[2] : ""; break;
                case "def":   kind = OmwType.Def;   value = cols.Length > 3 ? cols[3] : (cols.Length > 2 ? cols[2] : ""); break;
                case "exe":   kind = OmwType.Exe;   value = cols.Length > 3 ? cols[3] : (cols.Length > 2 ? cols[2] : ""); break;
                default: continue;
            }
            value = value.Replace('_', ' ').Trim();
            if (value.Length == 0) continue;

            int dash = synKey.LastIndexOf('-');
            if (dash < 0 || dash + 1 >= synKey.Length) continue;
            if (!long.TryParse(synKey[..dash], out long offset)) continue;
            char pos = synKey[dash + 1] == 's' ? 'a' : synKey[dash + 1];

            Hash128 synId = SourceEntityIdConventions.WordNetSynset(offset, pos);
            yield return new OmwRow(synId, lang, kind, value);
        }
    }

    private enum OmwType { Lemma, Def, Exe }

    private readonly record struct OmwRow(Hash128 SynsetId, string Lang, OmwType Type, string Value);
}
