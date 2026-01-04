using System.Net;

namespace Phoenix.Mediator.Wrappers;

public record ErrorResponse(HttpStatusCode HttpStatusCode,IReadOnlyList<string> Errors);