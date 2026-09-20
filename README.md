# Phoenix.Mediator

`Phoenix.Mediator` is a lightweight mediator library for ASP.NET Core Minimal APIs.

The core package has **no third-party runtime dependencies**. Validation, Sentry, and Serilog
support live in opt-in companion packages, so you only pull in what you use.

It provides:
- Request/handler abstractions (`IRequest`, `IRequest<TResponse>`, `IRequestHandler<...>`)
- Endpoint-group discovery for Minimal APIs (`BaseEndpointGroup` + `MapEndpoints()`)
- Consistent API result mapping (`SendAsApiResult()`, `ToApiResult()`) and error wrappers
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
                await sender.SendAsApiResult(query, ct));
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

### Duplicate routes

Mapping the same route and HTTP method twice is accepted by ASP.NET Core: it only fails when a request first matches both endpoints, with an `AmbiguousMatchException` that surfaces as a 500. A route duplicated by accident — two endpoint groups mapping `GET users/{id}`, or the same route with a different parameter name — therefore stays invisible until someone calls it.

`MapEndpoints` reports these instead, and throws before the app starts:

```
1 route is mapped more than once. ASP.NET Core does not fail on this while routes are built; it throws
AmbiguousMatchException (HTTP 500) the first time a request matches more than one endpoint:

  GET /users/{id}:
    - HTTP: GET users/{id} (mapped in Sample.Api.UserEndpoints)
    - HTTP: GET users/{userId} (mapped in Sample.Api.AdminEndpoints)
```

Routes the matcher can tell apart are not reported: a different HTTP method, a route constraint (`{id:int}` next to `{slug}`), a different `WithOrder`, a different `RequireHost`, or an endpoint mapped for every method (like `/health`) next to one mapped for a specific method.

To log instead of throwing, or to turn the check off:

```csharp
app.MapEndpoints(new MapEndpointsOptions
{
    DuplicateEndpointHandling = DuplicateEndpointHandling.Warn // or .None
});
```

Only endpoints mapped by the time `MapEndpoints` returns are checked. If you map more afterwards, call `app.ValidateNoDuplicateEndpoints();` once you are done.

## Sending requests

In endpoints, use `SendAsApiResult`. It sends the request through the mediator and maps the result to an `IResult` (see [Response and error behavior](#response-and-error-behavior)):

```csharp
IResult result = await sender.SendAsApiResult(request, cancellationToken);
```

Everywhere else (services, background jobs, tests), use `Send` to get the handler's response itself:

```csharp
// IRequest<TResponse>: pass both type arguments. C# can't infer TResponse, and without them
// the call binds to the Send(object) overload, which returns object?.
SingleResponse<string> greeting = await sender.Send<GetGreetingQuery, SingleResponse<string>>(query, cancellationToken);

// IRequest (no response): returns a plain Task.
await sender.Send(command, cancellationToken);
```

Avoid `(await sender.Send(...)).ToApiResult()` in endpoints:
- `ToApiResult()` maps `null` to `204 No Content` without reading `EmptyResponseStatusCode`, but the [endpoint helpers](#endpoint-helpers) advertise the configured status in OpenAPI. With `EmptyResponseStatusCode.Ok`, the docs say `200` while the endpoint returns `204`.
- For an `IRequest` (no response), `sender.Send(command, ct)` binds to the overload that returns a plain `Task`. There's no result to call `ToApiResult()` on, so the code doesn't compile.

## Response and error behavior

Success responses come from `SendAsApiResult`. Errors come from the exception-handling middleware: `SendAsApiResult` doesn't catch exceptions, it lets them propagate. `MapEndpoints` registers the middleware by default; if you set `UseExceptionHandling = false`, call `app.UsePhoenixExceptionHandling()` yourself.

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

## File uploads (multipart)

`PostMultiPart` / `PutMultiPart` / `PatchMultiPart` map the route and add a body size limit (5 MB by default,
applied to both the server limit and the multipart form limit), a request timeout (120 s by default), and the
default OpenAPI responses.

```csharp
group.PostMultiPart("documents", async (ISender sender, [FromForm] UploadDocumentCommand command, CancellationToken ct) =>
    await sender.SendAsApiResult(command, ct), disableAntiforgery: true);
```

Two things decide whether these endpoints work, and neither is specific to this package:

**Antiforgery.** ASP.NET Core requires an antiforgery token for *any* endpoint whose delegate binds form data
(`IFormFile`, `IFormFileCollection`, `IFormCollection`, `[FromForm]`). Pick one:

- **APIs authenticated with bearer tokens** (no cookies): pass `disableAntiforgery: true`. Cookies are what CSRF
  abuses, so there is nothing to protect here.
- **Cookie-authenticated apps**: call `services.AddAntiforgery()` **and** `app.UseAntiforgery()`, expose the token
  (`IAntiforgery.GetAndStoreTokens(httpContext)`), and have clients send it in the `RequestVerificationToken` header
  (or the `__RequestVerificationToken` form field) along with the antiforgery cookie. Registering the services
  without the middleware is not enough: requests then fail with a 500 whose cause is only in the log. The helpers
  log a warning at startup when the services are missing entirely.

A delegate that reads `HttpRequest.Form` itself is never validated, whatever `disableAntiforgery` says, because
validation is enforced by form parameter binding. Validate such endpoints yourself with `IAntiforgery`.

**Timeouts.** `timeoutSeconds` only adds metadata. It is enforced only if the app calls
`services.AddRequestTimeouts()` and `app.UseRequestTimeouts()`, and it covers the whole request, so a large upload
over a slow connection can hit it.

## Route, query, and header members in body requests

Minimal APIs bind a request like `UpdateStudentCommand` from the JSON body as a whole, and only honor `[FromRoute]`, `[FromQuery]`, and `[FromHeader]` on its members under `[AsParameters]`. On their own, those attributes do nothing here: a route `id` is still read from the body and shows up in the OpenAPI request schema.

`AddMediator` keeps mediator-request members marked with those attributes out of the JSON body, so they aren't read from or written to it and the OpenAPI schema leaves them out. No `[JsonIgnore]` needed. Assign the value in the endpoint, and keep the parameter in the delegate so OpenAPI documents it:

```csharp
public record UpdateStudentCommand : IRequest<SingleResponse<int>>
{
    [FromRoute]
    public int Id { get; init; }
    public string? Name { get; init; }
}

group.Patch("{id}", (ISender sender, int id, UpdateStudentCommand command, CancellationToken ct) =>
    sender.SendAsApiResult(command with { Id = id }, ct));
```

Positional records work the same way: `public record UpdateStudentCommand([FromRoute] int Id, string? Name) : IRequest<SingleResponse<int>>;`

**The endpoint must assign the value.** An excluded member is always `default` after body binding, so a forgotten
`with { Id = id }` means the handler silently gets `0`/`Guid.Empty`/`null`. The request therefore needs a shape you
can still write to:

| Request shape | Assigning the route value |
|---|---|
| `record` with `{ get; init; }`, or a positional `record` | `command with { Id = id }` |
| `class` with `{ get; set; }` | `command.Id = id;` |
| `class` with `{ get; init; }`, or get-only properties set by a constructor | **not possible** — use a record or a settable property |

Two limits to keep in mind:

- **Only the JSON body is filtered.** A request bound from a form (`[FromForm]`, the multipart helpers) still reads
  those members from the form fields, so a caller can post `Id=999` to `/students/5`. In form endpoints, assign the
  route value after binding, or read it from the route parameter instead of the command.
- **OpenAPI support depends on the generator.** `Microsoft.AspNetCore.OpenApi` (.NET 9+) builds schemas from the same
  JSON contract and leaves these members out. Swashbuckle builds schemas by reflection and still shows them; add a
  schema filter there if the published schema matters.

This only affects the Minimal API JSON options (`ConfigureHttpJsonOptions`). Serializing the request with other `JsonSerializerOptions`, for example in logs, still includes the member.

Alternatively, let ASP.NET Core bind everything and skip the manual assignment. This works for classes and records
alike, and every OpenAPI generator documents it correctly:

```csharp
public record UpdateStudentCommand([FromRoute] int Id, [FromBody] UpdateStudentBody Body) : IRequest<SingleResponse<int>>;

group.Patch("{id}", (ISender sender, [AsParameters] UpdateStudentCommand command, CancellationToken ct) =>
    sender.SendAsApiResult(command, ct));
```

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
- Multiple roles **in one call** are **OR**-combined (matches ASP.NET Core's `AuthorizeAttribute.Roles` semantics).
  Calling it more than once — on the group and again on the endpoint, for example — is **AND**: each call adds its
  own requirement, so the caller must be in a role from every call.
- Role matching is **case-sensitive**, and the enum member name is the claim value. When your identity provider
  issues a different spelling, map it with `[EnumMember]`:
  ```csharp
  public enum AppRole
  {
      Admin,
      [EnumMember(Value = "super-admin")] SuperAdmin,   // claim value: "super-admin"
  }
  ```
- Roles are matched against claims of the identity's role claim type (`ClaimTypes.Role` by default). With JWT
  bearer tokens carrying a `"role"`/`"roles"` claim and `MapInboundClaims = false`, set
  `TokenValidationParameters.RoleClaimType` to match, or every check fails with `403`. Providers that nest roles
  (Keycloak's `realm_access.roles`) need a claims transformation first.
- `[Flags]` combinations (`AppRole.Admin | AppRole.Manager`) and undefined values are rejected with an
  `ArgumentException` at startup: a combination is ambiguous — pass the roles as separate arguments for OR.
- Calling `RequireRole<TBuilder, TRole>()` with no roles is equivalent to `RequireAuthorization()`
  (any authenticated user). For that case, prefer `RequireAuthorization()` directly — type inference
  can't pick `TRole` from an empty argument list.
- Both type parameters are inferred at the call site, so you write `.RequireRole(AppRole.Admin)`,
  not `.RequireRole<RouteHandlerBuilder, AppRole>(AppRole.Admin)`.

## Validation

Install `Phoenix.Mediator.Validation` and call `AddMediatorValidation(assemblies...)` — it registers the
validation pipeline behavior and all FluentValidation validators in those assemblies.
Validation failures are returned as `400` with the `errors` response body.

Visibility does not matter: `public`, `internal`, `file`-scoped and `private` nested validators are all
discovered, the same way handlers are.

A validator that is never registered fails silently — the behavior finds no validators for the request,
reports no failures and lets it through, so invalid input is accepted exactly as if it had been checked.
Each case that causes it is logged as a warning once at host startup:

| Warning | Cause |
| --- | --- |
| `AddMediatorValidation() was called without assemblies` | The behavior is registered but nothing is scanned. Intentional only if you register your `IValidator<T>` implementations yourself — the warning is suppressed when you have. |
| `found no FluentValidation validators in the scanned assemblies` | The assemblies you passed hold no validators. Validators often live in a different assembly from the handlers. |
| `still has unbound type parameters` | The validator is generic, or is nested inside a generic type and inherits its type parameters. The scan skips it. Move it out of the generic type, or register a closed version explicitly. |
| `has no public constructor` | The validator is registered but the container cannot construct it, so the first request that uses it throws `A suitable constructor ... could not be located`. |

## Optional logging helpers

Install `Phoenix.Mediator.Serilog` (Serilog only, no Sentry dependency):

```csharp
using Phoenix.Mediator.Serilog;

builder.AddLogging();
var app = builder.Build();
app.UsePhoenixRequestLogEnrichment();
```

To also send events to Sentry, install `Phoenix.Mediator.Serilog.Sentry` and compose the add-on:

```csharp
using Phoenix.Mediator.Serilog;
using Phoenix.Mediator.Serilog.Sentry;

builder.AddSentry(); // Sentry ASP.NET integration (error capture + tracing + IHub)
// initializeSdk: false — AddSentry() already initialized the SDK, and it must be initialized exactly once.
builder.AddLogging(configureSinks: lc => lc.WriteToSentry(builder.Configuration, initializeSdk: false));
```

Using the Serilog sink on its own (without `AddSentry()`)? Leave `initializeSdk` at its default, so the sink
initializes the SDK.

File logging is on by default (rolling files under `{ContentRoot}/logs`). For containerized or horizontally-scaled deployments, disable it and rely on stdout collection:

```csharp
builder.AddLogging(enableFileLogging: false);
```

Sentry PII remains disabled unless you explicitly set `Sentry:SendDefaultPii=true`. Client-IP log enrichment is also off unless PII is enabled (or you pass `app.UsePhoenixRequestLogEnrichment(logClientIp: true)`); the trace id is always enriched.

## Upgrading

### Behavior changes after 2.0.6

- **Duplicate handlers now fail at startup.** Two handlers for the same request used to be resolved by scan order,
  silently. `AddMediator`/`AddMediatorHandlers` now throw and name both types. Register the one you want explicitly
  before the scan if you need to override a handler.
- **`RequireRole` rejects `[Flags]` combinations and undefined enum values** with an `ArgumentException` at startup.
  Pass roles as separate arguments for OR semantics.
- **Cancelled requests are no longer turned into `500`.** The exception-handling middleware rethrows cancellation
  when the request was aborted, so `UseRequestTimeouts` can write its `504` and client disconnects stop filling the
  error log.
- **Framework bad requests keep their status code.** Malformed JSON, missing required parameters, invalid
  antiforgery tokens and oversized forms return their real status (`400`, `413`, ...) with the standard
  `{"errors":[...],"traceId":"..."}` body, instead of `500` in Development.
- **`UnauthorizedAccessException` is logged** (still mapped to `401`). .NET throws it for file-permission errors too,
  so it should never pass silently.
- **`MultiResponse<T>`** takes an `IReadOnlyList<T>` in its constructor and exposes `PageSize`, so it can be
  deserialized (`ReadFromJsonAsync<MultiResponse<T>>`) as well as serialized. Existing `new MultiResponse<T>(list, …)`
  calls keep compiling.
- **`WriteToSentry(configuration)`** takes an optional `initializeSdk` parameter; pass `false` when the app also
  calls `AddSentry()`.
- **`BaseEndpointGroup.GroupName`** lower-cases with the invariant culture, so Turkish/Azerbaijani servers no longer
  produce `ınvoice` route prefixes.

### 2.0.6

`AddMediator` began excluding mediator-request members marked `[FromRoute]`/`[FromQuery]`/`[FromHeader]` from the
JSON body. If an app previously relied on those values arriving in the body, they now arrive as `default` — assign
them in the endpoint, as shown in
[Route, query, and header members in body requests](#route-query-and-header-members-in-body-requests).


