using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Web;
using System.Reflection;

namespace Phoenix.Mediator.Validation;

public static class ValidationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the FluentValidation pipeline behavior to the mediator and registers all
    /// validators found in the provided assemblies, public and internal alike. Call after <c>AddMediator(...)</c>.
    /// Registering this before <c>AddMediatorSentry()</c> makes validation run outside the
    /// Sentry span; register it after to run inside. Safe to call multiple times.
    /// <para>
    /// Passing no assemblies registers the behavior only. Nothing is then validated unless the app registers
    /// its <c>IValidator&lt;T&gt;</c> implementations itself, so pass the assemblies holding your validators.
    /// </para>
    /// <para>
    /// A validator that can never run — none found at all, unbound type parameters, no public constructor —
    /// is reported as a warning at host startup rather than failing silently. See
    /// <c>ValidatorRegistrationDiagnostics</c>.
    /// </para>
    /// </summary>
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

        var registry = GetOrCreateRegistry(services);

        if (assemblies.Length > 0)
        {
            var newAssemblies = registry.Add(assemblies.Distinct());
            foreach (var assembly in newAssemblies)
                // includeInternalTypes: handlers are discovered regardless of visibility, so validators
                // must be too. Otherwise an `internal sealed` validator is silently never registered and
                // its request is never validated.
                services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
        }

        return services;
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
