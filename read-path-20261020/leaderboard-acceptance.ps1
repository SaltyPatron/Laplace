$ErrorActionPreference='Stop'
. E:/Repositories/Laplace/Laplace-Operations/laplace.env.ps1
$runtime='E:/Repositories/Laplace/.build/.tmp/warning-cleanup-20261020/app/bin/Laplace.Endpoints.OpenAICompat/Release/net10.0'
$source=@'
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

public static class LeaderboardAcceptance {
    public static async Task Main() { Console.WriteLine(await Run()); }
    public static async Task<string> Run() {
        const string connection="Host=127.0.0.1;Port=55438;Database=postgres;Username=readpath_validation;SSL Mode=Disable;Pooling=false;Maximum Pool Size=4;Command Timeout=20";
        var handle=NativeLibrary.Load("E:/Repositories/Laplace/.build/.tmp/read-path-20261020/laplace_core.dll");
        NativeLibrary.SetDllImportResolver(typeof(SqlCatalog).Assembly,
            (name,assembly,path)=>name=="laplace_core"?handle:IntPtr.Zero);
        var sql=SqlCatalog.Get("leaders.named").Text;
        if(!sql.Contains("LEFT JOIN leaders"))throw new Exception("Native catalog is stale");
        await using var source=NpgsqlDataSource.Create(connection);
        const string setup=@"
INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(laplace.word_id('b'),r.id,o.id),laplace.word_id('b'),r.id,o.id,
1700000000000,30000000000,60000000,1,now()
FROM (VALUES (laplace.relation_type_id('IS_A')),(laplace.relation_type_id('IS_EXAMPLE_OF'))) r(id)
CROSS JOIN (VALUES (laplace.word_id('a')),(laplace.word_id('b'))) o(id);
INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
VALUES (laplace.consensus_id(laplace.word_id('b'),laplace.relation_type_id('HAS_PART'),laplace.word_id('a')),
laplace.word_id('b'),laplace.relation_type_id('HAS_PART'),laplace.word_id('a'),0,1,60000000,1,now());";
        const string cleanup=@"DELETE FROM laplace.consensus WHERE subject_id=laplace.word_id('b') AND type_id IN
(laplace.relation_type_id('IS_A'),laplace.relation_type_id('IS_EXAMPLE_OF'),laplace.relation_type_id('HAS_PART'));";
        // This is the private validation cluster. No serving rows are written.
        await using(var command=source.CreateCommand(setup))await command.ExecuteNonQueryAsync();
        var saved=Environment.GetEnvironmentVariable("LAPLACE_DB");
        IAsyncDisposable client=null;
        try {
            var named=await NpgsqlSubstrateReads.BandLeadersNamedAsync(source,new[]{1,2,4,5},4,CancellationToken.None);
            if(named.Count!=7 || named.Count(r=>r.Leader.HasValue)!=4 || named.Count(r=>!r.Leader.HasValue)!=3)
                throw new Exception("Compiled named read lost empty bands or admitted refuted edges");
            await using(var command=source.CreateCommand("SELECT consensus.relation_canonical(type_id),encode(object_id,'hex') FROM consensus.band_edges(2,NULL,4)")) {
                await using var reader=await command.ExecuteReaderAsync();
                var actual=named.Where(r=>r.Leader.HasValue).Select(r=>r.Leader.Value).ToArray();
                var ordinal=0;
                while(await reader.ReadAsync()) {
                    if(actual[ordinal].Relation!=reader.GetString(0) || actual[ordinal].ObjectIdHex!=reader.GetString(1))
                        throw new Exception("Named read reordered tied relations");
                    ordinal++;
                }
                if(ordinal!=4)throw new Exception("Fixture did not exercise four tied edges");
            }
            Environment.SetEnvironmentVariable("LAPLACE_DB",connection);
            var type=typeof(LeadersResponse).Assembly.GetType("Laplace.Endpoints.OpenAICompat.SubstrateClient",true);
            client=(IAsyncDisposable)Activator.CreateInstance(type,true);
            var task=(Task)type.GetMethod("LeadersAsync").Invoke(client,new object[]{new[]{1,2,4,5},4,CancellationToken.None});
            await task;
            var result=(System.Collections.Generic.IReadOnlyList<BandLeaders>)task.GetType().GetProperty("Result").GetValue(task);
            if(!result.Select(b=>b.Band).SequenceEqual(new[]{1,2,4,5}) || result.Single(b=>b.Band==2).Rows.Count!=4 ||
                result.Where(b=>b.Band!=2).Any(b=>b.Rows.Count!=0))throw new Exception("API projection lost empty arenas");
            if(result.Single(b=>b.Band==2).Rows.Any(r=>r.Subject!="b" || (r.Object!="a"&&r.Object!="b")))
                throw new Exception("Unicode labels differ from selected cache identities");
            var empty=await NpgsqlSubstrateReads.BandLeadersNamedAsync(source,Array.Empty<int>(),4,CancellationToken.None);
            if(empty.Count!=0)throw new Exception("Empty request expanded into the whole catalog");
            return JsonSerializer.Serialize(new LeadersResponse("list",result));
        } finally {
            if(client!=null)await client.DisposeAsync();
            Environment.SetEnvironmentVariable("LAPLACE_DB",saved);
            await using var command=source.CreateCommand(cleanup);
            await command.ExecuteNonQueryAsync();
        }
    }
}
'@
$program=Join-Path $PSScriptRoot 'leaderboard-acceptance.cs'
Set-Content -LiteralPath $program -Value $source -Encoding utf8
$sdk=Get-ChildItem 'C:/Program Files/dotnet/sdk' -Directory | Where-Object Name -Like '10.*' | Sort-Object Name -Descending | Select-Object -First 1
$pack=Get-ChildItem 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref' -Directory | Where-Object Name -Like '10.*' | Sort-Object Name -Descending | Select-Object -First 1
$assembly=Join-Path $runtime 'LeaderboardAcceptance.dll'
$compilerArguments=@('-nologo','-noconfig','-nostdlib+','-target:exe',('-out:'+$assembly),$program)
$compilerArguments+=Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0') -Filter *.dll | ForEach-Object { '-r:'+$_.FullName }
$compilerArguments+=@('Laplace.Core','Laplace.Substrate','Laplace.Endpoints.OpenAICompat','Npgsql') | ForEach-Object { '-r:'+(Join-Path $runtime ($_+'.dll')) }
& dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') @compilerArguments
if($LASTEXITCODE){exit $LASTEXITCODE}
& dotnet exec --depsfile "$runtime/Laplace.Endpoints.OpenAICompat.deps.json" --runtimeconfig "$runtime/Laplace.Endpoints.OpenAICompat.runtimeconfig.json" $assembly
exit $LASTEXITCODE
