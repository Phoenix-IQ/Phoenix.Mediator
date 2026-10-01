using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Exceptions;

/// <summary>
/// 409: the request conflicts with the current state — a duplicate that must be unique, or a record someone else
/// changed in the meantime.
/// </summary>
public sealed class ConflictException(string message) : HttpResponseException(new ErrorResponse(HttpStatusCode.Conflict, [message]));
