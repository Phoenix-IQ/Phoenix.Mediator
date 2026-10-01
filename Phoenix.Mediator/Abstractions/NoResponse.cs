namespace Phoenix.Mediator.Abstractions;

/// <summary>
/// The response type an <see cref="IRequestBehavior{TRequest}"/> sees for a request that has none (an
/// <see cref="IRequest"/>): <c>typeof(TResponse) == typeof(NoResponse)</c>. It carries no value; there is only
/// <see cref="Value"/>.
/// <para>
/// Named for what it means here rather than <c>Unit</c>, the usual name for such a type: MediatR, System.Reactive and
/// LanguageExt each declare a <c>Unit</c>, and a file importing one of them alongside this namespace would stop compiling.
/// </para>
/// </summary>
public readonly struct NoResponse : IEquatable<NoResponse>
{
    /// <summary>The only value of this type.</summary>
    public static readonly NoResponse Value;

    public bool Equals(NoResponse other) => true;

    public override bool Equals(object? obj) => obj is NoResponse;

    public override int GetHashCode() => 0;

    public override string ToString() => nameof(NoResponse);

    public static bool operator ==(NoResponse left, NoResponse right) => true;

    public static bool operator !=(NoResponse left, NoResponse right) => false;
}
