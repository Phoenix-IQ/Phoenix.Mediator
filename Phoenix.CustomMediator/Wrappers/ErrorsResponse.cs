namespace Phoenix.CustomMediator.Wrappers;

public record ErrorsResponse(IReadOnlyList<string> Errors);