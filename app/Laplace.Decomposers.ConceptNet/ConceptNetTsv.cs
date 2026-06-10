using System.Text;

namespace Laplace.Decomposers.ConceptNet;

internal ref struct ConceptNetTsvRow
{
    public ReadOnlySpan<byte> Relation { get; init; }
    public ReadOnlySpan<byte> StartUri { get; init; }
    public ReadOnlySpan<byte> EndUri { get; init; }
    public ReadOnlySpan<byte> MetaJson { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> line, out ConceptNetTsvRow row)
    {
        row = default;
        if (!TryField(line, 1, out var rel)) return false;
        if (!TryField(line, 2, out var start)) return false;
        if (!TryField(line, 3, out var end)) return false;
        if (!TryField(line, 4, out var meta)) return false;
        row = new ConceptNetTsvRow { Relation = rel, StartUri = start, EndUri = end, MetaJson = meta };
        return true;
    }

    private static bool TryField(ReadOnlySpan<byte> line, int fieldIndex, out ReadOnlySpan<byte> field)
    {
        field = default;
        int tab = 0;
        int start = 0;
        for (int i = 0; i <= line.Length; i++)
        {
            if (i == line.Length || line[i] == (byte)'\t')
            {
                if (tab == fieldIndex)
                {
                    field = line[start..i];
                    return true;
                }
                tab++;
                start = i + 1;
            }
        }
        return false;
    }

    public string RelationText() => Encoding.UTF8.GetString(Relation);
    public string StartUriText() => Encoding.UTF8.GetString(StartUri);
    public string EndUriText() => Encoding.UTF8.GetString(EndUri);
    public string MetaText() => Encoding.UTF8.GetString(MetaJson);
}
