using Phoenix.Mediator.Wrappers;

namespace Phoenix.Mediator.Abstractions;

public interface IPagedRequest<TItem> : IRequest<MultiResponse<TItem>>
{
    int PageNum { get; }
    int PageSize { get; }
    string? Query { get; }
}