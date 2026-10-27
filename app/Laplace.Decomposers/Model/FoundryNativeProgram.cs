using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Laplace.SubstrateCRUD.Npgsql;
using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using Native=Laplace.Engine.Synthesis.NativeInterop;
namespace Laplace.Decomposers.Model;
internal static partial class FoundryNativeProgramHost
{
    public static unsafe Task<int> WriteAsync(RecipeDescriptor desc,string outputPath,string tokenizerDir,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,int vocab,int width,int intermediate,double[] embedding,
        IReadOnlyDictionary<string,FoundryExport.PlaneCoo> planes,IReadOnlyDictionary<string,FoundryReadout.Program> paths,
        string? sourceScope,Action<IntPtr,LlamaRecipeExtractor.RecipeInfo,IReadOnlyList<LlamaTokenizerParser.TokenRecord>,string,bool> metadata,IReadOnlyList<(NpgsqlFoundryReads.OrderedSequenceRow Evidence,int[] Slots)>? sequences=null,object? attribution=null,object? basis=null)
    {
        if(desc.ModelType is not ("llama" or "mixtral"))throw new InvalidOperationException($"no architecture codec is registered for {desc.ModelType}");
        if(desc.Norm!="rmsnorm")throw new InvalidOperationException("the selected Llama/Mixtral codec requires rmsnorm; a different normalization must select its matching architecture codec");
        int heads=desc.Layers[0].Heads.Count,kv=desc.Layers[0].KvHeads,experts=desc.Layers[0].Experts.Count;
        if(desc.Layers.Any(l=>l.Experts.Count!=experts||l.IntermediateSize>0&&l.IntermediateSize!=intermediate))
            throw new InvalidOperationException("the selected Llama/Mixtral codec requires uniform expert and FFN shapes");
        var config=new JsonObject{
            ["architectures"]=new JsonArray(experts>0?"MixtralForCausalLM":"LlamaForCausalLM"),
            ["model_type"]=experts>0?"mixtral":"llama",["hidden_size"]=width,["num_hidden_layers"]=desc.NumLayers,
            ["num_attention_heads"]=heads,["num_key_value_heads"]=kv,["intermediate_size"]=intermediate,
            ["vocab_size"]=vocab,["hidden_act"]="silu",["rms_norm_eps"]=1e-5,["rope_theta"]=desc.RopeTheta,
            ["torch_dtype"]="float32",["tie_word_embeddings"]=desc.TieEmbeddings,["bos_token_id"]=1,["eos_token_id"]=2,
            ["max_position_embeddings"]=desc.ContextLength,
            ["laplace"]=new JsonObject{["recipe_id"]=desc.RecipeId.ToHex(),["source_scope"]=sourceScope,
                ["recipe"]="recipe.json",["canonical_bindings"]="provenance.json",["tensor_receipts"]="tensors.json",
                ["calculations"]="calculations.json",["operators"]="operators.json",["basis"]=basis is null?null:"basis.json",
                ["source_attribution"]=attribution is null?null:"source-attribution.json"},
            ["rope_scaling"]=desc.Rope?null:new JsonObject{["rope_type"]="linear",["factor"]=1e30}
        };
        if(experts>0){config["num_local_experts"]=experts;config["num_experts_per_tok"]=desc.Layers[0].ExpertsPerToken;}
        byte[] cfg=Encoding.UTF8.GetBytes(config.ToJsonString());var architecture=LlamaRecipeExtractor.ParseBytes(cfg,"native construction architecture");
        string format=desc.Format.ToLowerInvariant();if(format is not ("gguf" or "safetensors"))throw new InvalidOperationException($"no output codec registered for {format}");
        IntPtr gguf=IntPtr.Zero,safe=IntPtr.Zero;
        if(format=="gguf")gguf=Native.GgufWriterCreate(outputPath);
        else {Directory.CreateDirectory(outputPath);safe=Native.FormatWriterCreate("safetensors",outputPath);}
        if(gguf==IntPtr.Zero&&safe==IntPtr.Zero)throw new IOException("native output codec could not open the destination");
        try {
            if(gguf!=IntPtr.Zero){metadata(gguf,architecture,tokens,tokenizerDir,true);
                Native.GgufWriterAddMetadataF32(gguf,"llama.rope.scale_linear",desc.Rope?1:1e30f);
                if(experts>0){Native.GgufWriterAddMetadataU32(gguf,"llama.expert_count",(uint)experts);Native.GgufWriterAddMetadataU32(gguf,"llama.expert_used_count",(uint)desc.Layers[0].ExpertsPerToken);}
                Native.GgufWriterAddMetadataStr(gguf,"laplace.export.source_scope",sourceScope??"unfiltered consensus");
                Native.GgufWriterAddMetadataStr(gguf,"laplace.export.program","native operator compilation and emitted attention/FFN execution");
            }else {
                fixed(byte* c=cfg)if(Native.FormatWriterSetConfig(safe,c,(nuint)cfg.Length)!=0)throw new IOException("SafeTensors config rejected");
                byte[] tok=File.ReadAllBytes(Path.Combine(tokenizerDir,"tokenizer.json"));
                fixed(byte* t=tok)if(Native.FormatWriterSetTokenizer(safe,t,(nuint)tok.Length)!=0)throw new IOException("SafeTensors tokenizer rejected");
            }
            var binding=FoundryReadout.Bind(tokens,vocab);
            using var program=new NativeSynthesisProgram(vocab,width,embedding,desc.NumLayers,desc.Rope,desc.RopeTheta,FoundryDefaults.AttnGain,FoundryDefaults.ResidGain);
            program.TieEmbeddings(desc.TieEmbeddings);
            program.OutputFloor(desc.LmHead.MaxOutsideProbability,desc.LmHead.EqualCandidates);
            program.ObservationReadout(desc.LmHead.Op is "trajectory" or "sentence_order" or "conditional" or "conditional_pos");
            if(sequences is not null)foreach(var sequence in sequences)program.AddSequence(sequence.Slots);
            var ids=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach(var (key,plane) in planes){if(key=="context")continue;
                var actual=paths.TryGetValue(key,out var path)?path.Plane:plane;
                if(actual.Nnz==0)throw new InvalidOperationException($"declared operator {key} has no evidence under this source scope");
                ids[key]=program.AddPlane(actual.Rows,actual.Cols,actual.Vals);
            }
            WriteOperators(outputPath+".operators.json",tokens,binding,planes,paths);
            if(gguf!=IntPtr.Zero){using var receipt=File.OpenRead(outputPath+".operators.json");Native.GgufWriterAddMetadataStr(gguf,"laplace.export.operators.sha256",Convert.ToHexStringLower(SHA256.HashData(receipt)));}
            int Resolve(string key)=>key=="context"?-1:ids.TryGetValue(key,out var id)?id:throw new InvalidOperationException($"unbound operator {key}");
            int ResolveBody(string key)=>desc.ContinuationCompile&&!FoundryExport.IsContinuationOperator(key)?-2:Resolve(key);
            foreach(var layer in desc.Layers)program.AddLayer(layer.Heads.Count,layer.KvHeads,intermediate,
                layer.Heads.Select(h=>ResolveBody(h.Key)).ToArray(),ResolveBody(layer.Ffn.Key),layer.Experts.Select(e=>ResolveBody(e.Key)).ToArray(),layer.ExpertsPerToken);
            if(paths.TryGetValue(desc.LmHead.Key,out var readout))readout.WriteReceipt(gguf,outputPath,tokens);
            else if(gguf!=IntPtr.Zero)Native.GgufWriterAddMetadataStr(gguf,"laplace.export.recipe",Encoding.UTF8.GetString(desc.CanonicalJson));
            if(attribution is not null){
                byte[] receipt=JsonSerializer.SerializeToUtf8Bytes(attribution,new JsonSerializerOptions{WriteIndented=true});
                File.WriteAllBytes(outputPath+".source-attribution.json",receipt);
                if(gguf!=IntPtr.Zero)Native.GgufWriterAddMetadataStr(gguf,"laplace.export.source_attribution.sha256",Convert.ToHexStringLower(SHA256.HashData(receipt)));
            }
            if(basis is not null){
                byte[] receipt=JsonSerializer.SerializeToUtf8Bytes(basis,new JsonSerializerOptions{WriteIndented=true});
                File.WriteAllBytes(outputPath+".basis.json",receipt);
                if(gguf!=IntPtr.Zero)Native.GgufWriterAddMetadataStr(gguf,"laplace.export.basis.sha256",Convert.ToHexStringLower(SHA256.HashData(receipt)));
            }
            program.Compile(Resolve(desc.LmHead.Key),binding.InputOperand,binding.OperandOutput,gguf,safe);
            program.WriteStates(outputPath+".states.bin");
            program.WriteTargets(outputPath+".targets.bin",vocab);
            int rc=gguf!=IntPtr.Zero?Native.GgufWriterFinalize(gguf):Native.FormatWriterFinalize(safe);
            if(rc!=0)throw new IOException("native output codec failed to finalize the model");
            var options=new JsonSerializerOptions{WriteIndented=true};
            File.WriteAllText(outputPath+".tensors.json",JsonSerializer.Serialize(program.Tensors,options));
            var lineage=program.Tensors.Select(t=>{
                var record=JsonSerializer.SerializeToElement(t);string name=record.GetProperty("gguf_name").GetString()??record.GetProperty("hf_name").GetString()!;
                int layer=-1;if(name.StartsWith("blk.",StringComparison.Ordinal))int.TryParse(name.Split('.')[1],out layer);
                else if(name.StartsWith("model.layers.",StringComparison.Ordinal))int.TryParse(name.Split('.')[2],out layer);
                var dependencies=new HashSet<string>(StringComparer.Ordinal){desc.Embed.Key};
                if(layer>=0)for(int l=0;l<=layer;l++){foreach(var h in desc.Layers[l].Heads)dependencies.Add(h.Key);dependencies.Add(desc.Layers[l].Ffn.Key);foreach(var e in desc.Layers[l].Experts)dependencies.Add(e.Key);}
                if(name is "output.weight" or "lm_head.weight" or "output_norm.weight" or "model.norm.weight"){
                    dependencies.UnionWith(ids.Keys);dependencies.Add(desc.LmHead.Key);
                }
                return new{tensor=t,layer,operator_dependencies=dependencies.Order(StringComparer.Ordinal),source_attribution=attribution is null?null:Path.GetFileName(outputPath)+".source-attribution.json"};
            }).ToArray();
            File.WriteAllText(outputPath+".slot-lineage.json",JsonSerializer.Serialize(lineage,options));
            File.WriteAllText(outputPath+".calculations.json",JsonSerializer.Serialize(new{recipe=desc.RecipeId.ToHex(),source_scope=sourceScope,basis,operator_bindings=ids,layers=program.Calculations},options));
            File.WriteAllBytes(outputPath+".recipe.json",desc.CanonicalJson);
            if(sequences is not null)File.WriteAllText(outputPath+".sequences.json",JsonSerializer.Serialize(sequences.Select(s=>new{entity=Convert.ToHexStringLower(s.Evidence.EntityId),physicality=Convert.ToHexStringLower(s.Evidence.PhysicalityId),tokens=s.Slots}),options));
            if(safe!=IntPtr.Zero)foreach(string name in new[]{"recipe","provenance","basis","operators","tensors","calculations","slot-lineage","source-attribution","sequences"}) {
                string sidecar=outputPath+"."+name+".json";
                if(File.Exists(sidecar))File.Copy(sidecar,Path.Combine(outputPath,name+".json"),overwrite:true);
            }
            Console.WriteLine($"native synthesis complete: {outputPath} | layers={desc.NumLayers} heads={heads}/{kv} experts={experts} width={width} format={format}");
            return Task.FromResult(0);
        }finally{if(gguf!=IntPtr.Zero)Native.GgufWriterFree(gguf);if(safe!=IntPtr.Zero)Native.FormatWriterFree(safe);}
    }
    private static void WriteOperators(string file,IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,FoundryReadout.Bindings binding,
        IReadOnlyDictionary<string,FoundryExport.PlaneCoo> planes,IReadOnlyDictionary<string,FoundryReadout.Program> paths)
    {
        using var stream=File.Create(file);using var writer=new Utf8JsonWriter(stream);
        writer.WriteStartObject();writer.WriteString("cells","exact sparse operands submitted to the native compiler; canonical realization normalization occurs natively");
        writer.WriteStartArray("bindings");foreach(var token in tokens){writer.WriteStartObject();writer.WriteNumber("slot",token.TokenId);writer.WriteString("canonical_id",token.EntityId.ToHex());writer.WriteString("operand_id",binding.OperandIds[token.TokenId].ToHex());writer.WriteNumber("input_operand",binding.InputOperand[token.TokenId]);writer.WriteEndObject();}writer.WriteEndArray();
        writer.WriteStartArray("operators");foreach(var (key,plane) in planes){
            writer.WriteStartObject();writer.WriteString("key",key);
            if(key.StartsWith("metric:",StringComparison.Ordinal))writer.WriteString("selection","generation.metric_edges: angular candidate probe=64, refined k=16; exp(-distance), maximum-absolute-value normalization");
            if(key=="context"){writer.WriteString("execution","causal attention over ordered prefixes");writer.WriteEndObject();continue;}
            var actual=paths.TryGetValue(key,out var path)?path.Plane:plane;
            writer.WriteStartArray("cells");for(int x=0;x<actual.Nnz;x++){writer.WriteStartArray();writer.WriteNumberValue(actual.Rows[x]);writer.WriteNumberValue(actual.Cols[x]);writer.WriteNumberValue(actual.Vals[x]);writer.WriteEndArray();}writer.WriteEndArray();writer.WriteEndObject();
        }writer.WriteEndArray();writer.WriteEndObject();
    }
}
