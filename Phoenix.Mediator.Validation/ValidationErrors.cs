using FluentValidation.Results;
using Phoenix.Mediator.Wrappers;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Phoenix.Mediator.Validation;

/// <summary>
/// Turns FluentValidation failures into the 400 error response: every message in <see cref="ErrorResponse.Errors"/>, and
/// the same messages grouped by field in <see cref="ErrorResponse.FieldErrors"/>. Shared by the validation behavior and
/// the <c>ValidationException</c> mapping, so a validator that runs in the pipeline and one a handler calls itself
/// (<c>ValidateAndThrowAsync</c>) produce the same body.
/// </summary>
internal static class ValidationErrors
{
    public static ErrorResponse ToErrorResponse(IEnumerable<ValidationFailure> failures)
    {
        var errors = new List<string>();
        var byField = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var failure in failures)
        {
            errors.Add(failure.ErrorMessage);

            // A rule on the whole request (RuleFor(x => x).Must(...), or context.AddFailure(message)) has no field
            // to put its message next to; it stays in the flat list only.
            if (string.IsNullOrEmpty(failure.PropertyName))
                continue;

            var field = ToClientFieldPath(failure.PropertyName);
            if (!byField.TryGetValue(field, out var messages))
                byField[field] = messages = [];

            messages.Add(failure.ErrorMessage);
        }

        return new ErrorResponse(HttpStatusCode.BadRequest, errors)
        {
            FieldErrors = byField.Count == 0
                ? null
                : byField.ToDictionary(static entry => entry.Key, static entry => entry.Value.ToArray(), StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// FluentValidation names a field by its C# path (<c>Address.City</c>, <c>Items[0].Name</c>); the client sent it in
    /// camelCase JSON (<c>address.city</c>, <c>items[0].name</c>), and needs that spelling to find the field in its form.
    /// Each segment is camel-cased the way the Minimal API JSON defaults name properties; indexers are kept as they are.
    /// </summary>
    internal static string ToClientFieldPath(string propertyPath)
    {
        var path = new StringBuilder(propertyPath.Length);

        foreach (var segment in propertyPath.Split('.'))
        {
            if (path.Length > 0)
                path.Append('.');

            var indexer = segment.IndexOf('[');
            var name = indexer < 0 ? segment : segment[..indexer];

            path.Append(JsonNamingPolicy.CamelCase.ConvertName(name));

            if (indexer >= 0)
                path.Append(segment, indexer, segment.Length - indexer);
        }

        return path.ToString();
    }
}
