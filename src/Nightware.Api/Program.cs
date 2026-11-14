using Nightware.Api.Composition;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNightware(builder.Configuration);

var app = builder.Build();
app.UseNightware();
app.Run();

/// <summary>Entry point; public so test hosts can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
