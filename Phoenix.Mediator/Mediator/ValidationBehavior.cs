using FluentValidation;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Wrappers;

namespace Phoenix.Mediator.Mediator;

public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid)
                errors.AddRange(result.Errors.Select(e => e.ErrorMessage));
        }

        if (errors.Count > 0)
            throw new RequestValidationException(new ErrorsResponse(errors));

        return await next().ConfigureAwait(false);
    }
}

public sealed class ValidationBehavior<TRequest>(IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest> where TRequest : IRequest
{
    public async Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid)
                errors.AddRange(result.Errors.Select(e => e.ErrorMessage));
        }

        if (errors.Count > 0)
            throw new RequestValidationException(new ErrorsResponse(errors));

        await next().ConfigureAwait(false);
    }
}


