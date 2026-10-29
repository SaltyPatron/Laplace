using System.Text.Json;

namespace Laplace.Decomposers.Abstractions;

/// <summary>IDs compiled by the Engine from the installed firmware and producer recipes.</summary>
public sealed record NativeQueryRelation(string Declaration, byte[] Entity);

public sealed record NativeQueryProgram(IReadOnlyList<IReadOnlyList<NativeQueryRelation>> Paths,
    IReadOnlyList<NativeQueryRelation> Up)
{
    public static NativeQueryProgram Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var paths = new List<IReadOnlyList<NativeQueryRelation>>();
        foreach (var path in document.RootElement.GetProperty("chains").EnumerateArray())
        {
            var relations = new List<NativeQueryRelation>();
            foreach (var binding in path.EnumerateArray())
            {
                string name = binding.GetProperty("name").GetString() ?? throw new InvalidDataException("Missing query declaration.");
                byte[] id = Convert.FromHexString(binding.GetProperty("entity").GetString() ?? "");
                if (id.Length != 16) throw new InvalidDataException("A compiled query ID must contain 16 bytes.");
                relations.Add(new(name, id));
            }
            if (relations.Count is < 1 or > 8) throw new InvalidDataException("Invalid compiled query path length.");
            paths.Add(relations);
        }
        if (paths.Count is < 1 or > 4) throw new InvalidDataException("Invalid compiled query path count.");
        var up = document.RootElement.GetProperty("up").EnumerateArray().Select(binding =>
        {
            var id = Convert.FromHexString(binding.GetProperty("entity").GetString() ?? "");
            if (id.Length != 16) throw new InvalidDataException("A compiled query ID must contain 16 bytes.");
            return new NativeQueryRelation(binding.GetProperty("name").GetString()
                ?? throw new InvalidDataException("Missing query declaration."), id);
        }).ToArray();
        if (up.Length is < 1 or > 8) throw new InvalidDataException("Invalid compiled binding path.");
        return new(paths, up);
    }
}
