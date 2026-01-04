using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Exceptions;

public class HttpResponseException(ErrorResponse errorResponse) : Exception
{
    public HttpStatusCode HttpStatusCode => errorResponse.HttpStatusCode;
    public IReadOnlyList<string> Errors => errorResponse.Errors;
}