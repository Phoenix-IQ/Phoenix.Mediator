using Phoenix.CustomMediator.Wrappers;

namespace Phoenix.CustomMediator.Mediator;

public sealed class RequestValidationException(ErrorsResponse errors) : Exception("Request validation failed.")
{
    public ErrorsResponse Errors { get; } = errors;
}


