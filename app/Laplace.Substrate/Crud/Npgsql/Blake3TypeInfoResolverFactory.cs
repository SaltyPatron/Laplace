using Laplace.Engine.Core;
using global::Npgsql.Internal;
using global::Npgsql.Internal.Postgres;

// Npgsql's public custom-converter API is experimental in the pinned 10.0.2.
// Keep that dependency confined to this transport adapter.
#pragma warning disable NPG9001

namespace Laplace.SubstrateCRUD.Npgsql;

internal sealed class Blake3TypeInfoResolverFactory : PgTypeInfoResolverFactory
{
    public override IPgTypeInfoResolver CreateResolver() => new Resolver();
    public override IPgTypeInfoResolver CreateArrayResolver() => new ArrayResolver();

    private class Resolver : IPgTypeInfoResolver
    {
        protected static DataTypeName Blake3Name => new("public.blake3");
        private TypeInfoMappingCollection? _mappings;
        protected TypeInfoMappingCollection Mappings => _mappings ??= AddMappings();

        public PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => Mappings.Find(type, dataTypeName, options);

        private static TypeInfoMappingCollection AddMappings()
        {
            var mappings = new TypeInfoMappingCollection();
            mappings.AddStructType<Hash128>(Blake3Name,
                static (options, mapping, _) => mapping.CreateInfo(options, new Converter()),
                isDefault: true);
            return mappings;
        }
    }

    private sealed class ArrayResolver : Resolver, IPgTypeInfoResolver
    {
        private TypeInfoMappingCollection? _mappings;
        private new TypeInfoMappingCollection Mappings => _mappings ??= AddMappings(base.Mappings);

        public new PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => Mappings.Find(type, dataTypeName, options);

        private static TypeInfoMappingCollection AddMappings(TypeInfoMappingCollection elements)
        {
            var mappings = new TypeInfoMappingCollection(elements);
            mappings.AddStructArrayType<Hash128>(Blake3Name);
            return mappings;
        }
    }

    private sealed class Converter : PgBufferedConverter<Hash128>
    {
        public override bool CanConvert(DataFormat format, out BufferRequirements bufferRequirements)
        {
            bufferRequirements = BufferRequirements.CreateFixedSize(16);
            return format == DataFormat.Binary;
        }

        protected override Hash128 ReadCore(PgReader reader)
        {
            if (reader.CurrentRemaining != 16)
                throw new InvalidDataException("A blake3 entity identity must contain exactly 16 bytes.");
            Span<byte> bytes = stackalloc byte[16];
            reader.ReadBytes(bytes);
            return Hash128.FromBytes(bytes);
        }

        protected override void WriteCore(PgWriter writer, Hash128 value)
        {
            Span<byte> bytes = stackalloc byte[16];
            value.WriteBytes(bytes);
            writer.WriteBytes(bytes);
        }
    }
}

#pragma warning restore NPG9001
