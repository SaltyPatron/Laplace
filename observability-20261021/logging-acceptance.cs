using System;using System.IO;using Microsoft.Extensions.Logging;using Laplace.Ops;
public static class LoggingAcceptance {
 public static void Main(){
  Environment.SetEnvironmentVariable("LAPLACE_OPS_LOG_DIR","E:/Repositories/Laplace/.build/.tmp/observability-20261021/sink-logs");
  using(var factory=LaplaceLogging.ConsoleAndFile("acceptance")){
   using var scope=factory.CreateLogger("Laplace.Acceptance.Nested.Category").BeginScope(new System.Collections.Generic.Dictionary<string,object>{{"CorrelationId","actual-sink-check"}});
   try{try{throw new IOException("inner storage failure");}catch(Exception inner){throw new InvalidOperationException("outer operation failure",inner);}}
   catch(Exception ex){factory.CreateLogger("Laplace.Acceptance.Nested.Category").LogError(new EventId(991,"Acceptance"),ex,"Failure during {Operation}.","sink.acceptance");}
  }
  foreach(var file in Directory.GetFiles("E:/Repositories/Laplace/.build/.tmp/observability-20261021/sink-logs"))Console.WriteLine(Path.GetFileName(file)+" "+new FileInfo(file).Length);
 }
}
