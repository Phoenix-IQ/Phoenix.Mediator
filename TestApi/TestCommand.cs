using FluentValidation;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Wrappers;

namespace TestApi;

public class TestCommand : IRequest
{
    public string Test { get; set; } = string.Empty;
}

public class TestCommandValidator : AbstractValidator<TestCommand>
{
    public TestCommandValidator()
    {
        RuleFor(x => x.Test).NotEmpty().WithMessage("Test property must not be empty");
    }
}
public class TestCommandHandler : IRequestHandler<TestCommand>
{
    public async Task Handle(TestCommand request, CancellationToken cancellationToken)
    {
        await Task.Delay(10);
    }
}
public class TestQuery : IRequest<SingleResponse<string>>
{
    public string Query { get; set; } = string.Empty;
}
sealed class TestQueryHandler : IRequestHandler<TestQuery, SingleResponse<string>>
{
    public async Task<SingleResponse<string>> Handle(TestQuery request, CancellationToken cancellationToken)
    {
        await Task.Delay(10);
        return new SingleResponse<string>($"You sent: {request.Query}");
    }
}
public class TestCommandWithResult : IRequest<string>
{
    public string Value { get; set; } = string.Empty;
}
sealed class TestCommandWithResultHandler : IRequestHandler<TestCommandWithResult, string>
{
    public async Task<string> Handle(TestCommandWithResult request, CancellationToken cancellationToken)
    {
        await Task.Delay(10);
        return $"You sent: {request.Value}";
    }
}
public record TestRecord(int Age, string Name);
public class TestRecordQuery : IRequest<SingleResponse<TestRecord>>
{
    public int Age { get; set; }
    public string Name { get; set; } = string.Empty;
}
sealed class TestRecordQueryHandler : IRequestHandler<TestRecordQuery, SingleResponse<TestRecord>>
{
    public async Task<SingleResponse<TestRecord>> Handle(TestRecordQuery request, CancellationToken cancellationToken)
    {
        await Task.Delay(10);
        return new SingleResponse<TestRecord>(new TestRecord(request.Age, request.Name));
    }
}
public class TestEndpoints : BaseEndpointGroup
{
    public override void Map(WebApplication app)
    {
        app.MapGroup(GroupName)
            .Post("", async (ISender sender, TestCommand command, CancellationToken cancellationToken) => await sender.Send(command, cancellationToken))
            .Post("fa", async (ISender sender, TestCommandWithResult command, CancellationToken cancellationToken) => await sender.Send(command, cancellationToken))
            .Get("test", async (ISender sender, [AsParameters] TestQuery query, CancellationToken cancellationToken) => await sender.Send(query, cancellationToken))
            .Get("record", async (ISender sender, [AsParameters] TestRecordQuery query, CancellationToken cancellationToken) => await sender.Send(query, cancellationToken));
    }
}
