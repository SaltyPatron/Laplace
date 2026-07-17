using System.Reflection;
using System.Runtime.CompilerServices;
using Parquet;
using Parquet.Schema;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Shared parquet row streaming for Stack/TinyCodes code corpora. Pure extract —
/// no builder logic, no SQL.
/// </summary>
public static class SharedParquetRecordStream
{
    /// <summary>One witnessed cell of a generic parquet row: the column name and its
    /// native CLR value (string/long/double/bool/DateTime/… or null).</summary>
    public readonly record struct GenericCell(string Column, object? Value);

    /// <summary>
    /// Generic flat-schema row reader — the container-strip primitive for the generic
    /// <c>ParquetDecomposer</c>. Streams every row as one <see cref="GenericCell"/> per
    /// top-level data field, values in their native CLR type. Makes no schema
    /// assumptions beyond a flat (non-nested) column layout, so ANY tabular parquet
    /// file/dataset can be witnessed column-by-column without bespoke plumbing.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<GenericCell>> ReadGenericRowsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct);

        DataField[] fields = reader.Schema.GetDataFields();
        if (fields.Length == 0) yield break;

        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            ct.ThrowIfCancellationRequested();
            using var rgr = reader.OpenRowGroupReader(rg);
            int count = (int)rgr.RowCount;

            var columns = new Array[fields.Length];
            for (int c = 0; c < fields.Length; c++)
                columns[c] = await ReadColumnAsync(rgr, fields[c], count, ct);

            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cells = new GenericCell[fields.Length];
                for (int c = 0; c < fields.Length; c++)
                {
                    Array col = columns[c];
                    object? value = i < col.Length ? col.GetValue(i) : null;
                    cells[c] = new GenericCell(fields[c].Name, value);
                }
                yield return cells;
            }
        }
    }

    // Parquet.Net 6 exposes no public DataColumn/ReadColumn — reads go through the
    // typed ReadAsync&lt;T&gt;(field, Memory&lt;T&gt;, …) overloads. This resolves the right
    // overload from the field's nullability-aware CLR type once and reads a whole
    // column into a boxed Array, so the generic reader stays type-agnostic.
    private static readonly MethodInfo[] ReadAsyncOverloads = typeof(ParquetRowGroupReader)
        .GetMethods()
        .Where(m => m.Name == nameof(ParquetRowGroupReader.ReadAsync)
            && m.GetParameters() is { Length: 4 } p
            && p[1].ParameterType.IsGenericType
            && p[1].ParameterType.GetGenericTypeDefinition() == typeof(Memory<>))
        .ToArray();

    private static async ValueTask<Array> ReadColumnAsync(
        ParquetRowGroupReader rgr, DataField field, int count, CancellationToken ct)
    {
        Type elem = field.ClrNullableIfHasNullsType;
        Array data = Array.CreateInstance(elem, count);
        Type memType = typeof(Memory<>).MakeGenericType(elem);
        object mem = memType.GetConstructor([elem.MakeArrayType()])!.Invoke([data]);

        Type? underlying = Nullable.GetUnderlyingType(elem);
        MethodInfo? chosen = null;
        foreach (var m in ReadAsyncOverloads)
        {
            MethodInfo mi = m;
            if (m.IsGenericMethodDefinition)
            {
                Type inner = m.GetParameters()[1].ParameterType.GetGenericArguments()[0];
                bool paramNullable = inner.IsGenericType
                    && inner.GetGenericTypeDefinition() == typeof(Nullable<>);
                if (underlying is not null)
                {
                    if (!paramNullable) continue;
                    mi = m.MakeGenericMethod(underlying);
                }
                else
                {
                    if (paramNullable || !elem.IsValueType) continue;
                    mi = m.MakeGenericMethod(elem);
                }
            }
            if (mi.GetParameters()[1].ParameterType == memType) { chosen = mi; break; }
        }
        if (chosen is null)
            throw new NotSupportedException(
                $"No ParquetRowGroupReader.ReadAsync overload for column '{field.Name}' of type {elem}.");

        await (ValueTask)chosen.Invoke(rgr, [field, mem, null, ct])!;
        return data;
    }

    public static DataField? FindField(DataField[] fields, string name)
    {
        foreach (var f in fields)
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    public static IEnumerable<string> EnumerateParquet(string root, SearchOption searchOption)
    {
        if (File.Exists(root))
        {
            if (root.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)) yield return root;
            yield break;
        }
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root, "*.parquet", searchOption)
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }

    /// <summary>
    /// Exact row count from parquet metadata — row-group headers only, no
    /// column data decoded. Feeds IngestInventory so single-container corpora
    /// report real progress instead of input_units=0 (blind "0/N intents").
    /// </summary>
    public static async Task<long> CountRowsAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct);
        long total = 0;
        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            using var rgr = reader.OpenRowGroupReader(rg);
            total += rgr.RowCount;
        }
        return total;
    }

    public static async IAsyncEnumerable<(string? Content, string? Language, string? Path)> ReadStackRowsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct);

        DataField[] fields = reader.Schema.GetDataFields();
        DataField? contentField = FindField(fields, "content");
        DataField? languageField = FindField(fields, "language") ?? FindField(fields, "lang");
        DataField? pathField = FindField(fields, "path") ?? FindField(fields, "max_stars_repo_path");
        DataField? vendorField = FindField(fields, "is_vendor");
        DataField? generatedField = FindField(fields, "is_generated");

        if (contentField is null || languageField is null) yield break;

        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            ct.ThrowIfCancellationRequested();
            using var rgr = reader.OpenRowGroupReader(rg);
            int count = (int)rgr.RowCount;

            string[] contents = new string[count];
            string[] languages = new string[count];
            string[]? paths = pathField is not null ? new string[count] : null;
            bool?[]? vendors = vendorField is not null ? new bool?[count] : null;
            bool?[]? generated = generatedField is not null ? new bool?[count] : null;

            await rgr.ReadAsync(contentField, contents);
            await rgr.ReadAsync(languageField, languages);
            if (paths is not null) await rgr.ReadAsync(pathField!, paths);
            if (vendors is not null) await rgr.ReadAsync<bool>(vendorField!, vendors);
            if (generated is not null) await rgr.ReadAsync<bool>(generatedField!, generated);

            for (int i = 0; i < count; i++)
            {
                if (vendors is not null && vendors[i] == true) continue;
                if (generated is not null && generated[i] == true) continue;
                yield return (contents[i], languages[i], paths is not null ? paths[i] : null);
            }
        }
    }

    public static async IAsyncEnumerable<(string? ConceptKey, string? Lang, string? Prompt, string? Response)> ReadTinyCodesRowsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct);

        DataField[] fields = reader.Schema.GetDataFields();
        DataField? taskField = FindField(fields, "task_id");
        DataField? langField = FindField(fields, "programming_language");
        DataField? promptField = FindField(fields, "prompt");
        DataField? respField = FindField(fields, "response");
        string fileStem = Path.GetFileNameWithoutExtension(path);
        long rowBase = 0;
        if (promptField is null || respField is null || (taskField is null && langField is null))
            throw new InvalidOperationException(
                $"Unrecognized TinyCodes parquet schema in '{path}' — "
                + $"need prompt+response and task_id or programming_language; found: "
                + string.Join(", ", fields.Select(f => f.Name)));

        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            ct.ThrowIfCancellationRequested();
            using var rgr = reader.OpenRowGroupReader(rg);
            int count = (int)rgr.RowCount;

            string[]? taskIds = null;
            string[]? langs = null;
            string[] prompts = new string[count];
            string[] resps = new string[count];

            if (taskField is not null)
                await rgr.ReadAsync(taskField, taskIds = new string[count]);
            if (langField is not null)
                await rgr.ReadAsync(langField, langs = new string[count]);
            await rgr.ReadAsync(promptField, prompts);
            await rgr.ReadAsync(respField, resps);

            for (int i = 0; i < count; i++)
            {
                string? lang = langs?[i];
                string? key = taskIds?[i] ?? $"{fileStem}/{rowBase + i}";
                yield return (key, lang, prompts[i], resps[i]);
            }
            rowBase += count;
        }
    }
}
