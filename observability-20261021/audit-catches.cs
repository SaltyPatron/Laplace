using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
public static class FailureCatchAudit {
 public static int Main(string[] args) { try { Console.Write(System.Text.Json.JsonSerializer.Serialize(Scan(File.ReadAllLines(args[0])))); return 0; } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; } }
 public static object Scan(string[] paths) {
  var rows=new List<object>();var diagnostics=new List<object>();
  foreach(var path in paths) {
   var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(path),new CSharpParseOptions(LanguageVersion.Preview));
   var errors=tree.GetDiagnostics().Count(x=>x.Severity==DiagnosticSeverity.Error);
   if(errors>0)diagnostics.Add(new{path,errors});
   foreach(var c in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>()) {
    var body=c.Block.ToString();
    var logged=Regex.IsMatch(body,@"\b(Log(?:Trace|Debug|Information|Warning|Error|Critical|Fatal)|(?:Log|log|logger)\.(?:Error|Warning|Fatal|Debug|Information)|Console\.(?:Error|Write)|reportFailure|SelfLog\.WriteLine)\b");
    var propagated=c.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Any();
    rows.Add(new{path,line=tree.GetLineSpan(c.Span).StartLinePosition.Line+1,
     exception=c.Declaration==null?"any":c.Declaration.ToString(),filter=c.Filter==null?null:c.Filter.ToString(),
     logged,propagated,returns=c.Block.DescendantNodes().OfType<ReturnStatementSyntax>().Any(),empty=c.Block.Statements.Count==0,
     disposition=logged?"logging_present_review_context":propagated?"propagates_review_boundary":"review_required",body});
   }
  }
  return new{schema="laplace.failure-catch-audit/v1",scope="Tracked C# catches; caller propagation, validation and cancellation require review.",files=paths.Length,parserDiagnostics=diagnostics,catches=rows};
 }
}

