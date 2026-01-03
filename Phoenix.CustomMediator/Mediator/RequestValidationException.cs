using Phoenix.CustomMediator.Wrappers;

namespace Phoenix.CustomMediator.Mediator;

public sealed class RequestValidationException : Exception
{
    public RequestValidationException(ErrorsResponse errors)
        : base("Request validation failed.")
    {
        Errors = errors;
    }

    public ErrorsResponse Errors { get; }
}


