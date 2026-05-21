# Phoenix.Mediator

`Phoenix.Mediator` is a lightweight mediator library for ASP.NET Core Minimal APIs.

It provides:
- Request/handler abstractions (`IRequest`, `IRequest<TResponse>`, `IRequestHandler<...>`)
- Built-in pipeline behaviors for FluentValidation and Sentry
- Endpoint-group discovery for Minimal APIs (`BaseEndpointGroup` + `MapEndpoints()`)
- Consistent API result mapping (`ToApiResult()`) and error wrappers
- Optional Serilog/Sentry bootstrapping helpers

## Install

```bash
dotnet add package Phoenix.Mediator
```

## Target frameworks

- `net8.0`
- `net9.0`
- `net10.0`

## Quick start

### 1. Register mediator

```csharp
using Phoenix.Mediator.Mediator;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMediator(Assembly.GetExecutingAssembly());
```

Empty `IRequest` responses default to `204 No Content`. Configure `200 OK` during registration when that better matches your API contract:

```csharp
builder.Services.AddMediator(options =>
{
    options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok;
}, Assembly.GetExecutingAssembly());
```

`AddMediator(assemblies...)` registers:
- `ISender` (scoped)
- built-in pipeline behaviors (Sentry + FluentValidation)
- request handlers from the provided assemblies
- FluentValidation validators from the provided assemblies

### 2. Create a request + handler

```csharp
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Wrappers;

public sealed class GetGreetingQuery : IRequest<SingleResponse<string>>
{
    public string Name { get; set; } = string.Empty;
}

public sealed class GetGreetingQueryHandler : IRequestHandler<GetGreetingQuery, SingleResponse<string>>
{
    public Task<SingleResponse<string>> Handle(GetGreetingQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SingleResponse<string>($"Hello {request.Name}"));
    }
}
```

### 3. Map endpoints via endpoint groups

```csharp
using Microsoft.AspNetCore.Mvc;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Web;

public sealed class GreetingEndpoints : BaseEndpointGroup
{
    public override void Map(WebApplication app)
    {
        app.MapGroup(GroupName)
            .Get("hello", async (ISender sender, [AsParameters] GetGreetingQuery query, CancellationToken ct) =>
                (await sender.Send(query, ct)).ToApiResult());
    }
}
```

Then map all groups in `Program.cs`:

```csharp
using Phoenix.Mediator.Web;

var app = builder.Build();
app.MapEndpoints(); // also maps /health
app.Run();
```

If your endpoint groups live in a separate class library, pass those assemblies explicitly:

```csharp
app.MapEndpoints(typeof(GreetingEndpoints).Assembly);
```

## Sending requests

```csharp
object? result = await sender.Send(request, cancellationToken);
IResult apiResult = result.ToApiResult();
```

`ISender.Send(...)` accepts either:
- `IRequest<TResponse>`
- `IRequest`

## Response and error behavior

- `IRequest<TResponse>`: returns JSON body (`200 OK`) on success
- `IRequest` (no response): returns configured empty response status on success (`204 No Content` by default, or `200 OK`)
- `HttpResponseException` (or derived exceptions): returns `{"errors":[...]}` with mapped status code
- Unhandled exceptions: returns `500` with the configured unknown-error message

Unknown-error messages can be configured per consuming project in JSON. The middleware matches `Accept-Language` case-insensitively, including `ar`, `Arabic`, `en`, and `English`; if the header is missing, it uses `Default`/`DefaultLanguage`, then English.

```json
{
  "ErrorMessages": {
    "Default": "En",
    "Ar": "حصل خطأ غير معرف",
    "En": "Unknown error occurred"
  }
}
```

Built-in exception types:
- `BadRequestException`
- `NotFoundException`

Error body shape:

```json
{
  "errors": ["message 1", "message 2"]
}
```

## Endpoint helpers

`Phoenix.Mediator.Web.EndpointsExtensions` adds helpers for:
- `Get`, `Post`, `Put`, `Delete`, `Patch`
- `PostMultiPart`, `PutMultiPart`, `PatchMultiPart`

These helpers:
- Add default OpenAPI responses (`401`, `403`, `400`, `500`)
- Infer success response metadata from request type (`IRequest<T>` => `200`, `IRequest` => configured empty response status)
- Allow explicit response metadata via `ResponseDto`

## Validation

When you use `AddMediator(assemblies...)`, validators in those assemblies are auto-registered via FluentValidation.
Validation failures are returned as `400` with the `errors` response body.

## Optional logging helpers

```csharp
using Phoenix.Mediator.Extensions;

builder.AddLogging(hasSentry: true);
var app = builder.Build();
app.UsePhoenixRequestLogEnrichment();
```

Sentry PII remains disabled unless you explicitly set `Sentry:SendDefaultPii=true`.


