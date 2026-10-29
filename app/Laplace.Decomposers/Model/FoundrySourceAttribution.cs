using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Decomposers.Model;

internal static class FoundrySourceAttribution
{
    public static async Task<object> ReadAsync(NpgsqlDataSource data,
        IReadOnlyDictionary<string,FoundryReadout.Program> paths,byte[][]? sources)
    {
        var touched=new SortedSet<string>(StringComparer.Ordinal);
        foreach(var program in paths.Values)
        foreach(var evidence in program.Evidence)
        {
            using var doc=JsonAstDocument.Parse(evidence.Paths);
            if(!doc.Root.IsArray)throw new InvalidDataException("Foundry evidence paths must be a JSON array");
            foreach(var path in doc.Root.Items())
            {
                var claims=path.Property("claims");
                if(!claims.IsArray)throw new InvalidDataException("Foundry evidence path has no claims array");
                foreach(var cell in claims.Items())
                {
                    string id=cell.String("claim_id")??throw new InvalidDataException("Foundry evidence has no canonical claim_id");
                    if(id.StartsWith("\\x",StringComparison.Ordinal))id=id[2..];
                    byte[] bytes=Convert.FromHexString(id);
                    if(bytes.Length!=16)throw new InvalidDataException("Foundry claim_id must contain 16 bytes");
                    touched.Add(Convert.ToHexStringLower(bytes));
                }
            }
        }
        if(touched.Count==0)return new{covered_operators=Array.Empty<string>(),claims_touched=0,sources=Array.Empty<object>()};
        var rows=await NpgsqlFoundryReads.ExportSourceAttributionAsync(data,
            touched.Select(Convert.FromHexString).ToArray(),sources);
        if(rows.Count==0||rows.Sum(r=>(long)r.ShareBp)!=10000)
            throw new InvalidOperationException("Selected path claims have no informative witnessed attribution");
        if(rows.Any(r=>r.SourceId.Length!=16||r.ClaimsTouched<=0||r.Games<=0||
            !double.IsFinite(r.FisherWeight)||r.FisherWeight<0||r.ShareBp<0||r.ShareBp>10000))
            throw new InvalidDataException("Database returned invalid source attribution");
        return new{
            covered_operators=paths.Keys.Order(StringComparer.Ordinal).ToArray(),claims_touched=touched.Count,
            method="Native rooted-series posterior information; shared dependence roots; largest remainder to 10000 basis points",
            sources=rows.Select(r=>new{source_id=Convert.ToHexStringLower(r.SourceId),
                claims_touched=r.ClaimsTouched,games=r.Games,fisher_weight=r.FisherWeight,share_bp=r.ShareBp}).ToArray()
        };
    }
}
