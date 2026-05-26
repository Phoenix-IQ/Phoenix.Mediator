using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Exceptions;

public sealed class BadRequestException(string message) : HttpResponseException(new ErrorResponse(HttpStatusCode.BadRequest, [message]));
