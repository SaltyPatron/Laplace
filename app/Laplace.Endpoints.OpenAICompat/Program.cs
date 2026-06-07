using Laplace.Endpoints.OpenAICompat;

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
