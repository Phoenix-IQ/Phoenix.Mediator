using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Phoenix.Mediator.Mediator;

internal sealed class MediatorAssemblyRegistry
{
    private readonly object gate = new();
    private readonly HashSet<Assembly> assemblies = [];
    private readonly List<Action<IServiceCollection, IReadOnlyList<Assembly>>> subscribers = [];

    public Assembly[] AddAssemblies(IEnumerable<Assembly> candidateAssemblies)
    {
        ArgumentNullException.ThrowIfNull(candidateAssemblies);

        lock (gate)
        {
            var addedAssemblies = new List<Assembly>();

            foreach (var assembly in candidateAssemblies.Where(static assembly => assembly is not null))
            {
                if (assemblies.Add(assembly))
                    addedAssemblies.Add(assembly);
            }

            return addedAssemblies.ToArray();
        }
    }

    public Assembly[] GetAssemblies()
    {
        lock (gate)
        {
            return [.. assemblies];
        }
    }

    /// <summary>Adds <paramref name="subscriber"/> unless it is already subscribed; returns whether it was added.</summary>
    public bool Subscribe(Action<IServiceCollection, IReadOnlyList<Assembly>> subscriber)
    {
        lock (gate)
        {
            if (subscribers.Contains(subscriber))
                return false;

            subscribers.Add(subscriber);
            return true;
        }
    }

    public Action<IServiceCollection, IReadOnlyList<Assembly>>[] GetSubscribers()
    {
        lock (gate)
        {
            return [.. subscribers];
        }
    }
}
