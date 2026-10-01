using System.Net;

namespace Phoenix.Mediator.Wrappers;

public record ErrorResponse(HttpStatusCode HttpStatusCode, IReadOnlyList<string> Errors)
{
    /// <summary>
    /// The messages per request field, keyed by the field's path as the client wrote it (<c>email</c>,
    /// <c>address.city</c>, <c>items[0].name</c>), so a form can show each message next to its field. The same messages
    /// are also in <see cref="Errors"/>. <see langword="null"/> when the errors are not about individual fields.
    /// </summary>
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; init; }
}
