using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public sealed record NativeSqlQuery(string Name, string Text, string[] ParameterTypes);

/// <summary>Immutable SQL and parameter contracts marshalled from the native catalog.</summary>
public static partial class SqlCatalog
{
    [LibraryImport("laplace_core", EntryPoint = "laplace_sql_query_text", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr TextPointer(string name);

    [LibraryImport("laplace_core", EntryPoint = "laplace_sql_query_parameters", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ParametersPointer(string name);

    public static NativeSqlQuery Get(string name)
    {
        var text = Marshal.PtrToStringUTF8(TextPointer(name))
            ?? throw new ArgumentException($"Unknown native SQL query '{name}'.", nameof(name));
        var parameters = Marshal.PtrToStringUTF8(ParametersPointer(name))
            ?? throw new InvalidOperationException($"Missing native SQL parameter contract for '{name}'.");
        return new(name, text, parameters.Length == 0 ? [] : parameters.Split(','));
    }
}
