// Stream F scaffold per /home/ahart/.claude/plans/replicated-hatching-stream.md.
//
// Per ADR 0011 IProtocolEndpoint + DESIGN.md:679-684 + GLOSSARY "Endpoint extension":
// translates OpenAI-compatible HTTP requests (chat completions, completions,
// embeddings) into substrate cascade queries; translates substrate responses
// back into OpenAI-shape JSON. Dissolves conventional inference runtimes —
// the substrate IS the serving layer (GLOSSARY:493).
//
// Stream F-complete implements:
//   - POST /v1/chat/completions       → cascade A* per ADR 0035 with chat template
//   - POST /v1/completions            → cascade A* completion mode
//   - POST /v1/embeddings             → physicality lookup (PROJECTION coord)
//   - GET  /v1/models                 → list of synthesized model recipes
//   - Streaming SSE for chat completions per OpenAI spec
//
// Depends on Stream E (compiled cascade A* SRF). Until Stream E lands the
// endpoints return 501 Not Implemented with the documented pending-Stream-E
// stub message.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", stream = "F-scaffold" }));

app.MapGet("/v1/models", () => Results.Json(new
{
    @object = "list",
    data    = Array.Empty<object>(),
    note    = "Stream F-complete will list synthesized model recipes from substrate"
}));

app.MapPost("/v1/chat/completions", (HttpContext ctx) => Results.StatusCode(501)
    /* Stream F-complete: cascade A* through typed attestation graph per ADR 0035
       weighted by Glicko-2 effective-mu, prompt decomposed per R19. Until Stream
       E's compiled cascade SRF lands, this endpoint returns 501. */);

app.MapPost("/v1/completions", (HttpContext ctx) => Results.StatusCode(501));
app.MapPost("/v1/embeddings",   (HttpContext ctx) => Results.StatusCode(501));

app.Run();
