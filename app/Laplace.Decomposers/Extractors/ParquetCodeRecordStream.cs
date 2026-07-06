using System.Runtime.CompilerServices;
using Parquet;
using Parquet.Schema;

namespace Laplace.Decomposers.Extractors;

/// <summary>
/// Shared parquet row streaming for Stack/TinyCodes code corpora. Pure extract —
/// no builder logic, no SQL.
/// </summary>
public static class ParquetCodeRecordStream
{
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
