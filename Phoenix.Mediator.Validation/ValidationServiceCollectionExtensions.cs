using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Web;
using System.Reflection;

namespace Phoenix.Mediator.Validation;

public static class ValidationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the FluentValidation pipeline behavior to the mediator and registers the validators, public and internal alike,
    /// in every assembly given to <c>AddMediator(...)</c> — before this call or after it — and in
    /// <paramref name="assemblies"/>. Validators usually sit next to their handlers, so
    /// <c>AddMediator(assembly).AddMediatorValidation()</c> is enough; pass assemblies here only for validators kept
    /// somewhere else. Registering this before <c>AddMediatorSentry()</c> makes validation run outside the Sentry span;
    /// register it after to run inside. Safe to call multiple times: each assembly is scanned once.
    /// <para>
    /// A validator that can never run — none found at all, unbound type parameters, no public constructor —
    /// is reported as a warning at host startup rather than failing silently. See
    /// <c>ValidatorRegistrationDiagnostics</c>.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">More assemblies to scan, for validators outside the assemblies given to <c>AddMediator(...)</c>.</param>
    public static IServiceCollection AddMediatorValidation(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<>), typeof(ValidationBehavior<>)));
        // Deduplicated by implementation type, so repeated AddMediatorValidation calls report once.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ValidatorRegistrationDiagnostics>());
        // A handler calling ValidateAndThrowAsync itself gets the same 400 body as the behavior, instead of a 500.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<ExceptionHandlingOptions>, ValidationExceptionMapping>());

        // The startup diagnostics read the registry even when nothing gets scanned.
        GetOrCreateRegistry(services);

        // Validators usually sit next to their handlers: scan every assembly AddMediator(...) gets, including the ones a
        // later AddMediator(...) call adds.
        services.OnMediatorAssemblies(static (collection, mediatorAssemblies) => AddValidators(collection, mediatorAssemblies));

        if (assemblies.Length > 0)
            AddValidators(services, assemblies);

        return services;
    }

    private static void AddValidators(IServiceCollection services, IEnumerable<Assembly> assemblies)
    {
        // The registry hands back only the assemblies not scanned yet, so one given to both AddMediator(...) and
        // AddMediatorValidation(...) is scanned once.
        foreach (var assembly in GetOrCreateRegistry(services).Add(assemblies.Distinct()))
            // includeInternalTypes: handlers are discovered regardless of visibility, so validators
            // must be too. Otherwise an `internal sealed` validator is silently never registered and
            // its request is never validated.
            services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
    }

    private static ValidatorAssemblyRegistry GetOrCreateRegistry(IServiceCollection services)
    {
        var existing = services
            .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(ValidatorAssemblyRegistry))
            ?.ImplementationInstance as ValidatorAssemblyRegistry;

        if (existing is not null)
            return existing;

        var registry = new ValidatorAssemblyRegistry(services);
        services.AddSingleton(registry);
        return registry;
    }
}
