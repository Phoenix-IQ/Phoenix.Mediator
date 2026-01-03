using Phoenix.CustomMediator.Wrappers;

namespace Phoenix.CustomMediator.Abstractions;

public interface ISingleRequest<TItem> : IRequest<SingleResponse<TItem>>
{
}
