namespace Phoenix.Mediator.Wrappers;

public class MultiResponse<T>(List<T> data, int totalCount, int pageSize)
{
    public List<T> Data => data;
    public int TotalCount => totalCount;
    public int PagesCount => pageSize == 0
        ? 0
        : (int)Math.Ceiling((double)totalCount / pageSize);
}