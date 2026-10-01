using System.Text.Json.Serialization;

namespace Phoenix.Mediator.Wrappers;

/// <summary>
/// Public API error body (matches { "errors": [...], "traceId": "..." }, plus "fieldErrors" for validation failures).
/// Status code is conveyed via HTTP status, not the JSON payload.
/// <para>
/// <paramref name="TraceId"/> correlates the response with logs/Sentry. It is optional so the type can
/// also describe bodies written outside a request, but the exception-handling middleware always sets it.
/// </para>
/// </summary>
public record ErrorsResponse(IReadOnlyList<string> Errors, string? TraceId = null)
{
    /// <summary>
    /// The messages per request field (see <see cref="ErrorResponse.FieldErrors"/>). Left out of the body entirely when
    /// there are none, so every other error keeps exactly the <c>errors</c> + <c>traceId</c> shape clients already read.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; init; }
}
