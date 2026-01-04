using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Exceptions;

public sealed class NotFoundException(string message) : HttpResponseException(new ErrorResponse(HttpStatusCode.NotFound, [message]));