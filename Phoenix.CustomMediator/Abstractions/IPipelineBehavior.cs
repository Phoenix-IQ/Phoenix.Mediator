namespace Phoenix.CustomMediator.Abstractions;

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();
public delegate Task RequestHandlerDelegate();

public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate<TResponse> next);
}

public interface IPipelineBehavior<in TRequest>
    where TRequest : IRequest
{
    Task Handle(TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate next);
}


