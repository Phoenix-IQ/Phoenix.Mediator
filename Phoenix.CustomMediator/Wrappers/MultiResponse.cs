namespace Phoenix.CustomMediator.Wrappers;

public class MultiResponse<T>
{
    public int StatusCode { get; set; } = 200;
    public string Message { get; set; } = "ok";
    public T Data { get; set; } = default!;
    public int PagesCount { get; set; }
    public int TotalCount { get; set; }  
}
