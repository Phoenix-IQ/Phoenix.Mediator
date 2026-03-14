using Microsoft.AspNetCore.Http;

namespace Phoenix.Mediator.Abstractions;

public interface ISender
{
    /// <summary>
    /// Sends a request and returns either:
    /// - the handler response value
    /// - an <see cref="IResult"/> for no-content or error flows
    /// </summary>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);
}
