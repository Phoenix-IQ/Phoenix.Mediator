using Microsoft.AspNetCore.Http;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Wrappers;

namespace Phoenix.Mediator.Web;

public static class AutoResponseMappingExtensions
{
    /// <summary>
    /// Maps mediator outputs to minimal-api results:
    /// - null => 204 NoContent
    /// - IResult => passthrough (allows handlers/pipelines to return Results.* directly)
    /// - ErrorResponse => uses ErrorResponse.HttpStatusCode
    /// - otherwise => 200 OK (and body = value)
    /// </summary>
    public static IResult ToApiResult(this object? value)
    {
        return value switch
        {
            null => Results.NoContent(),
            IResult result => result,
            ErrorResponse errors => Results.Json(new ErrorsResponse(errors.Errors), statusCode: (int)errors.HttpStatusCode),
            // Always return JSON so Swagger/clients consistently get the documented content-type/schema.
            _ => Results.Json(value)
        };
    }

    /// <summary>
    /// Sends a request through the mediator and maps the result (or exception) to an <see cref="IResult"/>.
    /// Use this in HTTP endpoint handlers instead of calling <c>sender.Send()</c> + <c>ToApiResult()</c> manually.
    /// </summary>
    public static async Task<IResult> SendAsApiResult(this ISender sender, object request, CancellationToken cancellationToken = default)
    {
        var result = await sender.Send(request, cancellationToken).ConfigureAwait(false);
        return result.ToApiResult();
    }

    /// <summary>
    /// Strongly-typed overload — no reflection, no boxing.
    /// </summary>
    public static async Task<IResult> SendAsApiResult<TRequest, TResponse>(this ISender sender, TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest<TResponse>
    {
        var result = await sender.Send<TRequest, TResponse>(request, cancellationToken).ConfigureAwait(false);
        return result.ToApiResult();
    }

    /// <summary>
    /// Strongly-typed overload for void requests — no reflection, no boxing.
    /// </summary>
    public static async Task<IResult> SendAsApiResult<TRequest>(this ISender sender, TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
    {
        await sender.Send(request, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }
}