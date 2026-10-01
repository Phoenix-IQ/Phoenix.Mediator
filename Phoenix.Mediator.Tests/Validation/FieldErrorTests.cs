using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Exceptions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Validation;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Wrappers;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// Validation failures carry their messages per field (<c>fieldErrors</c>) as well as in the flat <c>errors</c> list, keyed
/// by the path the client wrote; and a <see cref="ValidationException"/> a handler throws itself becomes the same 400.
/// <para>
/// Validators live inside <see cref="FeHost{TMarker}"/> so the other tests' validator scans never see them.
/// </para>
/// </summary>
public sealed class FieldErrorTests
{
    // FluentValidation names fields by their C# path; a client finds them by the camelCase JSON name it sent.
    [Fact]
    public async Task ValidationFailure_KeysEachMessageByTheFieldPathTheClientWrote()
    {
        var exception = await SendInvalidAsync(new FeStudentCommand
        {
            FullName = "",
            Address = new FeAddress { City = "" },
            Courses = [new FeCourse { Code = "" }, new FeCourse { Code = "MATH-101" }, new FeCourse { Code = "" }]
        });

        var fieldErrors = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[]>>(exception.FieldErrors);
        Assert.Equal(new[] { "address.city", "courses[0].code", "courses[2].code", "fullName" }, fieldErrors.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "'Full Name' must not be empty." }, fieldErrors["fullName"]);
    }

    // The flat list is what every existing client reads; it must keep every message, in order.
    [Fact]
    public async Task ValidationFailure_KeepsEveryMessageInTheFlatListToo()
    {
        var exception = await SendInvalidAsync(new FeStudentCommand { FullName = "", Address = new FeAddress { City = "" } });

        Assert.Equal(2, exception.Errors.Count);
        Assert.Equal(exception.FieldErrors!.Values.SelectMany(static messages => messages).Order(), exception.Errors.Order());
    }

    // Two rules on one field produce two messages under one key, in rule order.
    [Fact]
    public async Task ValidationFailure_SeveralMessagesForOneField_StayTogetherInOrder()
    {
        var exception = await SendInvalidAsync(new FeStudentCommand { FullName = "x", Address = new FeAddress { City = "Baghdad" } });

        Assert.Equal(new[] { "Too short.", "Must contain a space." }, exception.FieldErrors!["fullName"]);
    }

    // A rule on the request as a whole has no field to show its message next to: it stays in the flat list only.
    [Fact]
    public async Task ValidationFailure_OnTheWholeRequest_IsLeftOutOfTheFieldErrors()
    {
        var exception = await SendInvalidAsync(new FeStudentCommand { FullName = "Ada Lovelace", Address = new FeAddress { City = "Baghdad" }, Reject = true });

        Assert.Equal(new[] { "The request as a whole is rejected." }, exception.Errors);
        Assert.Null(exception.FieldErrors);
    }

    // End to end: the body a client receives.
    [Fact]
    public async Task ValidationFailure_OverHttp_ReturnsFieldErrorsInTheBody()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("fe/students", Json("""{"fullName":"","address":{"city":"Baghdad"}}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("'Full Name' must not be empty.", body.GetProperty("fieldErrors").GetProperty("fullName")[0].GetString());
        Assert.Equal("'Full Name' must not be empty.", body.GetProperty("errors")[0].GetString());
    }

    // A handler that validates on its own throws FluentValidation's ValidationException. That used to reach the middleware
    // as an unhandled 500; AddMediatorValidation maps it to the same 400 the behavior produces.
    [Fact]
    public async Task HandlerCallingValidateAndThrowAsync_GetsTheSameBadRequestBody()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("fe/self-validating", Json("""{"email":"not-an-email"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("'Email' is not a valid email address.", body.GetProperty("fieldErrors").GetProperty("email")[0].GetString());
    }

    // new ValidationException("message") carries no failures, only its message. Mapped through the failures alone the
    // caller got an empty errors list, and with no failures and no stack trace in the log the message was lost everywhere.
    [Fact]
    public async Task MessageOnlyValidationException_GetsItsMessageInTheBody()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("fe/message-only", Json("""{"email":"taken@example.com"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Email is already registered.", body.GetProperty("errors")[0].GetString());
        Assert.False(body.TryGetProperty("fieldErrors", out _));
    }

    // The mapping is an ordinary ExceptionHandlingOptions mapping, so an app that wants another body for it maps it
    // itself, after AddMediatorValidation.
    [Fact]
    public async Task AppMappingForValidationException_ReplacesTheBuiltInOne()
    {
        await using var app = await StartAppAsync(static services => services.Configure<ExceptionHandlingOptions>(static options =>
            options.Map<ValidationException>(HttpStatusCode.UnprocessableEntity, static _ => "fe-custom")));
        using var client = app.GetTestClient();

        var response = await client.PostAsync("fe/self-validating", Json("""{"email":"not-an-email"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("fe-custom", await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseException> SendInvalidAsync(FeStudentCommand command)
    {
        var services = new ServiceCollection();
        services.AddMediator().AddMediatorValidation();
        services.AddTransient<IRequestHandler<FeStudentCommand, string>, FeHost<object>.StudentHandler>();
        services.AddTransient<IValidator<FeStudentCommand>, FeHost<object>.StudentValidator>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        return await Assert.ThrowsAsync<HttpResponseException>(
            () => scope.ServiceProvider.GetRequiredService<ISender>().Send(command));
    }

    private static async Task<WebApplication> StartAppAsync(Action<IServiceCollection>? configure = null)
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMediator().AddMediatorValidation();
        builder.Services.AddTransient<IRequestHandler<FeStudentCommand, string>, FeHost<object>.StudentHandler>();
        builder.Services.AddTransient<IRequestHandler<FeSelfValidatingCommand, string>, FeHost<object>.SelfValidatingHandler>();
        builder.Services.AddTransient<IRequestHandler<FeMessageOnlyCommand, string>, FeHost<object>.MessageOnlyHandler>();
        builder.Services.AddTransient<IValidator<FeStudentCommand>, FeHost<object>.StudentValidator>();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UsePhoenixExceptionHandling();
        app.MapGroup("fe").Post("students", (ISender sender, FeStudentCommand command, CancellationToken ct) => sender.SendAsApiResult(command, ct));
        app.MapGroup("fe").Post("self-validating", (ISender sender, FeSelfValidatingCommand command, CancellationToken ct) => sender.SendAsApiResult(command, ct));
        app.MapGroup("fe").Post("message-only", (ISender sender, FeMessageOnlyCommand command, CancellationToken ct) => sender.SendAsApiResult(command, ct));
        await app.StartAsync();
        return app;
    }

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}

public sealed class FeStudentCommand : IRequest<string>
{
    public string FullName { get; set; } = "";
    public FeAddress? Address { get; set; }
    public List<FeCourse> Courses { get; set; } = [];
    public bool Reject { get; set; }
}

public sealed class FeAddress
{
    public string City { get; set; } = "";
}

public sealed class FeCourse
{
    public string Code { get; set; } = "";
}

public sealed class FeSelfValidatingCommand : IRequest<string>
{
    public string Email { get; set; } = "";
}

public sealed class FeMessageOnlyCommand : IRequest<string>
{
    public string Email { get; set; } = "";
}

/// <summary>Kept out of every assembly scan: the tests register these by hand.</summary>
public static class FeHost<TMarker>
{
    public sealed class StudentHandler : IRequestHandler<FeStudentCommand, string>
    {
        public Task<string> Handle(FeStudentCommand request, CancellationToken cancellationToken) => Task.FromResult("created");
    }

    public sealed class StudentValidator : AbstractValidator<FeStudentCommand>
    {
        public StudentValidator()
        {
            RuleFor(command => command.FullName).NotEmpty();
            RuleFor(command => command.FullName).MinimumLength(3).WithMessage("Too short.").When(command => command.FullName.Length > 0);
            RuleFor(command => command.FullName).Must(name => name.Contains(' ')).WithMessage("Must contain a space.").When(command => command.FullName.Length > 0);
            RuleFor(command => command.Address!.City).NotEmpty().When(command => command.Address is not null);
            RuleForEach(command => command.Courses).ChildRules(course => course.RuleFor(c => c.Code).NotEmpty());
            RuleFor(command => command).Must(command => !command.Reject).WithMessage("The request as a whole is rejected.");
        }
    }

    public sealed class MessageOnlyHandler : IRequestHandler<FeMessageOnlyCommand, string>
    {
        public Task<string> Handle(FeMessageOnlyCommand request, CancellationToken cancellationToken)
            => throw new ValidationException("Email is already registered.");
    }

    public sealed class SelfValidatingHandler : IRequestHandler<FeSelfValidatingCommand, string>
    {
        private static readonly InlineValidator<FeSelfValidatingCommand> Validator = CreateValidator();

        public async Task<string> Handle(FeSelfValidatingCommand request, CancellationToken cancellationToken)
        {
            await Validator.ValidateAndThrowAsync(request, cancellationToken);
            return "valid";
        }

        private static InlineValidator<FeSelfValidatingCommand> CreateValidator()
        {
            var validator = new InlineValidator<FeSelfValidatingCommand>();
            validator.RuleFor(command => command.Email).EmailAddress();
            return validator;
        }
    }
}
