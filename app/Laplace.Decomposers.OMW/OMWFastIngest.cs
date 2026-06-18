using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

public static class OMWFastIngest
{
    public static async IAsyncEnumerable<SubstrateChange> IngestAsync(
        string wnsDir,
        LanguageFilter? langs,
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var b = NewBuilder("omw/batch-0", batchSize);
        int count = 0, batchNum = 0;

        foreach (string tabFile in Directory.EnumerateFiles(wnsDir, "wn-data-*.tab", SearchOption.AllDirectories))
        {
            string fileLang = FileLang(tabFile);
            if (langs?.MatchesRaw(fileLang) == false) continue;

            await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(tabFile, ct))
            {
                if (!TryParseRow(line.Span, fileLang, out var row, out var valueUtf8)) continue;
                EmitRow(b, row, valueUtf8);
                if (++count >= batchSize)
                {
                    yield return b.Build();
                    b = NewBuilder($"omw/batch-{++batchNum}", batchSize);
                    count = 0;
                }
            }
        }

        if (count > 0) yield return b.Build();
    }

    public static bool TryParseRow(
        ReadOnlySpan<byte> line, string fileLang, out OmwRow row, out ReadOnlySpan<byte> valueUtf8)
    {
        row = default;
        valueUtf8 = default;
        if (line.IsEmpty || line[0] == (byte)'#') return false;
        if (!TsvSpan.TryField(line, 0, out var synKey)) return false;
        if (!TsvSpan.TryField(line, 1, out var typeField)) return false;

        string lang = fileLang;
        ReadOnlySpan<byte> typeSpan = typeField;
        int colon = typeField.IndexOf((byte)':');
        if (colon >= 0)
        {
            lang = Encoding.UTF8.GetString(typeField[..colon]);
            typeSpan = typeField[(colon + 1)..];
        }

        OmwType rowType;
        ReadOnlySpan<byte> valueSpan;
        if (typeSpan.SequenceEqual("lemma"u8))
        {
            rowType = OmwType.Lemma;
            if (!TsvSpan.TryField(line, 2, out valueSpan)) return false;
        }
        else if (typeSpan.SequenceEqual("def"u8))
        {
            rowType = OmwType.Def;
            valueSpan = TsvSpan.TryField(line, 3, out var v3) ? v3
                : (TsvSpan.TryField(line, 2, out var v2) ? v2 : default);
        }
        else if (typeSpan.SequenceEqual("exe"u8))
        {
            rowType = OmwType.Exe;
            valueSpan = TsvSpan.TryField(line, 3, out var v3) ? v3
                : (TsvSpan.TryField(line, 2, out var v2) ? v2 : default);
        }
        else return false;

        if (valueSpan.IsEmpty) return false;
        valueUtf8 = valueSpan;

        string synStr = Encoding.UTF8.GetString(synKey);
        int dash = synStr.LastIndexOf('-');
        if (dash < 0 || dash + 1 >= synStr.Length) return false;
        if (!long.TryParse(synStr.AsSpan(0, dash), out long offset)) return false;
        char ssType = synStr[dash + 1];
        row = new OmwRow(offset, ssType, lang, rowType);
        return true;
    }

    private static void EmitRow(SubstrateChangeBuilder b, in OmwRow row, ReadOnlySpan<byte> valueUtf8)
    {
        if (!TryAppendLemmaUtf8(b, valueUtf8, OMWDecomposer.Source, out var root))
            return;

        
        
        
        Hash128? synAnchor = ConceptAnchor.EmitAnchor(b, row.Offset, row.SsType, OMWDecomposer.Source);
        if (synAnchor is null) return;
        Hash128 synId = synAnchor.Value;
        ConceptAnchor.AttestSynsetCategory(b, synId, OMWDecomposer.Source, TC.AcademicCurated);

        Hash128 langId = LanguageReference.Resolve(row.Lang);
        OMWDecomposer.TrackLanguage(row.Lang);
        b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, EntityTypeRegistry.Language, OMWDecomposer.Source));

        switch (row.Type)
        {
            case OmwType.Lemma:
                b.AddAttestation(NativeAttestation.Categorical(
                    root, "IS_TRANSLATION_OF", synId, OMWDecomposer.Source, null, TC.AcademicCurated));
                b.AddAttestation(NativeAttestation.Categorical(
                    root, "HAS_LANGUAGE", langId, OMWDecomposer.Source, null, TC.AcademicCurated));
                break;
            case OmwType.Def:
                b.AddAttestation(NativeAttestation.Categorical(
                    synId, "HAS_DEFINITION", root, OMWDecomposer.Source, langId, TC.AcademicCurated));
                break;
            case OmwType.Exe:
                b.AddAttestation(NativeAttestation.Categorical(
                    synId, "HAS_EXAMPLE", root, OMWDecomposer.Source, langId, TC.AcademicCurated));
                break;
        }
    }

    private static string FileLang(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int dash = name.LastIndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : "und";
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(OMWDecomposer.Source, unit, null,
            entityCapacity: batch * 6, physicalityCapacity: batch * 6, attestationCapacity: batch * 2);

    
    private static bool TryAppendLemmaUtf8(
        SubstrateChangeBuilder b, ReadOnlySpan<byte> src, Hash128 sourceId, out Hash128 rootId)
    {
        while (src.Length > 0 && src[0] == (byte)' ') src = src[1..];
        while (src.Length > 0 && src[^1] == (byte)' ') src = src[..^1];
        if (src.IsEmpty) { rootId = default; return false; }
        return ContentWitnessBatch.TryAppendUnderscoredToBuilder(b, src, sourceId, out rootId);
    }

    public enum OmwType { Lemma, Def, Exe }
    public readonly record struct OmwRow(long Offset, char SsType, string Lang, OmwType Type);
}
