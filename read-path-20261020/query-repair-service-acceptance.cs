using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;
using Npgsql;

public static class QueryReadAcceptance {
 public static async Task Main() {
  Environment.SetEnvironmentVariable("LAPLACE_DB","Host=127.0.0.1;Port=55438;Database=postgres;Username=readpath_validation;SSL Mode=Disable;Pooling=true;Maximum Pool Size=1;Timeout=3;Command Timeout=15;Options=-c plan_cache_mode=force_custom_plan");
  var handle=NativeLibrary.Load("E:/Repositories/Laplace/.build/.tmp/read-path-20261020/laplace_core.dll");
  NativeLibrary.SetDllImportResolver(typeof(SqlCatalog).Assembly,(name,assembly,path)=>name=="laplace_core"?handle:IntPtr.Zero);
  var type=typeof(ExploreEntityResponse).Assembly.GetType("Laplace.Endpoints.OpenAICompat.SubstrateClient",true);
  await using var client=(IAsyncDisposable)Activator.CreateInstance(type,true);
  var readSource=(NpgsqlDataSource)type.GetField("_dataSourceReadOnly",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(client);
  await using(var connection=await readSource.OpenConnectionAsync()) {
   await using var command=connection.CreateCommand();
   command.CommandText="SELECT current_setting('plan_cache_mode'),current_setting('default_transaction_read_only')";
   await using var reader=await command.ExecuteReaderAsync();await reader.ReadAsync();
   if(reader.GetString(0)!="force_custom_plan"||reader.GetString(1)!="on")throw new Exception("Read-only pool discarded configured startup options or lost read-only enforcement");
   Console.WriteLine("Configured force_custom_plan preserved; default_transaction_read_only=on");
  }
  using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(15));
  foreach(var id in new[]{"b7b840fb843e7b1098cd32a101a2978d","be7353b51f2d16f48fcdbe7d88b48e34"}) {
   var timer=Stopwatch.StartNew();
   var task=(Task)type.GetMethod("ExploreEntityAsync").Invoke(client,new object[]{id,5,5,cancellation.Token});
   await task;
   var result=(ExploreEntityResponse)task.GetType().GetProperty("Result").GetValue(task);
   if(result==null||!result.Exists||result.Tier!=4||!result.Label.StartsWith("Chess AnalysisMarker"))throw new Exception("Entity projection lost stored facets or its label");
   Console.WriteLine(JsonSerializer.Serialize(new {id,result.Label,result.Exists,result.EvidenceCount,rows=result.ConsensusOut.Count,pool=1,milliseconds=timer.ElapsedMilliseconds}));
  }
  var catalogMethod=type.GetMethod("ExploreCatalogAsync");
  var firstCatalog=(Task)catalogMethod.Invoke(client,new object[]{CancellationToken.None});await firstCatalog;
  type.GetField("_catalogCachedAt",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(client,DateTimeOffset.UtcNow.AddMinutes(-6));
  var writeSource=(NpgsqlDataSource)type.GetField("_dataSource",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(client);
  bool propagated=false;
  await using(var occupied=await writeSource.OpenConnectionAsync()) {
   try { var reload=(Task)catalogMethod.Invoke(client,new object[]{CancellationToken.None});await reload; }
   catch(Exception ex) when(ex.GetType().Name=="SubstrateUnavailableException") { propagated=true; }
  }
  if(!propagated)throw new Exception("Expired catalog silently returned stale success while its database pool was unavailable");
  var recovered=(Task)catalogMethod.Invoke(client,new object[]{CancellationToken.None});await recovered;
  Console.WriteLine("Expired catalog connection failure reached the caller; subsequent database recovery succeeded.");
 }
}
