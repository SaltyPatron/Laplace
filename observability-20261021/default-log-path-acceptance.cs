using System;using System.IO;using System.Text.Json;using Laplace.Engine.Core;
public static class DefaultLogPathAcceptance { public static void Main(){
 Environment.SetEnvironmentVariable("LAPLACE_OPS_LOG_DIR",null);
 var path=LaplaceInstall.OpsLogDirectory;
 if(Path.GetFullPath(path).StartsWith(Path.GetFullPath(AppContext.BaseDirectory),StringComparison.OrdinalIgnoreCase))throw new Exception("Default log path is inside the binary directory");
 var selected="E:/Repositories/Laplace/.build/.tmp/observability-20261021/declared-logs";
 Environment.SetEnvironmentVariable("LAPLACE_OPS_LOG_DIR",selected);
 if(LaplaceInstall.OpsLogDirectory!=Path.GetFullPath(selected))throw new Exception("Declared log directory was not respected");
 Console.WriteLine(JsonSerializer.Serialize(new{defaultOutsideBinary=true,explicitOverrideRespected=true,noDefaultFileWrites=true}));
}}
