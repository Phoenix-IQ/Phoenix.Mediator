using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Validation;
using Phoenix.Mediator.Wrappers;
using System.Reflection;
using System.Reflection.Emit;
using Phoenix.Mediator.Tests.Infrastructure;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// A validator that is never registered fails silently: the behavior resolves an empty validator list,
/// finds no failures and lets the request through, so invalid input is accepted as if it had been checked.
/// These cover the startup warnings that make each such case visible.
/// </summary>
public sealed class ValidatorDiagnosticsTests
{
    // Neither AddMediator nor AddMediatorValidation got an assembly, so nothing can be scanned for validators.
    [Fact]
    public async Task WarnsWhenNoAssemblyIsGivenAnywhere()
    {
        var warnings = await WarningsFor(static services => services.AddMediator().AddMediatorValidation());

        Assert.Contains(warnings, warning => warning.Contains("no assemblies to scan", StringComparison.Ordinal));
    }

    // AddMediatorValidation() scans the assemblies given to AddMediator, so validators found there count.
    [Fact]
    public async Task DoesNotWarnWhenTheValidatorsAreInAnAssemblyGivenToAddMediator()
    {
        var assembly = new FakeAssembly("App", typeof(DiagnosticsRequest), typeof(DiagnosticsHandler), typeof(DiagnosticsValidator));

        var warnings = await WarningsFor(services => services.AddMediator(assembly).AddMediatorValidation());

        Assert.DoesNotContain(warnings, warning => warning.Contains("passes validation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WarnsWhenScannedAssembliesHoldNoValidators()
    {
        var assembly = new FakeAssembly("HandlersOnly", typeof(DiagnosticsRequest), typeof(DiagnosticsHandler));

        var warnings = await WarningsFor(services => services.AddMediatorValidation(assembly));

        var warning = Assert.Single(warnings, w => w.Contains("no FluentValidation validators", StringComparison.Ordinal));
        Assert.Contains("HandlersOnly", warning, StringComparison.Ordinal);
    }

    // No assemblies anywhere is a supported way to use the behavior with hand-registered validators, so it must not
    // be reported as "nothing is validated".
    [Fact]
    public async Task DoesNotWarnWhenValidatorsAreRegisteredByHand()
    {
        var warnings = await WarningsFor(services =>
        {
            services.AddMediator().AddMediatorValidation();
            services.AddScoped<IValidator<DiagnosticsRequest>, DiagnosticsValidator>();
        });

        Assert.DoesNotContain(warnings, warning => warning.Contains("passes validation", StringComparison.Ordinal));
    }

    // A validator nested in a generic type inherits that type's parameters, so it stays an open generic and
    // the scan skips it. This is the one visibility-shaped case that really is silent.
    [Fact]
    public async Task WarnsAboutValidatorNestedInGenericType()
    {
        var nested = typeof(DiagnosticsGenericHost<>).GetNestedTypes(BindingFlags.NonPublic).Single();
        var assembly = new FakeAssembly("GenericHost", nested);

        var warnings = await WarningsFor(services => services.AddMediatorValidation(assembly));

        var warning = Assert.Single(warnings, w => w.Contains("unbound type parameters", StringComparison.Ordinal));
        Assert.Contains("DiagnosticsGenericHost", warning, StringComparison.Ordinal);
    }

    // Registered, but the container has no public constructor to call: without this warning the only signal is
    // "A suitable constructor ... could not be located" thrown by the first request that uses it.
    [Fact]
    public async Task WarnsAboutValidatorWithoutPublicConstructor()
    {
        var assembly = new FakeAssembly("PrivateCtor", EmitValidatorWithPrivateConstructor());

        var warnings = await WarningsFor(services => services.AddMediatorValidation(assembly));

        Assert.Contains(warnings, warning => warning.Contains("no public constructor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNotWarnWhenValidatorsAreDiscovered()
    {
        var assembly = new FakeAssembly("WithValidators", typeof(DiagnosticsValidator));

        var warnings = await WarningsFor(services => services.AddMediatorValidation(assembly));

        Assert.Empty(warnings);
    }

    // TryAddEnumerable dedupes by implementation type, so composing the package twice must not double the noise.
    [Fact]
    public async Task ReportsOnceWhenRegisteredRepeatedly()
    {
        var warnings = await WarningsFor(static services =>
        {
            services.AddMediator();
            services.AddMediatorValidation();
            services.AddMediatorValidation();
        });

        Assert.Single(warnings, warning => warning.Contains("no assemblies to scan", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<string>> WarningsFor(Action<IServiceCollection> configure)
    {
        var collector = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(collector));
        configure(services);

        await using var provider = services.BuildServiceProvider(validateScopes: true);

        foreach (var hosted in provider.GetServices<IHostedService>()
            .Where(static service => service.GetType().Namespace == typeof(ValidationBehavior<>).Namespace))
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        return collector.Warnings.Select(static entry => entry.Message).ToArray();
    }

    /// <summary>
    /// Emitted rather than declared in this assembly: a validator with no public constructor that the real
    /// assembly scan could see would break every test that builds a Development-mode host, because
    /// <c>ValidateOnBuild</c> tries to construct it.
    /// </summary>
    private static Type EmitValidatorWithPrivateConstructor()
    {
        var baseType = typeof(AbstractValidator<DiagnosticsRequest>);
        var module = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("Phoenix.Mediator.Tests.Emitted"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Main");

        var type = module.DefineType("PrivateCtorValidator", TypeAttributes.Public | TypeAttributes.Sealed, baseType);
        var il = type.DefineConstructor(MethodAttributes.Private, CallingConventions.Standard, Type.EmptyTypes).GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        // AbstractValidator<T> is abstract, so its parameterless constructor is protected, not public.
        il.Emit(OpCodes.Call, baseType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, Type.EmptyTypes, modifiers: null)!);
        il.Emit(OpCodes.Ret);

        return type.CreateType()!;
    }

}

public sealed class DiagnosticsRequest : IRequest<SingleResponse<string>>
{
    public string Name { get; set; } = string.Empty;
}

public sealed class DiagnosticsHandler : IRequestHandler<DiagnosticsRequest, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(DiagnosticsRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SingleResponse<string>(request.Name));
}

public sealed class DiagnosticsValidator : AbstractValidator<DiagnosticsRequest>
{
    public DiagnosticsValidator() => RuleFor(request => request.Name).NotEmpty();
}

// Never registered by design: the nested validator inherits THost, so it stays an open generic.
public sealed class DiagnosticsGenericHost<THost>
{
    private sealed class Validator : AbstractValidator<DiagnosticsRequest>
    {
        public Validator() => RuleFor(request => request.Name).NotEmpty();
    }
}
