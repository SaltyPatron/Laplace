using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Api.Contracts;
public static class InspectionApiAcceptance {
 public static async Task Main() {
  Environment.SetEnvironmentVariable("LAPLACE_DB","Host=127.0.0.1;Port=55438;Database=laplace_inspection_20261021;Username=readpath_validation;SSL Mode=Disable;Pooling=false;Command Timeout=15");
  var t=typeof(ExploreResolveResponse).Assembly.GetType("Laplace.Endpoints.OpenAICompat.SubstrateClient",true);
  await using var client=(IAsyncDisposable)Activator.CreateInstance(t,true);
  foreach(var name in new[]{"ExploreResolveAsync","ExploreEntityPreviewAsync","ExploreEntityAsync"}) {
   object[] args=name=="ExploreResolveAsync"?new object[]{"Laplace",CancellationToken.None}:name=="ExploreEntityPreviewAsync"?new object[]{"3e7f0ee67a293f2da9fc17a259a275fe",CancellationToken.None}:new object[]{"3e7f0ee67a293f2da9fc17a259a275fe",5,5,CancellationToken.None};
   var task=(Task)t.GetMethod(name).Invoke(client,args);await task;
   var result=task.GetType().GetProperty("Result").GetValue(task);
   var json=JsonSerializer.Serialize(result);using var doc=JsonDocument.Parse(json);
   if(!doc.RootElement.GetProperty("exists").GetBoolean()||doc.RootElement.GetProperty("label").GetString()!="Laplace")throw new Exception(name+" lost the known entity");
   Console.WriteLine(name+": "+json);
  }
 }
}
