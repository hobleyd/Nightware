using System.Reflection;

using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;

namespace Nightware.Admin.Pages.Api;

/// <summary>The OpenAPI document behind the API reference (embedded at build time from <c>Api/openapi.json</c>).</summary>
public sealed class OpenApiModel(IAdminActor actor) : ConsolePageModel(actor)
{
    internal const string ResourceName = "Nightware.Admin.Api.openapi.json";

    private static readonly Lazy<byte[]> Document = new(static () =>
    {
        using var stream = typeof(OpenApiModel).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The console was built without {ResourceName}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    public IActionResult OnGet() => File(Document.Value, "application/json");
}
