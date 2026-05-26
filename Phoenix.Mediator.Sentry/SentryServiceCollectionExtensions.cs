using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Phoenix.Mediator.Abstractions;

namespace Phoenix.Mediator.Sentry;

public static class SentryServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Sentry tracing/error-capture pipeline behavior to the mediator. Call after
    /// <c>AddMediator(...)</c>; register before <c>AddMediatorValidation(...)</c> to make the
    /// Sentry span wrap validation. Safe to call multiple times.
    /// <para>
    /// The behavior consumes an optional <c>IHub</c>: when Sentry is configured (e.g. via
    /// <c>Sentry.AspNetCore</c>'s <c>UseSentry()</c>), an <c>IHub</c> is registered and injected;
    /// when it is not, the behavior is a no-op pass-through. No hub is force-registered here so the
    /// behavior never traces against an unconfigured SDK.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMediatorSentry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(SentryBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<>), typeof(SentryBehavior<>)));

        return services;
    }
}
