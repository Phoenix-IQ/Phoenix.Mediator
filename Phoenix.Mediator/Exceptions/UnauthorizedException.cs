using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Exceptions;

/// <summary>
/// 401: the caller is not authenticated, or their credentials are no longer valid (an expired or revoked refresh
/// token, say). Clients typically respond by signing in again, so for "signed in but not allowed" throw
/// <see cref="ForbiddenException"/> instead.
/// </summary>
public sealed class UnauthorizedException(string message) : HttpResponseException(new ErrorResponse(HttpStatusCode.Unauthorized, [message]));
