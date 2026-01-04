namespace Phoenix.Mediator.Wrappers;

/// <summary>
/// Public API error body (matches { "errors": [...] }).
/// Status code is conveyed via HTTP status, not the JSON payload.
/// </summary>
public record ErrorsResponse(IReadOnlyList<string> Errors);


