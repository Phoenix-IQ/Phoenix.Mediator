# Phoenix.Mediator

`Phoenix.Mediator` is a lightweight mediator library for ASP.NET Core Minimal APIs.

The core package has **no third-party runtime dependencies**. Validation, Sentry, and Serilog
support live in opt-in companion packages, so you only pull in what you use.

It provides:
- Request/handler abstractions (`IRequest`, `IRequest<TResponse>`, `IRequestHandler<...>`)
- Endpoint-group discovery for Minimal APIs (`BaseEndpointGroup` + `MapEndpoints()`)
- Consistent API result mapping (`ToApiResult()`) and error wrappers
- Opt-in pipeline behaviors (FluentValidation, Sentry) via companion packages
- Opt-in Serilog/Sentry bootstrapping helpers via a companion package

## Packages

| Package | Purpose | Adds dependency on |
|---------|---------|--------------------|
| `Phoenix.Mediator` | Core mediator, endpoints, error handling | (none — `Microsoft.AspNetCore.App` only) |
| `Phoenix.Mediator.Validation` | `AddMediatorValidation()` — FluentValidation behavior | FluentValidation |
| `Phoenix.Mediator.Sentry` | `AddMediatorSentry()` — Sentry tracing/error behavior | Sentry |
| `Phoenix.Mediator.Serilog` | `AddLogging()` / request log enrichment | Serilog (no Sentry) |
| `Phoenix.Mediator.Serilog.Sentry` | `AddSentry()` + `WriteToSentry()` add-on | Sentry, Sentry.Serilog |
| `Phoenix.Mediator.All` | Convenience bundle — depends on all of the above | everything above |

## Install

Pick the individual packages you need:

```bash
dotnet add package Phoenix.Mediator
# optional:
dotnet add package Phoenix.Mediator.Validation
dotnet add package Phoenix.Mediator.Sentry
dotnet add package Phoenix.Mediator.Serilog
dotnet add package Phoenix.Mediator.Serilog.Sentry  # only if you want the Sentry sink
```

…or pull everything in one shot with the bundle:

```bash
dotnet add package Phoenix.Mediator.All
```

`Phoenix.Mediator.All` is a meta-package: it ships no code, just transitive references to every package in the table above. Prefer the individual packages when you want to avoid unused third-party dependencies (FluentValidation, Sentry, Serilog).

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
var assembly = Assembly.GetExecutingAssembly();

builder.Services
    .AddMediator(assembly)        // core: ISender + request handlers
    .AddMediatorSentry()          // optional: Phoenix.Mediator.Sentry
    .AddMediatorValidation(assembly); // optional: Phoenix.Mediator.Validation
```

Empty `IRequest` responses default to `204 No Content`. Configure `200 OK` during registration when that better matches your API contract:

```csharp
builder.Services.AddMediator(options =>
{
    options.EmptyResponseStatusCode = EmptyResponseStatusCode.Ok;
}, assembly);
```

`AddMediator(assemblies...)` registers:
- `ISender` (scoped)
- request handlers from the provided assemblies
- the `/health` endpoint support

Pipeline behaviors are **opt-in** and run in registration order (first registered = outermost).
`AddMediatorSentry()` before `AddMediatorValidation(...)` makes the Sentry span wrap validation.
`AddMediatorValidation(assemblies...)` also registers FluentValidation validators from those assemblies.

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

By default `MapEndpoints` also registers the exception-handling middleware and maps `/health`. To opt out of either (e.g. you register the middleware yourself for precise ordering), pass `MapEndpointsOptions`:

```csharp
app.MapEndpoints(new MapEndpointsOptions
{
    UseExceptionHandling = false, // you call app.UsePhoenixExceptionHandling() yourself
    MapHealthChecks = false       // or app.MapPhoenixHealthChecks("/healthz")
});
```

Or compose the pieces directly: `app.UsePhoenixExceptionHandling();`, `app.MapPhoenixHealthChecks();`, then `app.MapEndpoints(...)`.

## Sending requests

Send a request through the mediator and map the result to an `IResult` with `ToApiResult()`:

```csharp
object? result = await sender.Send(request, cancellationToken);
IResult apiResult = result.ToApiResult();
```

Note: the parameterless `ToApiResult()` maps a `null`/void result to `204 No Content` and does **not** consult `EmptyResponseStatusCode`. If you need the configured empty-response status for void requests, use `sender.SendAsApiResult(request, ct)` instead.

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

Error body shape (the `traceId` correlates the response with your logs/Sentry):

```json
{
  "errors": ["message 1", "message 2"],
  "traceId": "0af7651916cd43dd8448eb211c80319c"
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

## Authorization

`Phoenix.Mediator.Web.AuthorizationExtensions.RequireRole<TBuilder, TRole>(...)` is a thin wrapper over
`RequireAuthorization(...)` that takes any **enum** as the role type. Enum member names are used as the
role-claim values, so they must match the roles your identity provider issues.

Define your roles as an enum and gate endpoints with them:

```csharp
public enum AppRole
{
    Admin,
    Manager,
    User
}

public sealed class AdminEndpoints : BaseEndpointGroup
{
    public override void Map(WebApplication app)
    {
        app.MapGroup(GroupName)
            .Get("admin/stats", (ISender sender, CancellationToken ct) => /* ... */)
            .RequireRole(AppRole.Admin);                       // single role

        app.MapGroup(GroupName)
            .Post("reports", (ISender sender, CancellationToken ct) => /* ... */)
            .RequireRole(AppRole.Admin, AppRole.Manager);      // OR — either role works
    }
}
```

Notes:
- Multiple roles are **OR**-combined (matches ASP.NET Core's `AuthorizeAttribute.Roles` semantics).
- Calling `RequireRole<TBuilder, TRole>()` with no roles is equivalent to `RequireAuthorization()`
  (any authenticated user). For that case, prefer `RequireAuthorization()` directly — type inference
  can't pick `TRole` from an empty argument list.
- Both type parameters are inferred at the call site, so you write `.RequireRole(AppRole.Admin)`,
  not `.RequireRole<RouteHandlerBuilder, AppRole>(AppRole.Admin)`.

## Validation

Install `Phoenix.Mediator.Validation` and call `AddMediatorValidation(assemblies...)` — it registers the
validation pipeline behavior and all FluentValidation validators in those assemblies.
Validation failures are returned as `400` with the `errors` response body.

## Optional logging helpers

Install `Phoenix.Mediator.Serilog` (Serilog only, no Sentry dependency):

```csharp
using Phoenix.Mediator.Extensions;

builder.AddLogging();
var app = builder.Build();
app.UsePhoenixRequestLogEnrichment();
```

To also send events to Sentry, install `Phoenix.Mediator.Serilog.Sentry` and compose the add-on:

```csharp
using Phoenix.Mediator.Extensions;

builder.AddSentry(); // Sentry ASP.NET integration (error capture + tracing + IHub)
builder.AddLogging(configureSinks: lc => lc.WriteToSentry(builder.Configuration)); // Serilog → Sentry sink
```

File logging is on by default (rolling files under `{ContentRoot}/logs`). For containerized or horizontally-scaled deployments, disable it and rely on stdout collection:

```csharp
builder.AddLogging(enableFileLogging: false);
```

Sentry PII remains disabled unless you explicitly set `Sentry:SendDefaultPii=true`. Client-IP log enrichment is also off unless PII is enabled (or you pass `app.UsePhoenixRequestLogEnrichment(logClientIp: true)`); the trace id is always enriched.


