namespace Phoenix.CustomMediator.Wrappers;

public class SingleResponse<T>
{
    public int StatusCode { get; set; } = 200;
    public string Message { get; set; } = "ok";
    public T? Result { get; set; } 
}
