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
                if (!TryParseRow(line.Span, fileLang, out var row)) continue;
                EmitRow(b, row);
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

    public static bool TryParseRow(ReadOnlySpan<byte> line, string fileLang, out OmwRow row)
    {
        row = default;
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
        string value = Encoding.UTF8.GetString(valueSpan).Replace('_', ' ').Trim();
        if (value.Length == 0) return false;

        string synStr = Encoding.UTF8.GetString(synKey);
        int dash = synStr.LastIndexOf('-');
        if (dash < 0 || dash + 1 >= synStr.Length) return false;
        if (!long.TryParse(synStr.AsSpan(0, dash), out long offset)) return false;
        char ssType = synStr[dash + 1];   // RAW — satellite 's' must not fold to 'a' (drops 10,693 synsets)
        row = new OmwRow(offset, ssType, lang, rowType, value);
        return true;
    }

    private static void EmitRow(SubstrateChangeBuilder b, OmwRow row)
    {
        var contentId = ContentWitnessBatch.TryAppendToBuilder(
            b, Encoding.UTF8.GetBytes(row.Value), OMWDecomposer.Source, out var root)
            ? root : (Hash128?)null;
        if (contentId is null) return;

        // The synset is the shared ILI anchor — the SAME content id WordNet emits for this offset.
        // This is the convergence point: every language's wordform attests onto it, no wordform
        // dedup. Emit it (idempotent) so OMW is self-contained; WordNet (layer 2) carries the IS_A.
        Hash128? synAnchor = ConceptAnchor.EmitAnchor(b, row.Offset, row.SsType, OMWDecomposer.Source);
        if (synAnchor is null) return;   // unmapped in CILI — skip (CILI covers PWN-3.0)
        Hash128 synId = synAnchor.Value;

        Hash128 langId = LanguageReference.Resolve(row.Lang);
        b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, EntityTypeRegistry.Language, OMWDecomposer.Source));

        switch (row.Type)
        {
            case OmwType.Lemma:
                b.AddAttestation(NativeAttestation.Categorical(
                    contentId.Value, "IS_TRANSLATION_OF", synId, OMWDecomposer.Source, null, TC.AcademicCurated));
                b.AddAttestation(NativeAttestation.Categorical(
                    contentId.Value, "HAS_LANGUAGE", langId, OMWDecomposer.Source, null, TC.AcademicCurated));
                break;
            case OmwType.Def:
                b.AddAttestation(NativeAttestation.Categorical(
                    synId, "HAS_DEFINITION", contentId.Value, OMWDecomposer.Source, langId, TC.AcademicCurated));
                break;
            case OmwType.Exe:
                b.AddAttestation(NativeAttestation.Categorical(
                    synId, "HAS_EXAMPLE", contentId.Value, OMWDecomposer.Source, langId, TC.AcademicCurated));
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

    public enum OmwType { Lemma, Def, Exe }
    public readonly record struct OmwRow(long Offset, char SsType, string Lang, OmwType Type, string Value);
}
