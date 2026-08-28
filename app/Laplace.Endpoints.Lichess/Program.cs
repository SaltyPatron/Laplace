using Laplace.Endpoints.Lichess;

await using var app = LichessServiceHost.Build(LichessOptions.FromEnvironment());
await app.RunAsync();
