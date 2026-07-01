using System.Diagnostics;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

var cs = "Host=localhost;Username=postgres;Password=postgres;Database=laplace";
await using var ds = new NpgsqlDataSourceBuilder(cs).Build();
var writer = new NpgsqlSubstrateWriter(ds, applyPartitions: 1);
var src = Hash128.OfCanonical("substrate/source/test/bench/v1");
var typeId = Hash128.OfCanonical("ThroughputFixture");
const int n = 500_000;
var b = new SubstrateChangeBuilder(src, "one-shot", null, n, 0, 0);
for (int i = 0; i < n; i++) b.AddEntity(Hash128.Blake3(BitConverter.GetBytes(i + 90_000_000)), 0, typeId);
var sw = Stopwatch.StartNew();
var r = await writer.ApplyAsync(b.Build());
sw.Stop();
Console.WriteLine($"inserted={r.EntitiesInserted} rt={r.RoundTrips} sec={sw.Elapsed.TotalSeconds:F2} rps={r.EntitiesInserted/sw.Elapsed.TotalSeconds:F0}");
