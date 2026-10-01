using FluentValidation.Results;
using Phoenix.Mediator.Wrappers;
using System.Net;

namespace Phoenix.Mediator.Validation;

/// <summary>
/// Turns FluentValidation failures into the 400 error response, one message per failure in <see cref="ErrorResponse.Errors"/>.
/// Shared by the validation behavior and the <c>ValidationException</c> mapping, so a validator that runs in the pipeline
/// and one a handler calls itself (<c>ValidateAndThrowAsync</c>) produce the same body.
/// </summary>
internal static class ValidationErrors
{
    public static ErrorResponse ToErrorResponse(IEnumerable<ValidationFailure> failures)
    {
        return new ErrorResponse(HttpStatusCode.BadRequest, failures.Select(static failure => failure.ErrorMessage).ToList());
    }
}
