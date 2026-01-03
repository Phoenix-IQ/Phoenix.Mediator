using FluentValidation;
using Phoenix.CustomMediator.Abstractions;
using Phoenix.CustomMediator.Wrappers;

namespace Phoenix.CustomMediator.Mediator;

public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IRequestValidator<TRequest>> _validators;
    private readonly IEnumerable<IValidator<TRequest>> _fluentValidators;

    public ValidationBehavior(IEnumerable<IRequestValidator<TRequest>> validators, IEnumerable<IValidator<TRequest>> fluentValidators)
    {
        _validators = validators;
        _fluentValidators = fluentValidators;
    }

    public async Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate<TResponse> next)
    {
        var errors = new List<string>();
        int? code = null;

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result is { Count: > 0 })
                errors.AddRange(result);
            code ??= validator.ErrorCode;
        }

        foreach (var validator in _fluentValidators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid)
                errors.AddRange(result.Errors.Select(e => e.ErrorMessage));
        }

        if (errors.Count > 0)
            throw new RequestValidationException(new ErrorsResponse(code ?? 400, errors));

        return await next().ConfigureAwait(false);
    }
}

public sealed class ValidationBehavior<TRequest> : IPipelineBehavior<TRequest>
    where TRequest : IRequest
{
    private readonly IEnumerable<IRequestValidator<TRequest>> _validators;
    private readonly IEnumerable<IValidator<TRequest>> _fluentValidators;

    public ValidationBehavior(IEnumerable<IRequestValidator<TRequest>> validators, IEnumerable<IValidator<TRequest>> fluentValidators)
    {
        _validators = validators;
        _fluentValidators = fluentValidators;
    }

    public async Task Handle(TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate next)
    {
        var errors = new List<string>();
        int? code = null;

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result is { Count: > 0 })
                errors.AddRange(result);
            code ??= validator.ErrorCode;
        }

        foreach (var validator in _fluentValidators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid)
                errors.AddRange(result.Errors.Select(e => e.ErrorMessage));
        }

        if (errors.Count > 0)
            throw new RequestValidationException(new ErrorsResponse(code ?? 400, errors));

        await next().ConfigureAwait(false);
    }
}


