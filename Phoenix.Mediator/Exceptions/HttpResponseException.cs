using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Exceptions;

public sealed class HttpResponseException(ErrorResponse errorsResponse) : Exception
{
    public HttpStatusCode HttpStatusCode => errorsResponse.HttpStatusCode;
    public IReadOnlyList<string> Errors => errorsResponse.Errors;
}