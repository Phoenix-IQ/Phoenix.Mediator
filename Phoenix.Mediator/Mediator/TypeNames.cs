namespace Phoenix.Mediator.Mediator;

/// <summary>
/// Type names written the way they appear in C# (<c>IRequestHandler&lt;GetOrder, OrderDto&gt;</c>) rather than the
/// way reflection prints them (<c>IRequestHandler`2[GetOrder,OrderDto]</c>), for messages a developer has to act on.
/// </summary>
internal static class TypeNames
{
    public static string Display(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var arity = name.IndexOf('`');
        if (arity >= 0)
            name = name[..arity];

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Display))}>";
    }
}
