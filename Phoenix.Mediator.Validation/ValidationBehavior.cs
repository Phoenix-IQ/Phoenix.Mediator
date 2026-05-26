using FluentValidation;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Validation;

public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        await ValidationGuard.EnsureValidAsync(validators, request, cancellationToken).ConfigureAwait(false);
        return await next().ConfigureAwait(false);
    }
}

public sealed class ValidationBehavior<TRequest>(IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest> where TRequest : IRequest
{
    public async Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        await ValidationGuard.EnsureValidAsync(validators, request, cancellationToken).ConfigureAwait(false);
        await next().ConfigureAwait(false);
    }
}

internal static class ValidationGuard
{
    /// <summary>
    /// Runs every validator for the request and throws a 400 <see cref="HttpResponseException"/>
    /// aggregating all failure messages. No-op when there are no validators or no failures.
    /// </summary>
    public static async Task EnsureValidAsync<TRequest>(IEnumerable<IValidator<TRequest>> validators, TRequest request, CancellationToken cancellationToken)
    {
        List<string>? errors = null;

        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsValid)
                continue;

            errors ??= [];
            errors.AddRange(result.Errors.Select(static e => e.ErrorMessage));
        }

        if (errors is { Count: > 0 })
            throw new HttpResponseException(new ErrorResponse(HttpStatusCode.BadRequest, errors));
    }
}
