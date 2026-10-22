using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed unsafe class NativeIdentityParityTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Codepoint(uint scalar, Hash128* result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Compose(Hash128* children, nuint count, Hash128* result);

    [Fact]
    public void ScalarWordsAndCompositionsMatchTheIndependentNativeLibrary()
    {
        var path = Environment.GetEnvironmentVariable("LAPLACE_NATIVE_TEST_LIBRARY") ?? "laplace";
        var library = NativeLibrary.Load(path);
        try
        {
            var scalar = Marshal.GetDelegateForFunctionPointer<Codepoint>(NativeLibrary.GetExport(library, "lp_id_codepoint"));
            var compose = Marshal.GetDelegateForFunctionPointer<Compose>(NativeLibrary.GetExport(library, "lp_id_compose"));
            foreach (uint value in new uint[] { 0, 0x41, 0xe9, 0x1f9e0, 0x10ffff })
            {
                Hash128 expected;
                scalar(value, &expected);
                Assert.Equal(expected, Hash128.Blake3(Encoding.UTF8.GetBytes(new Rune((int)value).ToString())));
            }
            foreach (var word in new[] { "a", "dog", "mississippi", "abandon" })
            {
                var children = word.Select(character => Hash128.Blake3(Encoding.UTF8.GetBytes(character.ToString()))).ToArray();
                fixed (Hash128* input = children)
                {
                    Hash128 expected;
                    compose(input, (nuint)children.Length, &expected);
                    foreach (byte tier in new byte[] { 0, 2, 7, 255 })
                    {
                        var actual = Hash128.Merkle(tier, children);
                        var modules = System.Diagnostics.Process.GetCurrentProcess().Modules
                            .Cast<System.Diagnostics.ProcessModule>()
                            .Where(module => module.ModuleName.Contains("laplace_core", StringComparison.OrdinalIgnoreCase))
                            .Select(module => module.FileName);
                        Assert.True(expected == actual,
                            $"Native identity differs for {word}, tier {tier}; loaded Core: {string.Join(", ", modules)}");
                    }
                }
            }
            // Cover both sides of Native's one-chunk composition implementation.
            foreach (int count in new[] { 1, 2, 33, 64, 65, 129 })
            {
                var children = new Hash128[count];
                for (int i = 0; i < count; i++)
                    fixed (Hash128* item = &children[i]) scalar((uint)(0x41 + i), item);
                fixed (Hash128* input = children)
                {
                    Hash128 expected;
                    compose(input, (nuint)count, &expected);
                    Assert.Equal(expected, Hash128.Merkle(6, children));
                    if (count == 1) Assert.Equal(children[0], expected);
                }
            }
        }
        finally { NativeLibrary.Free(library); }
    }
}
