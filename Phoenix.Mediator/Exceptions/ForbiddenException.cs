using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Exceptions;

/// <summary>
/// 403: the caller is signed in but may not do this — the resource belongs to someone else, or their role does not
/// allow it.
/// </summary>
public sealed class ForbiddenException(string message) : HttpResponseException(new ErrorResponse(HttpStatusCode.Forbidden, [message]));
