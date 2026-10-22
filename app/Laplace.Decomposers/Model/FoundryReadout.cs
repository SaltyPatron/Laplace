using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

internal static class FoundryReadout
{
    internal sealed record Bindings(Dictionary<Hash128,List<int>> Operands,
        int[] InputOperand, int[] OperandOutput, Hash128[] OperandIds);

    internal static Bindings Bind(IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens, int vocab)
    {
        var ids=new Hash128[vocab];
        foreach(var t in tokens) {
            var bytes=t.CanonicalBytes.AsSpan();
            // This is an explicit realization operation over canonical content.
            // The token record and its exact root identifier remain untouched.
            ids[t.TokenId]=t.Role.HasFlag(TokenRole.LeadingSpace) && bytes.Length>1 && bytes[0]==32
                ? TextDecomposer.ContentRootId(bytes[1..])!.Value : t.EntityId;
        }
        var groups=tokens.GroupBy(t=>ids[t.TokenId]).ToArray();
        var operands=new Dictionary<Hash128,List<int>>();
        var input=new int[vocab]; var output=Enumerable.Repeat(-1,vocab).ToArray();
        foreach(var group in groups) {
            var basis=group.OrderBy(t=>t.Role.HasFlag(TokenRole.LeadingSpace)).ThenBy(t=>t.TokenId).First();
            operands[group.Key]=[basis.TokenId];
            foreach(var t in group) input[t.TokenId]=basis.TokenId;
            var emit=group.Where(t=>!t.IsByteLevel && !t.Role.HasFlag(TokenRole.Special))
                .OrderByDescending(t=>t.Role.HasFlag(TokenRole.LeadingSpace)).ThenBy(t=>t.TokenId).FirstOrDefault();
            if(emit is not null) output[basis.TokenId]=emit.TokenId;
        }
        return new(operands,input,output,ids);
    }

    internal sealed record Program(Bindings Bindings, FoundryExport.PlaneCoo Plane,
        IReadOnlyList<NpgsqlFoundryReads.PathEdgeRow> Evidence, RecipeDescriptor Recipe)
    {
        internal unsafe void Compile(int vocab,int dim,double epsilon,out double[] embed,out double[] head)
        {
            embed=new double[(long)vocab*dim]; head=new double[(long)vocab*dim];
            int rc;
            fixed(int* input=Bindings.InputOperand,emit=Bindings.OperandOutput,r=Plane.Rows,c=Plane.Cols)
            fixed(double* w=Plane.Vals,e=embed,h=head)
                rc=SynthInterop.LookupCompile((nuint)vocab,(nuint)dim,2,epsilon,input,emit,r,c,w,(nuint)Plane.Nnz,e,h);
            if(rc!=0) throw new InvalidOperationException($"lookup compiler rejected the selected operator (status {rc}, vocab {vocab}, width {dim})");
        }

        internal unsafe double[] Embed(int vocab,int width,IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,out int features)
            =>Embed(vocab,width,tokens,out features,out _);

        internal unsafe double[] Embed(int vocab,int width,IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,out int features,out object receipt,double outsideBound=0,bool equalCandidates=false)
        {
            var coordinates=FoundryBasis.Coordinates(tokens,vocab);
            var embedding=new double[(long)vocab*width];nuint used=0;int rc;
            fixed(int* input=Bindings.InputOperand,emit=Bindings.OperandOutput,r=Plane.Rows,c=Plane.Cols)
            fixed(double* w=Plane.Vals,coords=coordinates,e=embedding)
                rc=SynthInterop.ReadoutEmbedPolicy((nuint)vocab,(nuint)width,input,emit,r,c,w,(nuint)Plane.Nnz,coords,e,&used,outsideBound,equalCandidates?1:0);
            if(rc!=0)throw new InvalidOperationException($"selected-operator embedding failed ({rc}); width {width}, required features {used}");
            features=(int)used;receipt=FoundryBasis.Refine(embedding,vocab,width,features-2,coordinates,Bindings.InputOperand);return embedding;
        }

        internal async Task CalibrateAsync(string executable,string outputPath,int vocab,int width)
        {
            string dataPath=outputPath+".readout.bin";
            using(var stream=File.Create(dataPath)) using(var writer=new BinaryWriter(stream)) {
                writer.Write(Encoding.ASCII.GetBytes("LPREAD01"));writer.Write(vocab);writer.Write(width);writer.Write(Plane.Nnz);
                foreach(int x in Bindings.InputOperand)writer.Write(x);
                foreach(int x in Bindings.OperandOutput)writer.Write(x);
                foreach(int x in Plane.Rows)writer.Write(x);
                foreach(int x in Plane.Cols)writer.Write(x);
                foreach(double x in Plane.Vals)writer.Write(x);
            }
            var start=new System.Diagnostics.ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(outputPath);start.ArgumentList.Add(dataPath);
            using var process=System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("runtime calibrator did not start");
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            File.WriteAllText(outputPath+".calibration.json",await stdout);
            File.WriteAllText(outputPath+".calibration.log",await stderr);
            if(process.ExitCode!=0) throw new InvalidOperationException($"runtime calibrator failed ({process.ExitCode}); see calibration.log");
            using var measurement=JsonDocument.Parse(await stdout);
            long offset=measurement.RootElement.GetProperty("output_weight_offset").GetInt64();
            int count=measurement.RootElement.GetProperty("output_weight_bytes").GetInt32();
            if(count!=checked(vocab*width*sizeof(float))) throw new InvalidOperationException("calibrator returned an inconsistent output tensor size");
            byte[] finalHead=new byte[count];
            using(var artifact=File.OpenRead(outputPath)){artifact.Position=offset;artifact.ReadExactly(finalHead);}
            string receiptPath=outputPath+".tensors.json";
            var receipts=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(receiptPath))!.AsArray();
            var headReceipt=receipts.Single(item=>item!["tensor"]!.GetValue<string>()=="output.weight")!;
            headReceipt["sha256"]=Convert.ToHexString(SHA256.HashData(finalHead)).ToLowerInvariant();
            headReceipt["calculation"]="native SGELSD on complete-runtime hidden states; measurement in calibration.json";
            File.WriteAllText(receiptPath,receipts.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        }

        internal void WriteReceipt(IntPtr gguf,string outputPath,IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens)
        {
            var receipt=new {
                recipe=JsonDocument.Parse(Recipe.CanonicalJson).RootElement,
                operation="positive weighted path transfer; sum of products; explicit token realization",
                bindings=tokens.Select(t=>new {slot=t.TokenId,canonical_id=t.EntityId.ToHex(),operand_id=Bindings.OperandIds[t.TokenId].ToHex(),
                    input_operand=Bindings.InputOperand[t.TokenId],output_slot=Bindings.OperandOutput[Bindings.InputOperand[t.TokenId]]}),
                evidence=Evidence.Select(e=>new {subject=Convert.ToHexString(e.SubjectId),@object=Convert.ToHexString(e.ObjectId),weight=e.W,
                    paths=JsonDocument.Parse(e.Paths).RootElement})
            };
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(receipt,new JsonSerializerOptions {WriteIndented=true});
            File.WriteAllBytes(outputPath+".provenance.json",bytes);
            if(gguf==IntPtr.Zero) return;
            SynthInterop.GgufWriterAddMetadataStr(gguf,"laplace.export.recipe",Encoding.UTF8.GetString(Recipe.CanonicalJson));
            SynthInterop.GgufWriterAddMetadataStr(gguf,"laplace.export.provenance_sha256",Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            SynthInterop.GgufWriterAddMetadataStr(gguf,"laplace.export.realization","leading-space content operand; exact canonical token IDs retained");
        }
    }

    internal static async Task<Program> ReadAsync(NpgsqlDataSource ds,RecipeDescriptor recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,int vocab)
    {
        if(recipe.LmHead.Op!="path") throw new InvalidOperationException("lookup compile requires an explicit lm_head path operator");
        var bindings=Bind(tokens,vocab);
        var rows=await NpgsqlFoundryReads.PathPlaneAsync(ds,bindings.Operands.Keys.Select(id=>id.ToBytes()).ToArray(),
            recipe.LmHead.PathJson,recipe.Vocab.Fanout,128,true);
        var rr=new List<int>(); var cc=new List<int>(); var ww=new List<double>();
        foreach(var edge in rows) {
            if(!bindings.Operands.TryGetValue(Hash128.FromBytes(edge.SubjectId),out var x) ||
               !bindings.Operands.TryGetValue(Hash128.FromBytes(edge.ObjectId),out var y)) continue;
            rr.Add(x[0]); cc.Add(y[0]); ww.Add(edge.W);
        }
        if(rr.Count==0) throw new InvalidOperationException("selected path operator has no edges over the export vocabulary");
        Console.WriteLine($"  path readout: {rows.Count:N0} endpoint pairs; intermediate entities retained in provenance");
        return new(bindings,new(rr.ToArray(),cc.ToArray(),ww.ToArray()),rows,recipe);
    }

    internal static async Task<List<(string surface,long weight)>> SelectVocabularyAsync(
        NpgsqlDataSource ds,RecipeDescriptor recipe)
    {
        var chosen=new Dictionary<Hash128,double>();
        var seeds=recipe.Vocab.Seeds.Select(s=>TextDecomposer.ContentRootId(s) ?? throw new InvalidOperationException("empty seed")).ToHashSet();
        foreach(var id in seeds) chosen[id]=1;
        var frontier=seeds.ToArray();
        for(int hop=0;hop<recipe.Vocab.Hops && frontier.Length>0;hop++) {
            var rows=await NpgsqlFoundryReads.PathPlaneAsync(ds,frontier.Select(id=>id.ToBytes()).ToArray(),
                recipe.LmHead.PathJson,recipe.Vocab.Fanout,recipe.Vocab.Fanout,false);
            var fresh=new HashSet<Hash128>();
            foreach(var edge in rows) {
                var id=Hash128.FromBytes(edge.ObjectId);
                if(!chosen.ContainsKey(id)) fresh.Add(id);
                chosen[id]=Math.Max(chosen.GetValueOrDefault(id),edge.W);
            }
            var keep=chosen.OrderByDescending(p=>seeds.Contains(p.Key)).ThenByDescending(p=>p.Value).ThenBy(p=>p.Key.ToHex())
                .Take(recipe.Vocab.Size).ToDictionary(p=>p.Key,p=>p.Value);
            chosen=keep; frontier=fresh.Where(keep.ContainsKey).ToArray();
        }
        var all=chosen.Keys.ToArray();
        var labels=await NpgsqlSubstrateReads.RenderTextBatchAsync(ds,all.Select(id=>id.ToBytes()).ToArray(),CancellationToken.None)
            ?? throw new InvalidOperationException("path vocabulary could not be realized");
        var result=new List<(string,long)>();
        for(int i=0;i<all.Length;i++) {
            if(string.IsNullOrEmpty(labels[i]) || TextDecomposer.ContentRootId(labels[i])!=all[i])
                throw new InvalidOperationException($"path endpoint {all[i].ToHex()} needs an explicit content realization; its label is not its identity");
            result.Add((labels[i],Math.Max(1,(long)Math.Min(long.MaxValue/2,chosen[all[i]]*1_000_000))));
        }
        Console.WriteLine($"  path vocabulary: {result.Count:N0} canonical endpoints from {seeds.Count} seeds, {recipe.Vocab.Hops} rounds");
        return result;
    }
}
