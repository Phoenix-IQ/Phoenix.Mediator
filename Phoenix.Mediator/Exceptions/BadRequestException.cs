using System.Net;

namespace Phoenix.Mediator.Exceptions;

public class BadRequestException(string message) : HttpResponseException(new(HttpStatusCode.BadRequest, [message]));
