using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// A language filter's tags widened to every tag of the languages they name, by ISO 639
/// testimony in the substrate. Before ISO 639 is admitted the filter keeps its own tags.
/// </summary>
public static class LanguageTagExpansion
{
    public static async Task<LanguageFilter?> ExpandAsync(
        LanguageFilter? filter, NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        if (filter is not { IsActive: true }) return filter;
        await using var command = dataSource.CreateCommand(SqlCatalog.Get("language.tags").Text);
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, filter.Tags.ToArray());
        var tags = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            tags.Add(reader.GetString(0));
        return filter.WithTags(tags);
    }
}
