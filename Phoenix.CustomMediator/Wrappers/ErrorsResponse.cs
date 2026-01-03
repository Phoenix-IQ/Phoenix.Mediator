namespace Phoenix.CustomMediator.Wrappers;

public record ErrorsResponse(int ErrorCode, IReadOnlyList<string> Errors);