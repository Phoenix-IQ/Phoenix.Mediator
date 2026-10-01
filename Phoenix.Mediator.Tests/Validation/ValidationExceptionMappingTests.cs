using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Validation;
using Phoenix.Mediator.Web;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// A <see cref="ValidationException"/> a handler throws itself (<c>ValidateAndThrowAsync</c>) becomes the same 400 the
/// validation behavior produces, through the mapping <c>AddMediatorValidation</c> registers.
/// <para>
/// Handlers live inside <see cref="VxHost{TMarker}"/> so the other tests' handler scans never see them.
/// </para>
/// </summary>
public sealed class ValidationExceptionMappingTests
{
    // A handler that validates on its own throws FluentValidation's ValidationException. That used to reach the middleware
    // as an unhandled 500; AddMediatorValidation maps it to the same 400 the behavior produces.
    [Fact]
    public async Task HandlerCallingValidateAndThrowAsync_GetsTheSameBadRequestBody()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("vx/self-validating", Json("""{"email":"not-an-email"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("'Email' is not a valid email address.", body.GetProperty("errors")[0].GetString());
    }

    // new ValidationException("message") carries no failures, only its message. Mapped through the failures alone the
    // caller got an empty errors list, and with no failures and no stack trace in the log the message was lost everywhere.
    [Fact]
    public async Task MessageOnlyValidationException_GetsItsMessageInTheBody()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("vx/message-only", Json("""{"email":"taken@example.com"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Email is already registered.", body.GetProperty("errors")[0].GetString());
    }

    // The mapping is an ordinary ExceptionHandlingOptions mapping, so an app that wants another body for it maps it
    // itself, after AddMediatorValidation.
    [Fact]
    public async Task AppMappingForValidationException_ReplacesTheBuiltInOne()
    {
        await using var app = await StartAppAsync(static services => services.Configure<ExceptionHandlingOptions>(static options =>
            options.Map<ValidationException>(HttpStatusCode.UnprocessableEntity, static _ => "vx-custom")));
        using var client = app.GetTestClient();

        var response = await client.PostAsync("vx/self-validating", Json("""{"email":"not-an-email"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("vx-custom", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> StartAppAsync(Action<IServiceCollection>? configure = null)
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMediator().AddMediatorValidation();
        builder.Services.AddTransient<IRequestHandler<VxSelfValidatingCommand, string>, VxHost<object>.SelfValidatingHandler>();
        builder.Services.AddTransient<IRequestHandler<VxMessageOnlyCommand, string>, VxHost<object>.MessageOnlyHandler>();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UsePhoenixExceptionHandling();
        app.MapGroup("vx").Post("self-validating", (ISender sender, VxSelfValidatingCommand command, CancellationToken ct) => sender.Send(command, ct));
        app.MapGroup("vx").Post("message-only", (ISender sender, VxMessageOnlyCommand command, CancellationToken ct) => sender.Send(command, ct));
        await app.StartAsync();
        return app;
    }

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}

public sealed class VxSelfValidatingCommand : IRequest<string>
{
    public string Email { get; set; } = "";
}

public sealed class VxMessageOnlyCommand : IRequest<string>
{
    public string Email { get; set; } = "";
}

/// <summary>Kept out of every assembly scan: the tests register these by hand.</summary>
public static class VxHost<TMarker>
{
    public sealed class MessageOnlyHandler : IRequestHandler<VxMessageOnlyCommand, string>
    {
        public Task<string> Handle(VxMessageOnlyCommand request, CancellationToken cancellationToken)
            => throw new ValidationException("Email is already registered.");
    }

    public sealed class SelfValidatingHandler : IRequestHandler<VxSelfValidatingCommand, string>
    {
        private static readonly InlineValidator<VxSelfValidatingCommand> Validator = CreateValidator();

        public async Task<string> Handle(VxSelfValidatingCommand request, CancellationToken cancellationToken)
        {
            await Validator.ValidateAndThrowAsync(request, cancellationToken);
            return "valid";
        }

        private static InlineValidator<VxSelfValidatingCommand> CreateValidator()
        {
            var validator = new InlineValidator<VxSelfValidatingCommand>();
            validator.RuleFor(command => command.Email).EmailAddress();
            return validator;
        }
    }
}
