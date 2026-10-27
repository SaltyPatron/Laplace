using System.Text.Json;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Native=Laplace.Engine.Synthesis.NativeInterop;
namespace Laplace.Decomposers.Model;
internal static class FoundrySourceAttribution
{
    public static async Task<object> ReadAsync(NpgsqlDataSource data,IReadOnlyDictionary<string,FoundryReadout.Program> paths,byte[][]? sources)
    {
        var touched=new HashSet<(string s,string t,string o)>();
        foreach(var program in paths.Values)foreach(var evidence in program.Evidence){
            using var doc=JsonDocument.Parse(evidence.Paths);
            foreach(var path in doc.RootElement.EnumerateArray())foreach(var cell in path.GetProperty("claims").EnumerateArray())
                touched.Add((cell.GetProperty("subject_id").GetString()![2..],cell.GetProperty("type_id").GetString()![2..],cell.GetProperty("object_id").GetString()![2..]));
        }
        if(touched.Count==0)return new{covered_operators=Array.Empty<string>(),claims_touched=0,sources=Array.Empty<object>()};
        string json=JsonSerializer.Serialize(touched.OrderBy(c=>c.s,StringComparer.Ordinal).ThenBy(c=>c.t,StringComparer.Ordinal).ThenBy(c=>c.o,StringComparer.Ordinal).Select(c=>new{c.s,c.t,c.o}));
        var observations=await NpgsqlFoundryReads.ExportSourceObservationsAsync(data,json,sources);
        return Compute(paths.Keys.ToArray(),touched.Count,observations);
    }
    private static unsafe object Compute(string[] keys,int claims,IReadOnlyList<NpgsqlFoundryReads.SourceObservationRow> rows)
    {
        if(rows.Count==0)throw new InvalidOperationException("selected path cells have no attributable witness observations");
        var input=new Native.SourceObservation[rows.Count];var output=new Native.SourceShare[rows.Count];
        fixed(Native.SourceObservation* p=input)for(int x=0;x<rows.Count;x++){
            var r=rows[x];r.SourceId.CopyTo(new Span<byte>(p[x].SourceId,16));r.ClaimId.CopyTo(new Span<byte>(p[x].ClaimId,16));
            p[x].Games=r.Games;p[x].Rating=r.Rating;p[x].Rd=r.Rd;p[x].OpponentRating=r.OpponentRating;p[x].OpponentRd=r.OpponentRd;
        }
        nuint count=0;int rc;fixed(Native.SourceObservation* i=input)fixed(Native.SourceShare* o=output)rc=Native.SourceAttribution(i,(nuint)input.Length,o,(nuint)output.Length,&count);
        if(rc!=0)throw new InvalidOperationException($"native witness attribution failed ({rc})");
        var shares=new List<object>();fixed(Native.SourceShare* o=output)for(int x=0;x<(int)count;x++)shares.Add(new{
            source_id=Convert.ToHexStringLower(new ReadOnlySpan<byte>(o[x].SourceId,16)),claims_touched=o[x].ClaimsTouched,games=o[x].Games,fisher_weight=o[x].FisherWeight,share_bp=o[x].ShareBp});
        return new{covered_operators=keys,claims_touched=claims,method="games * g(opponent_phi)^2 * E * (1-E); native fixed-point Glicko-2; largest remainder to 10000 basis points",sources=shares};
    }
}
