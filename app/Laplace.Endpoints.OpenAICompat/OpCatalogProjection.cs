using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Presentation of the same installed signatures and policy the invoker consumes.
/// No client-side SQL-signature parser, guessed write-name rules, or new authority.
/// </summary>
internal static class OpCatalogProjection
{
    internal static OpDescription Describe(IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue("name", out var rawName) || rawName is not string name || name.Length == 0
            || !row.TryGetValue("args", out var rawArgs) || rawArgs is not string args
            || !row.TryGetValue("kind", out var rawKind) || rawKind is not string kind)
            throw new InvalidDataException("The installed operation catalog returned an incomplete signature.");
        row.TryGetValue("returns", out var returns);
        return new OpDescription(name, args, returns as string, kind,
            InstalledOpInvoker.ParseSignature(args)
                .Select(p => new OpParameterDescription(p.Name, p.Type, p.Optional)).ToArray(),
            InstalledOpInvoker.IsWritable(name), InstalledOpInvoker.IsDestructive(name));
    }
}
