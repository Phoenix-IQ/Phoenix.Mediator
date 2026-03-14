using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Exceptions;

public class HttpResponseException : Exception
{
    public HttpResponseException(ErrorResponse errorResponse)
        : this(errorResponse, BuildMessage(errorResponse))
    {
    }

    private HttpResponseException(ErrorResponse errorResponse, string message)
        : base(message)
    {
        ErrorResponse = errorResponse;
    }

    public ErrorResponse ErrorResponse { get; }
    public HttpStatusCode HttpStatusCode => ErrorResponse.HttpStatusCode;
    public IReadOnlyList<string> Errors => ErrorResponse.Errors;

    private static string BuildMessage(ErrorResponse errorResponse)
    {
        ArgumentNullException.ThrowIfNull(errorResponse);

        return errorResponse.Errors.Count > 0
            ? string.Join("; ", errorResponse.Errors)
            : errorResponse.HttpStatusCode.ToString();
    }
}
