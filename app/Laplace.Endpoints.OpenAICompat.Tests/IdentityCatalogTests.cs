using Laplace.Engine.Core;
using Laplace.Endpoints.OpenAICompat.Auth;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class IdentityCatalogTests
{
    [Theory]
    [InlineData("identity.create_user", "uuid,text,text,text")]
    [InlineData("identity.create_tenant", "text,text")]
    [InlineData("identity.create_external", "uuid,uuid,text,text,text,text")]
    [InlineData("identity.create_membership", "text,uuid")]
    [InlineData("identity.read_account", "text,text,text")]
    [InlineData("identity.update_user", "text,text,text,uuid")]
    [InlineData("identity.update_external", "text,text,text,text")]
    [InlineData("identity.upsert_client", "text,text,text")]
    [InlineData("identity.put_web_session", "text,uuid,text,bytea,timestamptz,timestamptz,timestamptz")]
    [InlineData("identity.get_web_session", "text")]
    [InlineData("identity.revoke_web_session", "text,uuid")]
    [InlineData("identity.list_web_sessions", "uuid")]
    [InlineData("identity.upsert_conversation", "text,text,uuid,text")]
    [InlineData("identity.list_conversations", "uuid,text")]
    public void EveryIdentityCommandUsesNativeTextAndTypedPositionalBindings(string name, string declaration)
    {
        var types = declaration.Split(',');
        var command = PostgresIdentityStore.Query(name, new object?[types.Length]);
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
        var command = PostgresIdentityStore.Query("identity.create_user", user, exactTitle, null, "");
        Assert.Equal(user, command.Parameters[0].Value);
        Assert.Equal(exactTitle, command.Parameters[1].Value);
        Assert.Same(DBNull.Value, command.Parameters[2].Value);
        Assert.Equal("text", command.Parameters[2].DataTypeName);
        Assert.Equal("", command.Parameters[3].Value);
        Assert.DoesNotContain(exactTitle, command.CommandText);
    }

    [Fact]
    public void WrongArityAndUnknownQueryFailBeforeTransport()
    {
        Assert.Throws<ArgumentException>(() => PostgresIdentityStore.Query("identity.read_account", "provider"));
        Assert.Throws<ArgumentException>(() => PostgresIdentityStore.Query("identity.not_installed"));
    }
}
