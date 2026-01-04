using Microsoft.AspNetCore.Http;
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
}


