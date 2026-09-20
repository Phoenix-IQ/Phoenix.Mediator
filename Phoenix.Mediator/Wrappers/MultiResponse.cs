namespace Phoenix.Mediator.Wrappers;

// The constructor parameters mirror the properties by name AND type, which is what System.Text.Json
// requires to deserialize a type that has no parameterless constructor: clients and integration tests
// need to read this type back, not only write it.
public class MultiResponse<T>(IReadOnlyList<T> data, int totalCount, int pageSize)
{
    private readonly IReadOnlyList<T> data = data ?? throw new ArgumentNullException(nameof(data));

    public IReadOnlyList<T> Data => data;
    public int TotalCount => totalCount;

    // Exposed so the constructor parameter has a matching property: System.Text.Json requires that for
    // every parameter, otherwise clients and tests can serialize this type but not deserialize it.
    public int PageSize => pageSize;

    public int PagesCount => pageSize <= 0
        ? 0
        : (int)Math.Ceiling((double)totalCount / pageSize);
}
