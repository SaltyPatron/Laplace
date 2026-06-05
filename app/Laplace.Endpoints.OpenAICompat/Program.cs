using Laplace.Endpoints.OpenAICompat;

// Stream F scaffold per /home/ahart/.claude/plans/replicated-hatching-stream.md.
//
// IProtocolEndpoint + + the "Endpoint extension" definition:
// translates OpenAI-compatible HTTP requests (chat completions, completions,
// embeddings) into substrate cascade queries; translates substrate responses
// back into OpenAI-shape JSON. Dissolves conventional inference runtimes —
// the substrate IS the serving layer.
//
// Stream F-complete implements:
//   - POST /v1/chat/completions       → cascade A* with chat template
//   - POST /v1/completions            → cascade A* completion mode
//   - POST /v1/embeddings             → physicality lookup (PROJECTION coord)
//   - GET  /v1/models                 → list of synthesized model recipes
//   - Streaming SSE for chat completions per OpenAI spec
//
// Depends on Stream E (compiled cascade A* SRF). Until Stream E lands the
// endpoints return 501 Not Implemented with the documented pending-Stream-E
// stub message.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenAiCompatServices();

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionEnvelopeMiddleware>();

app.MapCoreEndpoints();
app.MapOpenAiCompatEndpoints();
app.MapBillingEndpoints();

app.Run();

public partial class Program;
