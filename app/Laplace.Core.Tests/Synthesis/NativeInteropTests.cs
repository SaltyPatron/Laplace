using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Laplace.Engine.Synthesis;

namespace Laplace.Engine.Synthesis.Tests;

public class NativeInteropTests
{
    [Fact]
    public void LaplaceSynthesisVersion_ReturnsExpected()
    {
        var version = NativeInterop.LaplaceSynthesisVersion();
        Assert.Equal("0.1.0", version);
    }

    [Fact]
    public unsafe void ArchTemplateRequiredTensorsComplete_GrowsAndPopulatesBeyond300()
    {
        const int layers = 34;
        const int expected = 9 * layers + 3;
        byte[] json = Encoding.UTF8.GetBytes(
            $$"""
            {
              "architectures": ["LlamaForCausalLM"],
              "hidden_size": 2048,
              "intermediate_size": 5632,
              "num_attention_heads": 32,
              "num_hidden_layers": {{layers}},
              "num_key_value_heads": 4,
              "torch_dtype": "bfloat16",
              "vocab_size": 32000
            }
            """);

        IntPtr recipe;
        fixed (byte* p = json)
            recipe = NativeInterop.RecipeParse(p, (nuint)json.Length);
        Assert.NotEqual(IntPtr.Zero, recipe);

        IntPtr template = NativeInterop.ArchTemplateLoad("llama");
        Assert.NotEqual(IntPtr.Zero, template);

        try
        {
            // #1054's production failure shape: native needs 309 slots, while the
            // historical C# call sites supplied a fixed 300 and treated any positive
            // return as populated data. The bounded managed ABI must now fail closed.
            var bounded = new TensorSpec[300];
            int boundedRc;
            fixed (TensorSpec* p = bounded)
                boundedRc = NativeInterop.ArchTemplateRequiredTensors(
                    template, recipe, p, (nuint)bounded.Length);
            Assert.Equal(-2, boundedRc);
            Assert.True(bounded[0].Name == null);

            // The complete form exercises the real native capacity-query contract:
            // probe required count, allocate exactly that many slots, then retry.
            int count = NativeInterop.ArchTemplateRequiredTensorsComplete(
                template, recipe, out var specs);

            Assert.Equal(expected, count);
            Assert.Equal(expected, specs.Length);
            Assert.Equal("model.embed_tokens.weight",
                Marshal.PtrToStringUTF8((IntPtr)specs[0].Name));
            Assert.Equal("model.layers.33.post_attention_layernorm.weight",
                Marshal.PtrToStringUTF8((IntPtr)specs[^3].Name));
            Assert.Equal("lm_head.weight",
                Marshal.PtrToStringUTF8((IntPtr)specs[^1].Name));
            Assert.Equal(2UL, specs[0].Rank);
            Assert.Equal(32000UL, specs[0].Shape[0]);
            Assert.Equal(2048UL, specs[0].Shape[1]);
        }
        finally
        {
            NativeInterop.ArchTemplateFree(template);
            NativeInterop.RecipeFree(recipe);
        }
    }
}
