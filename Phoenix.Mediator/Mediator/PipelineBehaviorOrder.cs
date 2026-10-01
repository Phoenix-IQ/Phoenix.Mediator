using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Mediator.Abstractions;

namespace Phoenix.Mediator.Mediator;

/// <summary>
/// Where each pipeline behavior was registered, so behaviors of the two shapes —
/// <see cref="IPipelineBehavior{TRequest, TResponse}"/> / <see cref="IPipelineBehavior{TRequest}"/> and
/// <see cref="IRequestBehavior{TRequest}"/> — still run in one registration order. The container returns the
/// instances of each service type in the order they were registered, but has no order across service types.
/// <para>
/// This reads the live <see cref="IServiceCollection"/>, the way the validation package's diagnostics do: behaviors
/// are registered after <c>AddMediator</c>, so positions can only be read once the collection is complete, which it
/// is by the first send.
/// </para>
/// </summary>
internal sealed class PipelineBehaviorOrder(IServiceCollection services)
{
    private readonly ConcurrentDictionary<Type, int[]> positionsByService = new();
    private TypePositions? typePositions;

    /// <summary>
    /// The registration position of every registration that yields an instance of <paramref name="closedServiceType"/>,
    /// in the order the container returns those instances: its exact registrations, and the open generic ones the
    /// container can close over the same type arguments. Microsoft.Extensions.DependencyInjection returns them in
    /// registration order, so the k-th instance comes from the k-th position — factory registrations included.
    /// </summary>
    public int[] PositionsFor(Type closedServiceType) => positionsByService.GetOrAdd(closedServiceType, ReadPositions);

    /// <summary>
    /// The position of the registration that produced <paramref name="behavior"/>, judged by its type, or
    /// <see cref="int.MaxValue"/> when that cannot be told (a factory registration). The fallback for when the container
    /// returned instances the service collection does not account for, such as a third-party container's own registrations.
    /// </summary>
    public int PositionOf(object behavior, bool isRequestBehavior)
    {
        var positions = typePositions ??= TypePositions.Read(services);
        var byImplementation = isRequestBehavior ? positions.RequestBehaviors : positions.PipelineBehaviors;
        var type = behavior.GetType();

        if (byImplementation.TryGetValue(type, out var position))
            return position;

        return type.IsGenericType && byImplementation.TryGetValue(type.GetGenericTypeDefinition(), out position)
            ? position
            : int.MaxValue;
    }

    private int[] ReadPositions(Type closedServiceType)
    {
        var definition = closedServiceType.GetGenericTypeDefinition();
        var arguments = closedServiceType.GenericTypeArguments;
        var positions = new List<int>();

        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];

            // Keyed registrations never reach a mediator pipeline, and reading ImplementationType off one throws.
            if (descriptor.IsKeyedService)
                continue;

            if (descriptor.ServiceType == closedServiceType
                || (descriptor.ServiceType == definition && CanClose(descriptor.ImplementationType, arguments)))
            {
                positions.Add(index);
            }
        }

        return [.. positions];
    }

    private static bool CanClose(Type? openImplementation, Type[] arguments)
    {
        if (openImplementation is not { IsGenericTypeDefinition: true })
            return false;

        try
        {
            openImplementation.MakeGenericType(arguments);
            return true;
        }
        catch (ArgumentException)
        {
            // A constraint these arguments don't satisfy: the container skips the registration for them too.
            return false;
        }
    }

    // Two threads can race to build this; both produce the same positions from the same (by now complete)
    // collection, and publishing either one is fine.
    private sealed class TypePositions
    {
        public Dictionary<Type, int> PipelineBehaviors { get; } = [];

        public Dictionary<Type, int> RequestBehaviors { get; } = [];

        public static TypePositions Read(IServiceCollection services)
        {
            var positions = new TypePositions();

            for (var index = 0; index < services.Count; index++)
            {
                var descriptor = services[index];
                if (descriptor.IsKeyedService || !descriptor.ServiceType.IsGenericType)
                    continue;

                var service = descriptor.ServiceType.GetGenericTypeDefinition();
                var isRequestBehavior = service == typeof(IRequestBehavior<>);

                if (!isRequestBehavior && service != typeof(IPipelineBehavior<,>) && service != typeof(IPipelineBehavior<>))
                    continue;

                // A factory registration has no type to key on.
                var implementation = descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
                if (implementation is null)
                    continue;

                // First registration wins: registering the same type again does not move it.
                (isRequestBehavior ? positions.RequestBehaviors : positions.PipelineBehaviors).TryAdd(implementation, index);
            }

            return positions;
        }
    }
}
