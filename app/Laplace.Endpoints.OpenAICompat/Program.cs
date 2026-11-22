using Laplace.Endpoints.OpenAICompat;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Laplace.Hosting;
using Microsoft.Extensions.Options;

return await LaplaceHost.RunAsync("api", args, async logHealth =>
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = LaplaceInstall.InstallRoot,
        WebRootPath = LaplaceInstall.WebRoot,
    });

    builder.AddLaplaceHosting(
        new LaplaceHostOptions(
            ServiceName: "api",
            DefaultPort: LaplaceInstall.EndpointPort,
            RateLimitPerClientPerMinute: 300,
            WebhookPerMinute: 120,
            PublicQueryConcurrencyLimit: 16,
            ClientDiagnosticsPerMinute: 60,
            MapHealth: false),
        logHealth);

    builder.Services.AddOpenAiCompatServices();
    builder.Services.AddSingleton<AdminPostgresDataSources>();
    builder.Services.AddOpenApi();

    var app = builder.Build();
    app.InitializeLaplaceLogging();

    var laplaceAuth = app.Services.GetRequiredService<IOptions<LaplaceAuthOptions>>().Value;
    if (laplaceAuth.DevPrincipalActive)
        app.Logger.LogWarning(
            "LAPLACE_AUTH_DEV_PRINCIPAL is set: requests without a credential act as workspace '{Workspace}' with operator authority.",
            laplaceAuth.DevPrincipal!.Trim());

    var publicOrigin = app.Services.GetRequiredService<BrowserAuthSettings>().PublicOrigin;
    app.UseLaplaceHosting(
        publicOrigin,
        beforeLogging: static a => a.UseMiddleware<CorrelationIdMiddleware>(),
        beforeCompression: static a =>
        {
            a.UseMiddleware<ExceptionEnvelopeMiddleware>();
            a.UseMiddleware<RefactorProxyMiddleware>();
        },
        afterAuth: static a =>
        {
            a.UseMiddleware<ApiKeyEnforcementMiddleware>();
            a.UseMiddleware<BillingAccountBoundaryMiddleware>();
            a.UseMiddleware<CodeModelChatMiddleware>();
        });

    if (!LaplaceHost.IsOpenApiDocumentGeneration)
        app.MapPrometheusScrapingEndpoint();
    app.MapOpenApi();
    app.MapCoreEndpoints();
    app.MapClientDiagnostics();
    app.MapQueryEndpoints();
    app.MapOpEndpoints();
    app.MapAdminEndpoints();
    app.MapIngestAdminEndpoints();
    app.MapServiceControlEndpoints();
    app.MapOpenAiCompatEndpoints();
    app.MapCodeEndpoints();
    app.MapFoundryEndpoints();
    app.MapBillingEndpoints();
    app.MapBillingIdentityEndpoints();
    app.MapIdentityEndpoints();
    app.MapAccountEndpoints();
    app.MapAccessEndpoints();
    app.MapChessEndpoints();
    app.MapCompetitionEndpoints();
    app.MapFeedbackEndpoints();
    app.MapUserContentEndpoints();
    app.MapMcpEndpoints();

    app.MapFallback("/v1/{*path}", () => Results.Json(
        new Laplace.Api.Contracts.ErrorResponse(
            new Laplace.Api.Contracts.ErrorBody("not_found", "unknown_route", "No such API route.")),
        statusCode: StatusCodes.Status404NotFound));
    app.MapFallbackToFile("index.html");

    await app.RunAsync();
    return 0;
});

public partial class Program;
