namespace Laplace.Engine.Core;

/// <summary>Verified request authority carried by an operation, never part of content identity.</summary>
public sealed record OperationPrincipal(string TenantId, string Subject, string Authentication, string? Provider = null);
