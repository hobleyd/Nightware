using Nightware.Admin.Composition;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNightwareAdmin(builder.Configuration);

var app = builder.Build();
app.UseNightwareAdmin();
app.Run();

/// <summary>Entry point; public so test hosts can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
