namespace Laplace.Decomposers.Abstractions;

public static class TsvSpan
{
    public static bool TryField(ReadOnlySpan<byte> line, int fieldIndex, out ReadOnlySpan<byte> field)
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
}
