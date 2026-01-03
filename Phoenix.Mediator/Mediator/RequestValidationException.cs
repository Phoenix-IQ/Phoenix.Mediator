using Phoenix.Mediator.Wrappers;

namespace Phoenix.Mediator.Mediator;

public sealed class RequestValidationException(ErrorsResponse errors) : Exception("Request validation failed.")
{
    public ErrorsResponse Errors { get; } = errors;
}


