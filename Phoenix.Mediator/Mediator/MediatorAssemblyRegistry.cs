using System.Reflection;

namespace Phoenix.Mediator.Mediator;

internal sealed class MediatorAssemblyRegistry
{
    private readonly object gate = new();
    private readonly HashSet<Assembly> assemblies = [];

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
            return assemblies.ToArray();
        }
    }
}
