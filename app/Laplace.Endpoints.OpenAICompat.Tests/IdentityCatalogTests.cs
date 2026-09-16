using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class IdentityCatalogTests
{
    [Theory]
    [InlineData("identity.create_account", "uuid,uuid,text,text,text,text,text,text,text,text")]
    [InlineData("identity.account", "text,text,text")]
    [InlineData("identity.update_profile", "uuid,text,text,text,text,text,text")]
    [InlineData("identity.upsert_client", "text,text,text")]
    [InlineData("identity.put_session", "text,uuid,text,bytea,timestamptz,timestamptz,timestamptz")]
    [InlineData("identity.session", "text")]
    [InlineData("identity.revoke_session", "text,uuid")]
    [InlineData("identity.sessions", "uuid")]
    [InlineData("identity.upsert_conversation", "text,text,uuid,text")]
    [InlineData("identity.conversations", "uuid,text")]
    public void EveryIdentityCommandUsesNativeTextAndTypedPositionalBindings(string name, string declaration)
    {
        var types = declaration.Split(',');
        using var connection = new NpgsqlConnection();
        using var command = NpgsqlCatalog.Command(connection, null, name, new object?[types.Length]);
        Assert.Equal(SqlCatalog.Get(name).Text, command.CommandText);
        Assert.Equal(types.Length, command.Parameters.Count);
        for (int i = 0; i < types.Length; i++)
        {
            Assert.Equal(string.Empty, command.Parameters[i].ParameterName);
            Assert.Equal(types[i], command.Parameters[i].DataTypeName);
            Assert.Same(DBNull.Value, command.Parameters[i].Value);
            Assert.Contains($"${i + 1}", command.CommandText);
        }
    }

    [Fact]
    public void ValuesStaySeparateFromSqlAndNullRetainsItsDeclaredType()
    {
        var user = Guid.NewGuid();
        const string exactTitle = "  Owner's workspace; 日本語  ";
        using var connection = new NpgsqlConnection();
        using var command = NpgsqlCatalog.Command(connection, null, "identity.create_account",
            user, Guid.NewGuid(), "fixture-tenant", exactTitle, "provider", "issuer", "subject", null, "", null);
        Assert.Equal(user, command.Parameters[0].Value);
        Assert.Equal(exactTitle, command.Parameters[3].Value);
        Assert.Same(DBNull.Value, command.Parameters[7].Value);
        Assert.Equal("text", command.Parameters[7].DataTypeName);
        Assert.Equal("", command.Parameters[8].Value);
        Assert.DoesNotContain(exactTitle, command.CommandText);
    }

    [Fact]
    public void WrongArityAndUnknownQueryFailBeforeTransport()
    {
        using var connection = new NpgsqlConnection();
        Assert.Throws<ArgumentException>(() => NpgsqlCatalog.Command(connection, null, "identity.account", "provider"));
        Assert.Throws<ArgumentException>(() => NpgsqlCatalog.Command(connection, null, "identity.not_installed"));
    }
}
