using Microsoft.AspNetCore.Http;
using Phoenix.Mediator.Abstractions;

namespace Phoenix.Mediator.Web;

public static class SenderResultExtensions
{
    /// <summary>
    /// Sends a request through the mediator and maps the returned object to an IResult.
    /// In particular: null => 204 No Content.
    /// </summary>
    public static async Task<IResult> SendAsResult(this ISender sender, object request, CancellationToken ct = default)
    {
        var value = await sender.Send(request, ct).ConfigureAwait(false);
        return value.ToApiResult();
    }
}


