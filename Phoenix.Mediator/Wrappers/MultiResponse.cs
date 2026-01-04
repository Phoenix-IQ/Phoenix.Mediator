namespace Phoenix.Mediator.Wrappers;

public class MultiResponse<T>(List<T> data,int pagesCount, int totalCount)
{
    public List<T> Data => data;
    public int PagesCount => pagesCount;
    public int TotalCount => totalCount;
}