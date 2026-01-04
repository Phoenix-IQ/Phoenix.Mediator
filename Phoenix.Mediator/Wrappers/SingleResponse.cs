namespace Phoenix.Mediator.Wrappers;

public class SingleResponse<T>(T result)
{
    public T? Result => result;
}
