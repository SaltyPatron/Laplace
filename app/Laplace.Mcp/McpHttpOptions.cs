using Laplace.Engine.Core;

namespace Laplace.Mcp;

public sealed record McpHttpOptions(
    string Token,
    string Origin,
    int Port = 5188,
    int MaxSessions = 32,
    int IdleMinutes = 30)
{
    public static McpHttpOptions FromEnvironment() => new(
        LaplaceInstall.TryReadConfig("LAPLACE_MCP_TOKEN", "mcp.env") ?? "",
        LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_MCP_ORIGIN") ?? "https://hart-server:8443",
        int.TryParse(LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_MCP_HTTP_PORT"), out var port) ? port : 5188);

    public void Validate()
    {
        if (Token.Length < 32 || Token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("LAPLACE_MCP_TOKEN must contain at least 32 non-whitespace characters.");
        if (!Uri.TryCreate(Origin, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("LAPLACE_MCP_ORIGIN must be an HTTPS origin, without path or credentials.");
        if (Port is < 0 or > 65535 || MaxSessions is < 1 or > 256 || IdleMinutes is < 1 or > 120)
            throw new InvalidOperationException("Invalid MCP listener/session limits.");
    }
}
