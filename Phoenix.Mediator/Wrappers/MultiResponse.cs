namespace Phoenix.Mediator.Wrappers;

public class MultiResponse<T>
{
    public T Data { get; set; } = default!;
    public int PagesCount { get; set; }
    public int TotalCount { get; set; }  
}