using FluentValidation;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Web;
using Phoenix.Mediator.Wrappers;

namespace TestApi;
public class TestCommand : IRequest<SingleResponse<string>>
{
    public string Test { get; set; } = string.Empty;    
}

public class TestCommandValidator:AbstractValidator<TestCommand>
{
    public TestCommandValidator()
    {
        RuleFor(x=>x.Test).NotEmpty().WithMessage("Test property must not be empty");
    }
}
public class TestCommandHandler:IRequestHandler<TestCommand,SingleResponse<string   >>
{
    public async Task<SingleResponse<string>> Handle(TestCommand request, CancellationToken cancellationToken)
    {
        await Task.Delay(10);
        return new SingleResponse<string>($"Received: {request.Test}");
    }
}
public class TestEndpoints : BaseEndpointGroup
{
    public override void Map(WebApplication app)
    {
        app.MapGroup(GroupName)
            .Post("", async (ISender sender, TestCommand command, CancellationToken cancellationToken) => await sender.Send(command, cancellationToken));
    }
}