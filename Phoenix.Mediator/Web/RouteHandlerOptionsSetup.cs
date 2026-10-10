using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Phoenix.Mediator.Web;

/// <summary>
/// Turns on <see cref="RouteHandlerOptions.ThrowOnBadRequest"/> in every environment; ASP.NET Core turns it on in
/// Development only. A request a Minimal API endpoint can't bind (no body, or a JSON <c>null</c>, for a body parameter;
/// malformed JSON; a body that isn't JSON; a route or query value that doesn't parse) then throws
/// <see cref="BadHttpRequestException"/>, and the exception-handling middleware answers it with the framework's status
/// and message in the documented error body.
/// <para>
/// With the option off, ASP.NET Core sets the status and writes no body. For a parameter that fails to bind it also still
/// runs the endpoint's filters, skipping only the delegate, which is why the endpoint helpers' result filter must leave an
/// error status alone. ASP.NET Core's own setup only ever sets the option to <see langword="true"/>, so its order relative
/// to this one doesn't matter. An app's own <c>Configure&lt;RouteHandlerOptions&gt;</c> after <c>AddMediator</c> wins,
/// as later configuration does.
/// </para>
/// </summary>
internal sealed class RouteHandlerOptionsSetup : IConfigureOptions<RouteHandlerOptions>
{
    public void Configure(RouteHandlerOptions options) => options.ThrowOnBadRequest = true;
}
