using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Wrappers;

namespace Phoenix.Mediator.Web;

/// <summary>
/// What the endpoint helpers (<c>Get</c>, <c>Post</c>, ...) make of whatever an endpoint returns — usually what
/// <c>sender.Send(...)</c> returned.
/// </summary>
internal static class AutoResponseMappingExtensions
{
    /// <summary>
    /// Maps an endpoint's return value to the response:
    /// - null => the configured empty-response status (<paramref name="emptyResponseStatusCode"/>)
    /// - IResult => passthrough (allows handlers/pipelines to return Results.* directly)
    /// - ErrorResponse => uses ErrorResponse.HttpStatusCode, with the error body
    /// - otherwise => 200 OK (and body = value)
    /// </summary>
    public static IResult ToApiResult(this object? value, EmptyResponseStatusCode emptyResponseStatusCode)
    {
        return value switch
        {
            null => CreateEmptyResponseResult(emptyResponseStatusCode),
            IResult result => result,
            // Same body shape as the exception-handling middleware, trace id included.
            ErrorResponse errors => new ErrorResponseResult(errors),
            // Always return JSON so Swagger/clients consistently get the documented content-type/schema.
            _ => Results.Json(value)
        };
    }

    private static IResult CreateEmptyResponseResult(EmptyResponseStatusCode statusCode)
    {
        return statusCode switch
        {
            EmptyResponseStatusCode.Ok => Results.Ok(),
            EmptyResponseStatusCode.NoContent => Results.NoContent(),
            _ => throw new InvalidOperationException(MediatorMessages.InvalidEmptyResponseStatusCode)
        };
    }

    /// <summary>
    /// An <see cref="ErrorResponse"/> written as the exception-handling middleware writes errors: its status, and an
    /// <see cref="ErrorsResponse"/> body. The trace id is read when the response is written, so it falls back to
    /// <see cref="HttpContext.TraceIdentifier"/> when the request has no <see cref="Activity"/>, as the middleware's does.
    /// </summary>
    private sealed class ErrorResponseResult(ErrorResponse response) : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => (int)response.HttpStatusCode;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
            return Results.Json(new ErrorsResponse(response.Errors, traceId), statusCode: (int)response.HttpStatusCode).ExecuteAsync(httpContext);
        }
    }
}
