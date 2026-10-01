using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Wrappers;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Phoenix.Mediator.Web;

/// <summary>
/// How the Phoenix exception-handling middleware turns exceptions into error responses. Configure it with
/// <c>services.Configure&lt;ExceptionHandlingOptions&gt;(options =&gt; ...)</c>:
/// <code>
/// builder.Services.Configure&lt;ExceptionHandlingOptions&gt;(options => options
///     .Map&lt;DbUpdateConcurrencyException&gt;(HttpStatusCode.Conflict, _ => "Someone else changed this record. Reload and try again.")
///     .Map&lt;TimeoutRejectedException&gt;(HttpStatusCode.ServiceUnavailable));
/// </code>
/// <para>
/// <see cref="HttpResponseException"/>, the framework's <c>BadHttpRequestException</c>, and the cancellation of an
/// aborted request are handled before any mapping is consulted.
/// </para>
/// </summary>
public sealed class ExceptionHandlingOptions
{
    private readonly Dictionary<Type, ExceptionMapping> mappings = [];

    /// <summary>
    /// Map <see cref="ArgumentException"/> (including <see cref="ArgumentNullException"/> and
    /// <see cref="ArgumentOutOfRangeException"/>) to 400, <see cref="KeyNotFoundException"/> to 404 and
    /// <see cref="UnauthorizedAccessException"/> to 401, as every earlier version did. Default <see langword="true"/>.
    /// <para>
    /// Consider turning this off. These exceptions are usually a server-side bug — a null connection string, a missing
    /// dictionary key, a file-system permission — so mapping them tells the caller they sent a bad request and keeps the
    /// failure out of 5xx monitoring. Handlers that mean 400, 404 or 403 can throw <see cref="BadRequestException"/>,
    /// <see cref="NotFoundException"/> or <see cref="ForbiddenException"/>. With this off they become 500s, logged at
    /// Error. The default is planned to change to <see langword="false"/> in 3.0.
    /// </para>
    /// </summary>
    public bool MapCommonExceptions { get; set; } = true;

    /// <summary>
    /// The level client errors (4xx) are logged at: an <see cref="HttpResponseException"/>, a framework
    /// <c>BadHttpRequestException</c>, or a mapped exception. Default <see cref="LogLevel.Information"/>.
    /// <para>
    /// They are logged without the exception object, so without a stack trace: a 404 or a failed validation is the
    /// caller's doing, and a stack trace per bad request fills the exception logs and buries the real failures. The
    /// route, status and messages are in the log line. Server errors (5xx) are always logged at Error, with the exception.
    /// </para>
    /// </summary>
    public LogLevel ClientErrorLogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Responds with <paramref name="statusCode"/> to <typeparamref name="TException"/> and the exceptions derived from it,
    /// with the same generic message an unhandled error gets (localized from <c>ErrorMessages</c>). The exception's own
    /// message is never sent: it can hold connection strings, SQL or file paths.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not an error status (400-599).</exception>
    public ExceptionHandlingOptions Map<TException>(HttpStatusCode statusCode) where TException : Exception
    {
        EnsureErrorStatus(statusCode);

        mappings[typeof(TException)] = new ExceptionMapping(
            _ => new ErrorResponse(statusCode, []),
            UseGenericMessage: true);

        return this;
    }

    /// <summary>
    /// Responds with <paramref name="statusCode"/> to <typeparamref name="TException"/> and the exceptions derived from it,
    /// with the message <paramref name="message"/> returns. That message is sent to the caller, so it must not expose internals.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not an error status (400-599).</exception>
    public ExceptionHandlingOptions Map<TException>(HttpStatusCode statusCode, Func<TException, string> message) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureErrorStatus(statusCode);

        mappings[typeof(TException)] = new ExceptionMapping(
            exception => new ErrorResponse(statusCode, [message((TException)exception)]),
            UseGenericMessage: false);

        return this;
    }

    /// <summary>
    /// Full control: <paramref name="map"/> returns the error response to write for a <typeparamref name="TException"/>,
    /// messages and field errors included — or <see langword="null"/> to decline, which leaves the exception to the mapping
    /// for its base type, or to the default handling. Use it to map only some exceptions of a type, such as a database
    /// exception that reports a unique-key violation.
    /// </summary>
    public ExceptionHandlingOptions Map<TException>(Func<TException, ErrorResponse?> map) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(map);

        mappings[typeof(TException)] = new ExceptionMapping(
            exception => map((TException)exception),
            UseGenericMessage: false);

        return this;
    }

    /// <summary>
    /// The response for <paramref name="exception"/> from the most specific mapping that accepts it: its own type first,
    /// then each base type. Mapping the same type again replaces the earlier mapping.
    /// </summary>
    /// <param name="exception">The exception to map.</param>
    /// <param name="response">The response to write.</param>
    /// <param name="useGenericMessage">Write the localized unknown-error message instead of <paramref name="response"/>'s.</param>
    internal bool TryMap(Exception exception, [NotNullWhen(true)] out ErrorResponse? response, out bool useGenericMessage)
    {
        if (mappings.Count > 0)
        {
            for (var type = exception.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                if (mappings.TryGetValue(type, out var mapping) && mapping.Map(exception) is { } mapped)
                {
                    response = mapped;
                    useGenericMessage = mapping.UseGenericMessage;
                    return true;
                }
            }
        }

        response = null;
        useGenericMessage = false;
        return false;
    }

    private static void EnsureErrorStatus(HttpStatusCode statusCode)
    {
        if ((int)statusCode is < 400 or > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Exceptions can only be mapped to an error status (400-599).");
    }

    private sealed record ExceptionMapping(Func<Exception, ErrorResponse?> Map, bool UseGenericMessage);
}
