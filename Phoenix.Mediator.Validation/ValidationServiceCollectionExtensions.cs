using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Phoenix.Mediator.Abstractions;
using System.Reflection;

namespace Phoenix.Mediator.Validation;

public static class ValidationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the FluentValidation pipeline behavior to the mediator and registers all
    /// validators found in the provided assemblies. Call after <c>AddMediator(...)</c>.
    /// Registering this before <c>AddMediatorSentry()</c> makes validation run outside the
    /// Sentry span; register it after to run inside. Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddMediatorValidation(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<>), typeof(ValidationBehavior<>)));

        if (assemblies.Length > 0)
        {
            var newAssemblies = GetOrCreateRegistry(services).Add(assemblies.Distinct());
            foreach (var assembly in newAssemblies)
                services.AddValidatorsFromAssembly(assembly);
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

        var registry = new ValidatorAssemblyRegistry();
        services.AddSingleton(registry);
        return registry;
    }

    private sealed class ValidatorAssemblyRegistry
    {
        private readonly object gate = new();
        private readonly HashSet<Assembly> assemblies = [];

        public Assembly[] Add(IEnumerable<Assembly> candidates)
        {
            lock (gate)
            {
                var added = new List<Assembly>();
                foreach (var assembly in candidates.Where(static a => a is not null))
                {
                    if (assemblies.Add(assembly))
                        added.Add(assembly);
                }

                return added.ToArray();
            }
        }
    }
}
