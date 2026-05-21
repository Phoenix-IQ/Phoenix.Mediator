namespace Phoenix.Mediator.Mediator;

public sealed class MediatorOptions
{
    public EmptyResponseStatusCode EmptyResponseStatusCode { get; set; } = EmptyResponseStatusCode.NoContent;
}

public enum EmptyResponseStatusCode
{
    Ok = 200,
    NoContent = 204
}

internal interface IMediatorOptionsAccessor
{
    MediatorOptions Options { get; }
}
