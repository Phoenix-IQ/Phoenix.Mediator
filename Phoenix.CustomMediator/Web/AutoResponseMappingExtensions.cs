using Microsoft.AspNetCore.Http;
using Phoenix.CustomMediator.Wrappers;

namespace Phoenix.CustomMediator.Web;

public static class AutoResponseMappingExtensions
{
    /// <summary>
    /// Maps mediator outputs to minimal-api results:
    /// - null => 204 NoContent
    /// - ErrorsResponse => 200 OK (and body = ErrorsResponse) [override if you want typed status codes]
    /// - otherwise => 200 OK (and body = value)
    /// </summary>
    public static IResult ToApiResult(this object? value)
    {
        return value switch
        {
            null => Results.NoContent(),
            ErrorsResponse errors => Results.Json(errors),
            _ => Results.Ok(value)
        };
    }
}


