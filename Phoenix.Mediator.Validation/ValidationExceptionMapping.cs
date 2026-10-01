using FluentValidation;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Validation;

/// <summary>
/// Maps FluentValidation's <see cref="ValidationException"/> to the same 400 body the validation behavior produces.
/// A handler that validates on its own (<c>await validator.ValidateAndThrowAsync(command)</c>) throws this exception,
/// which used to reach the exception middleware as an unhandled 500. Registered by <c>AddMediatorValidation</c>; an
/// app that maps <see cref="ValidationException"/> itself, after that call, replaces this mapping.
/// </summary>
internal sealed class ValidationExceptionMapping : IConfigureOptions<ExceptionHandlingOptions>
{
    public void Configure(ExceptionHandlingOptions options)
    {
        // new ValidationException("Email is already registered.") carries its message and no failures. Mapped through the
        // failures alone, the caller would get an empty errors list and the message would be lost everywhere.
        options.Map<ValidationException>(static exception => exception.Errors?.Any() == true
            ? ValidationErrors.ToErrorResponse(exception.Errors)
            : new ErrorResponse(HttpStatusCode.BadRequest, [exception.Message]));
    }
}
