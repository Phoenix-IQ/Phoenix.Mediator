using System.Reflection;

namespace Phoenix.Mediator.Mediator;

internal static class AssemblyTypeLoader
{
    /// <summary>
    /// Returns the types in <paramref name="assembly"/> that could be loaded. When some fail to load
    /// (e.g. they reference an assembly that isn't deployed), the loadable ones are still returned and
    /// the failure is passed to <paramref name="onPartialLoad"/> instead of being thrown.
    /// </summary>
    public static IEnumerable<Type> GetLoadableTypes(Assembly assembly, Action<ReflectionTypeLoadException>? onPartialLoad = null)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            onPartialLoad?.Invoke(ex);
            return ex.Types.Where(static type => type is not null)!;
        }
    }
}
