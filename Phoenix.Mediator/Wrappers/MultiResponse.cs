namespace Phoenix.Mediator.Wrappers;

public class MultiResponse<T>(List<T> data, int totalCount, int pageSize)
{
    private readonly List<T> data = data ?? throw new ArgumentNullException(nameof(data));

    public IReadOnlyList<T> Data => data;
    public int TotalCount => totalCount;
    public int PagesCount => pageSize <= 0
        ? 0
        : (int)Math.Ceiling((double)totalCount / pageSize);
}
