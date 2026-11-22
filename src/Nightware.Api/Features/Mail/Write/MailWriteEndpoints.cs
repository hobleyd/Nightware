using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Mail.Write;

public static class MailWriteEndpoints
{
    /// <summary>Maps the mail write routes. Owned by Phase 3.</summary>
    public static RouteGroupBuilder MapMailWriteEndpoints(this RouteGroupBuilder graph)
    {
        MessageWriteEndpoints.Map(graph);
        MessageSendEndpoints.Map(graph);
        MimeEndpoints.Map(graph);
        FolderWriteEndpoints.Map(graph);
        Categories.MasterCategoryEndpoints.Map(graph);
        Rules.MessageRuleEndpoints.Map(graph);
        Upload.MailUploadEndpoints.Map(graph);
        return graph;
    }
}
