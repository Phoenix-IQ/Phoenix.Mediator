using Phoenix.CustomMediator.Wrappers;

namespace Phoenix.CustomMediator.Abstractions;

public interface IPagedRequest<TItem> : IRequest<MultiResponse<TItem>>
{
    int PageNum { get; }
    int PageSize { get; }
    string? Query { get; }
}