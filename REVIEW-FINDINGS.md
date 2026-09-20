# Phoenix.Mediator review findings

| | |
|---|---|
| **Reviewed** | `master` at `f3cfac1` (tag `v2.0.6`), all 46 tracked files |
| **Date** | 2026-09-17 |
| **Focus areas** | 1. Model binding (record vs class) · 2. Dependency injection · 3. Multipart and antiforgery · 4. Roles · 5. Other bugs |
| **Baseline** | Existing test suite: 31/31 passing on net8.0, net9.0 and net10.0 |

## How the findings were verified

Every finding below was reproduced unless it is marked *(from code)*.

- A throwaway ASP.NET Core app (outside this repo) referenced `Phoenix.Mediator`, `Phoenix.Mediator.Validation` and `Phoenix.Mediator.Serilog.Sentry` as project references.
- Each scenario started a real Kestrel server on `127.0.0.1` and sent real HTTP requests with `HttpClient`. Status codes, response bodies and log entries were captured.
- Runtimes: **.NET 8.0.31** and **.NET 10.0.12**, in both `Production` and `Development` environments where it matters.
- OpenAPI generators: **Swashbuckle.AspNetCore 6.5.0** on .NET 8, **Microsoft.AspNetCore.OpenApi 10.0.12** on .NET 10.
- Package versions as pinned by the repo (FluentValidation 12.1.1, Sentry 6.5.0, Serilog.Sinks.File 7.0.0, ...).

Results were identical on .NET 8 and .NET 10 unless a finding says otherwise.

### Severity legend

| Label | Meaning |
|---|---|
| **High** | Breaks a primary feature by default, loses data/logs, or hides failures silently |
| **Medium** | Wrong behavior in a realistic setup, security-relevant, or misleading error handling |
| **Low** | Edge case, docs, or design issue with limited impact |

## Summary

Status as of 2026-09-17, after the fix pass described in [Fix status](#fix-status).

| # | Sev. | Area | Finding | Status |
|---|---|---|---|---|
| 1 | Medium | Binding | Classes with `init`/constructor-only members can never receive route/query/header values; a forgotten `with` silently yields `0` | Documented |
| 2 | Medium | Binding | Multipart/form requests still bind `[FromRoute]` members from form fields (over-posting) | Documented |
| 3 | Medium-Low | Binding | .NET 9+ `RespectNullableAnnotations` turns an excluded non-nullable member into a 400 | **Fixed** |
| 4 | Low-Medium | Binding | README says OpenAPI hides excluded members; Swashbuckle still shows them | **Fixed** (docs) |
| 5 | Medium | Binding | The JSON exclusion shipped in patch `v2.0.6` with no opt-out | Documented |
| 6 | **High** | DI | `internal` FluentValidation validators are silently not registered | **Fixed** |
| 7 | Medium | DI | `AddMediatorValidation()` with no assemblies validates nothing, silently | Documented |
| 8 | Medium | DI | Generic handlers / generic endpoint groups in scanned assemblies crash startup | **Fixed** |
| 9 | Medium | DI | `Send(cmd)` with a variable typed as `IRequest` throws at runtime | **Fixed** |
| 10 | Low-Medium | DI | Duplicate handlers for one request are accepted silently | **Fixed** |
| 11 | Low | DI | Endpoint discovery depends on which registration API was used; no opt-outs | Partly (documented) |
| 12 | **High** | Multipart | Default multipart endpoints return 500 unless antiforgery is fully configured | Partly |
| 13 | Medium | Multipart | "Antiforgery ON by default" does not protect endpoints that read the form manually | Documented |
| 14 | Medium | Multipart | `timeoutSeconds` is ignored without timeout middleware; with it, timeouts become 500 instead of 504 | **Fixed** (504) + documented |
| 15 | Low-Medium | Multipart | `maxRequestBodySize` cannot exceed the form limit (128 MB default) | **Fixed** |
| 16 | Low | Multipart | 413/400 responses bypass the `{errors, traceId}` body; `Accepts` metadata overwritten | Partly |
| 17 | Medium | Roles | Enum names must match role claims exactly (case-sensitive), no name mapping | **Fixed** |
| 18 | Medium | Roles | `UnauthorizedAccessException` becomes 401 with no body and no log | Partly (now logged) |
| 19 | Low-Medium | Roles | `[Flags]`/undefined enum values accepted; stacked calls are AND (undocumented) | **Fixed** |
| 20 | Medium | Errors | `BadHttpRequestException` becomes 500 in Development; empty 400 bodies in Production | **Fixed** (Dev) / partly (Prod) |
| 21 | Medium | Errors | `ArgumentException` -> 400 and `KeyNotFoundException` -> 404 hide server bugs | **Not fixed** (needs a decision) |
| 22 | Medium | Errors | Cancellations (timeouts, disconnects) are logged as errors and returned as 500 | **Fixed** |
| 23 | **High** | Logging | Log files stop being written after 50 MB per day (no `rollOnFileSizeLimit`) | **Fixed** |
| 24 | Medium | Sentry | `TracesSampleRate` parsed with server culture: ignored on `ar-IQ`, 100% on `de-DE` | **Fixed** |
| 25 | Medium-Low | Sentry | README setup initializes the Sentry SDK twice with different options | **Fixed** (opt-out) |
| 26 | Low-Medium | Wrappers | `MultiResponse<T>` cannot be deserialized | **Fixed** |
| 27 | Low | Web | `GroupName` uses culture-sensitive `ToLower()` (Turkish `ınvoice` routes) | **Fixed** |
| 28 | Low | Web | Null `IRequest<T>` result returns 204 even when 200 is configured; error schema lacks `traceId` | **Fixed** |
| 29 | **High** | Process | CI never runs: workflow triggers on `main`, default branch is `master` | **Fixed** |
| 30 | Low | Docs | README logging snippets use a namespace that does not exist | **Fixed** |
| 31 | Low | Architecture | Core package forces ASP.NET Core onto projects that only need `IRequest` | **Not fixed** (package split) |

## Fix status

The findings above describe the code **as reviewed** (`f3cfac1`, `v2.0.6`); the Status column records what changed
afterwards. Fixes are covered by 18 new tests in `Phoenix.Mediator.Tests/ReviewFixTests.cs` (49 tests total, passing
on net8.0/net9.0/net10.0), and were re-verified with the same HTTP scenarios that found them.

**Deliberately not changed:**

- **21** — `ArgumentException` -> 400 and `KeyNotFoundException` -> 404 are kept. Removing them turns those into
  500s, which is a visible behavior change for any app whose handlers rely on it. Worth doing, but it is a decision
  about the package's error contract, not an obvious bug fix.
- **31** — splitting the abstractions into their own package is a packaging change, with new NuGet IDs and a
  migration for consumers.
- **7** — `AddMediatorValidation()` without assemblies still works, because registering validators separately is a
  legitimate setup; the behavior is documented instead of throwing.
- **1, 2, 5, 13** — inherent to how ASP.NET Core binds JSON, forms and route values. Documented in the README so the
  traps are visible.
- **12, 16, 18, 20 (Production)** — partly fixed; see the notes on those findings below for what remains and why.

---

## 1. Model binding: record vs class

### How binding works

ASP.NET Core Minimal APIs choose a binding source per **delegate parameter**, not per property:

| Endpoint shape | Binding | Honors `[FromRoute]`/`[FromQuery]`/`[FromHeader]` on properties? |
|---|---|---|
| `Post`/`Put`/`Patch` with a complex request parameter | Whole object read from the JSON body (System.Text.Json) | No |
| `[AsParameters] TRequest` (required for `Get`/`Delete`) | Each property / constructor parameter bound separately | Yes |
| `IFormFile`, `[FromForm] TRequest` (multipart helpers) | Form binder | No |

### What Phoenix adds

`AddMediator` registers `RequestBodyJsonOptionsSetup` ([ServiceCollectionExtensions.cs:39](Phoenix.Mediator/Mediator/ServiceCollectionExtensions.cs#L39)), an `IPostConfigureOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>` that wraps the Minimal API JSON type resolver ([RequestBodyJsonOptionsSetup.cs:21](Phoenix.Mediator/Web/RequestBodyJsonOptionsSetup.cs#L21)).

For every type implementing `IRequest` or `IRequest<T>`, a property marked `[FromRoute]`, `[FromQuery]` or `[FromHeader]` is excluded from the JSON body ([RequestBodyJsonOptionsSetup.cs:78](Phoenix.Mediator/Web/RequestBodyJsonOptionsSetup.cs#L78)). The attribute can be on the property, or on a same-named constructor parameter (positional records). Exclusion means:

- the property's getter and setter are removed from the JSON contract;
- `IsRequired` is forced to `false`;
- a `DiscardValueConverter<T>` skips the JSON value and returns `default` ([line 94](Phoenix.Mediator/Web/RequestBodyJsonOptionsSetup.cs#L94)).

After body binding the member is therefore **always `default`**, and the endpoint must assign it. This applies only to JSON bodies. `[AsParameters]` and form binding do not use it.

### Observed results

**JSON body.** `PUT /a/{shape}/5` with body `{"id":999,"name":"x"}`, handler echoes `Id` and `Name`:

| Request shape | How the endpoint assigns the route id | `Id` seen by handler |
|---|---|---|
| `class` with `[FromRoute] public int Id { get; set; }` | `cmd.Id = id;` | 5 |
| `class` with `{ get; init; }` | **Impossible**: `with` is records-only and `init` can't be set afterwards | **0** |
| `class` with get-only properties set by a `[FromRoute] int id` constructor parameter | **Impossible** | **0** |
| `record` with `{ get; init; }` | `cmd with { Id = id }` | 5 |
| positional `record Cmd([FromRoute] int Id, string? Name)` | `cmd with { Id = id }` | 5 |
| positional record, endpoint forgets `with` | nothing | **0**, no error |
| same shape, **not** an `IRequest` (plain DTO) | nothing | 999 (body value used, attribute ignored) |

**`[AsParameters]`.** `GET /b/{shape}/5?page=2&search=abc&id=777` with header `X-Tenant: t1`. For a class with settable properties, a positional record and a record with `init` properties, all three produced:

```json
{"id":5,"page":2,"tenant":"t1","search":"abc"}
```

`Id` came from the route (the `?id=777` query value was ignored), `Page` from the query and `Tenant` from the header. **Record vs class makes no difference with `[AsParameters]`.**

**Form (`[FromForm]`).** A class with settable properties, a class with `init` properties, a nominal record and a positional record all bound `Name` and an `IFormFile File` correctly on .NET 8 and .NET 10.

### Finding 1 — Medium: classes with `init` or constructor-only members can never receive the route value

**Where:** [RequestBodyJsonOptionsSetup.cs:78](Phoenix.Mediator/Web/RequestBodyJsonOptionsSetup.cs#L78), README pattern at [README.md:236](README.md#L236).

The README pattern `sender.SendAsApiResult(command with { Id = id }, ct)` only works for records. A class request cannot use `with`, and a class with `init` or constructor-only members cannot be updated after binding. For those shapes the handler always receives `0` / `Guid.Empty` / `null`.

The same silent default happens when a record endpoint forgets `with`. Typical consequences are `NotFoundException` for id `0`, or an update applied to the wrong row when `Guid.Empty`/`0` is meaningful.

**Suggested fix (pick one):**
- Document that members excluded from the body must be settable, or that the request must be a record.
- Add an endpoint filter that copies `[FromRoute]`/`[FromQuery]`/`[FromHeader]` members from `HttpContext` into the bound request, so endpoints don't have to.
- Recommend the ASP.NET-native shape, which works for classes and records and is documented correctly by every OpenAPI generator:
  ```csharp
  public record UpdateStudentCommand([FromRoute] int Id, [FromBody] UpdateStudentBody Body) : IRequest<SingleResponse<int>>;
  group.Patch("{id}", (ISender sender, [AsParameters] UpdateStudentCommand command, CancellationToken ct) =>
      sender.SendAsApiResult(command, ct));
  ```

### Finding 2 — Medium (security): multipart/form requests bind `[FromRoute]` members from form fields

**Where:** exclusion is JSON-only ([RequestBodyJsonOptionsSetup.cs:18](Phoenix.Mediator/Web/RequestBodyJsonOptionsSetup.cs#L18)); README section [README.md:222](README.md#L222) reads as a general guarantee.

**Reproduced:**
```csharp
public sealed class FormItemCmd : IRequest<string>
{
    [FromRoute] public int Id { get; set; }
    public string? Name { get; set; }
    public IFormFile? File { get; set; }
}

group.PostMultiPart("items/{id:int}", (int id, [FromForm] FormItemCmd cmd) => ..., disableAntiforgery: true);
```
`POST /f/items/5` with form fields `Id=999`, `Name=x` and a file returned `{"routeId":5,"cmdId":999,...}`.

If authorization checks the route id (`5`) but the handler uses `cmd.Id` (`999`), a caller can act on a resource they don't own (IDOR).

**Suggested fix:** state in the README that the protection is JSON-only. In form endpoints, always overwrite route members after binding (`cmd.Id = id`), or bind the route value separately and never read it from the command.

### Finding 3 — Medium-Low (.NET 9+): `RespectNullableAnnotations` turns an excluded non-nullable member into a 400

**Where:** `DiscardValueConverter<T>.Read` returns `default` ([RequestBodyJsonOptionsSetup.cs:94](Phoenix.Mediator/Web/RequestBodyJsonOptionsSetup.cs#L94)).

**Reproduced on .NET 10** (the option exists from .NET 9):
```csharp
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.RespectNullableAnnotations = true);

public sealed record SlugCmd([FromRoute] string Slug, string Name) : IRequest<Echo>;
group.Put("slug/{slug}", (ISender s, string slug, SlugCmd cmd, CancellationToken ct) => s.SendAsApiResult(cmd with { Slug = slug }, ct));
```

| Body | Result |
|---|---|
| `{"name":"x"}` | 200 |
| `{"slug":"body-slug","name":"x"}` | **400**, empty body |

Logged (Debug, `RequestDelegateFactory`):
```
JsonException: The constructor parameter 'Slug' on type 'SlugCmd' doesn't allow null values.
Consider updating its nullability annotation. Path: $.slug
```

Isolated with the exact Minimal API serializer options:

| `RespectNullableAnnotations` | `RespectRequiredConstructorParameters` | Deserialize with `slug` in body |
|---|---|---|
| true | false | **JsonException** |
| false | true | OK (`Slug = null`) |
| false | false | OK (`Slug = null`) |

So a client that sends a member the API documents as a route value gets a 400 instead of having it ignored.

**Suggested fix:** the discard path must not hand `null` to a non-nullable member or constructor parameter. Add a .NET 9+ regression test with `RespectNullableAnnotations = true` and a non-nullable reference-type member.

### Finding 4 — Low-Medium (docs): README says OpenAPI hides excluded members; Swashbuckle still shows them

**Where:** [README.md:226](README.md#L226).

| Generator | Request body schema for `RecordInitCmd` / `PositionalCmd` |
|---|---|
| Microsoft.AspNetCore.OpenApi 10.0.12 (.NET 10) | `[name]` (matches README) |
| Swashbuckle.AspNetCore 6.5.0 (.NET 8) | **`[id, name]`** |

Swashbuckle builds schemas by reflection rather than from the System.Text.Json contract. Consumers therefore see `id` in the body schema, send it, and the value is silently discarded. The existing test checks the schema only under `#if NET9_0_OR_GREATER` with `JsonSchemaExporter`, so this gap is not covered.

**Suggested fix:** name the generators that honor it. For Swashbuckle, ship or document an `ISchemaFilter` that removes those members.

### Finding 5 — Medium (release): the JSON exclusion shipped in a patch release with no opt-out

**Where:** tag `v2.0.6` points at `f3cfac1`, which added `RequestBodyJsonOptionsSetup`. It is registered unconditionally by every `AddMediator` overload.

Before `v2.0.6`, a body-bound request with a `[FromRoute]` member read that member from the JSON body. From `v2.0.6` it is always `default`. Apps that used the attribute decoratively, or whose clients sent the id in the body, changed behavior on a patch update without any compile-time or runtime signal.

**Suggested fix:** call it out in release notes as a behavior change. Consider an option (for example on `MediatorOptions`) to turn it off, and use a minor or major version bump for behavior changes like this.

---

## 2. Dependency injection

### What each call registers

| Call | Package | Registers |
|---|---|---|
| `AddMediator(params Assembly[])` | `Phoenix.Mediator` | `MediatorOptions` + validator, `AddHealthChecks()`, the JSON body post-configure, an assembly registry, scoped `Mediator` with `ISender` forwarding to it ([ServiceCollectionExtensions.cs:25](Phoenix.Mediator/Mediator/ServiceCollectionExtensions.cs#L25)), then `AddMediatorHandlers` for assemblies not seen before |
| `AddMediatorHandlers(params Assembly[])` | `Phoenix.Mediator` | Every closed `IRequestHandler<>` / `IRequestHandler<,>` as transient via `TryAddTransient` ([line 92](Phoenix.Mediator/Mediator/ServiceCollectionExtensions.cs#L92)) |
| `AddMediatorSentry()` | `Phoenix.Mediator.Sentry` | Open-generic `SentryBehavior<,>` and `SentryBehavior<>` via `TryAddEnumerable` ([SentryServiceCollectionExtensions.cs:20](Phoenix.Mediator.Sentry/SentryServiceCollectionExtensions.cs#L20)) |
| `AddMediatorValidation(params Assembly[])` | `Phoenix.Mediator.Validation` | Open-generic `ValidationBehavior<,>` / `ValidationBehavior<>`, plus `AddValidatorsFromAssembly` per new assembly ([ValidationServiceCollectionExtensions.cs:17](Phoenix.Mediator.Validation/ValidationServiceCollectionExtensions.cs#L17)) |

Pipeline order is registration order: the first behavior registered runs outermost. The existing test `Pipeline_ExecutesBehaviorsOutermostFirstAtRuntime` confirms this for closed behaviors, and `AddMediator_DoesNotDuplicateBuiltInRegistrations` for the two open-generic ones.

### Why registration takes several calls

Commit `3f3ea03` ("Split pipeline behaviors/logging into companion packages", 2026-05-26) moved FluentValidation and Sentry out of the core package. Before it, `AddMediator(assemblies)` registered handlers, validators, `SentryBehavior` and `ValidationBehavior` in one call. The diff removed those registrations and the old `AddMediatorValidators` method from the core.

The split has two sound reasons:

1. **Dependency direction.** `Phoenix.Mediator.Validation` references the core, so the core cannot call `AddMediatorValidation` without taking a dependency on FluentValidation again. The same applies to Sentry.
2. **Behavior order.** Separate calls let the app choose which behavior is outermost.

The cost is that assemblies are passed twice, and each step can be skipped or misconfigured without any error (findings 6 and 7).

**Suggested shape:** keep the packages separate, but return a builder from one entry point, the way `AddAuthentication().AddJwtBearer()` and `AddHealthChecks().AddCheck()` work. A sketch:

```csharp
builder.Services.AddMediator(typeof(Program).Assembly, mediator => mediator
    .AddSentry()        // extension method on the builder, shipped in Phoenix.Mediator.Sentry
    .AddValidation());  // shipped in Phoenix.Mediator.Validation; reuses the builder's assemblies
```

The builder remembers the assemblies, so validation can't be registered against a different or empty set. `Phoenix.Mediator.All` currently ships no code (`IncludeBuildOutput=false`) and could also host a one-call convenience method, since it already references every package.

### Finding 6 — High: `internal` validators are silently not registered

**Where:** [ValidationServiceCollectionExtensions.cs:29](Phoenix.Mediator.Validation/ValidationServiceCollectionExtensions.cs#L29).

`AddValidatorsFromAssembly(assembly)` uses FluentValidation's default `includeInternalTypes: false`, so it only scans exported types. The handler scan uses `Assembly.GetTypes()` and does include internal handlers. Projects that make validators `internal sealed` get validation on none of their requests.

**Reproduced:**
```csharp
services.AddMediator().AddMediatorHandlers(...).AddMediatorValidation(typeof(Program).Assembly);

public sealed class PublicValidatedCmdValidator : AbstractValidator<PublicValidatedCmd> { ... NotEmpty() ... }
internal sealed class InternalValidatedCmdValidator : AbstractValidator<InternalValidatedCmd> { ... NotEmpty() ... }
```

| | Registrations | Sending with empty `Name` |
|---|---|---|
| public validator | 1 | `HttpResponseException 400: 'Name' must not be empty.` |
| internal validator | **0** | **handler ran, no validation** |

**Suggested fix:**
```csharp
services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
```

### Finding 7 — Medium: `AddMediatorValidation()` with no assemblies validates nothing

**Where:** [ValidationServiceCollectionExtensions.cs:25](Phoenix.Mediator.Validation/ValidationServiceCollectionExtensions.cs#L25).

`AddMediatorValidation()` compiles and runs. It registers the behavior, but because the assembly list is empty no validators are registered. Sending an invalid request reached the handler. `AddMediatorHandlers()` throws `ArgumentException` for the same mistake ([ServiceCollectionExtensions.cs:96](Phoenix.Mediator/Mediator/ServiceCollectionExtensions.cs#L96)), so the API is inconsistent. `AddMediator()` without assemblies has the same problem: no handlers, and the failure only appears at the first request.

**Suggested fix:** throw `ArgumentException("At least one assembly must be provided.")`, or log a warning when no validators end up registered.

### Finding 8 — Medium: generic types in scanned assemblies crash startup

**Where:** handler scan filter [ServiceCollectionExtensions.cs:103](Phoenix.Mediator/Mediator/ServiceCollectionExtensions.cs#L103); endpoint group filter [EndpointsExtensions.cs:93](Phoenix.Mediator/Web/EndpointsExtensions.cs#L93).

Neither filter skips types with `ContainsGenericParameters`.

**Generic handler (reproduced):**
```csharp
public sealed record GenericGet<T>(int Id) : IRequest<T>;
public sealed class GenericGetHandler<T> : IRequestHandler<GenericGet<T>, T> { ... }
```
`BuildServiceProvider()` threw:
```
ArgumentException: Cannot instantiate implementation type 'GenericGetHandler`1[T]'
for service type 'IRequestHandler`2[GenericGet`1[T],T]'.
```
The scan registers the partially open interface `IRequestHandler<GenericGet<T>, T>`, which Microsoft.Extensions.DependencyInjection rejects. Generic CRUD handlers are a common pattern.

**Generic endpoint group (reproduced):**
```csharp
public class CrudEndpoints<TEntity> : BaseEndpointGroup { ... }
```
`app.MapEndpoints()` threw:
```
MemberAccessException: Cannot create an instance of CrudEndpoints`1[TEntity]
because Type.ContainsGenericParameters is true.
```

**Suggested fix:** skip `type.ContainsGenericParameters` in both scans, with a log message or a clear exception explaining that open generic handlers must be closed explicitly.

### Finding 9 — Medium: `Send` with a variable typed as `IRequest` throws at runtime

**Where:** [Mediator.cs:31](Phoenix.Mediator/Mediator/Mediator.cs#L31), [AutoResponseMappingExtensions.cs:71](Phoenix.Mediator/Web/AutoResponseMappingExtensions.cs#L71).

C# overload resolution prefers `Send<TRequest>(TRequest) where TRequest : IRequest`, inferring `TRequest = IRequest`. The mediator then looks up `IRequestHandler<IRequest>`.

**Reproduced:**

| Code | Result |
|---|---|
| `IRequest cmd = new DeleteThing(); await sender.Send(cmd);` | `InvalidOperationException: No service for type 'IRequestHandler`1[IRequest]' has been registered.` |
| `IRequest cmd = new DeleteThing(); await sender.SendAsApiResult(cmd);` | same exception |
| `object cmd = new DeleteThing(); await sender.Send(cmd);` | works |

This breaks code that dispatches a list of commands (`List<IRequest>`) or builds commands polymorphically.

**Suggested fix:** in `Send<TRequest>` and `SendAsApiResult<TRequest>`, when `typeof(TRequest)` is an interface, dispatch by the runtime type through the existing wrapper cache (the `Send(object)` path).

### Finding 10 — Low-Medium: duplicate handlers are accepted silently

**Where:** [ServiceCollectionExtensions.cs:115](Phoenix.Mediator/Mediator/ServiceCollectionExtensions.cs#L115).

`TryAddTransient` keeps the first handler found for a request type and ignores the rest. With `DupHandlerA` and `DupHandlerB` both handling `DupReq`, startup succeeded and `Send` returned `"DupHandlerA"`. Which handler wins depends on type order in the assembly, so a copy-pasted handler can silently shadow the real one.

**Suggested fix:** when the scan itself finds two implementations of the same handler interface, throw and name both types. Keep `TryAdd` semantics for handlers the app registered manually before the scan.

### Finding 11 — Low *(from code)*: endpoint discovery depends on the registration API; no opt-outs

**Where:** [EndpointsExtensions.cs:131](Phoenix.Mediator/Web/EndpointsExtensions.cs#L131), [ServiceCollectionExtensions.cs:37](Phoenix.Mediator/Mediator/ServiceCollectionExtensions.cs#L37).

- `MapEndpoints()` scans the application/entry assembly, assemblies passed to `MapEndpoints(...)`, and assemblies in the registry. Only `AddMediator(assemblies)` fills that registry. An assembly registered through `AddMediatorHandlers(assembly)` has its handlers registered, but its endpoint groups are not discovered.
- Every `AddMediator` overload always calls `AddHealthChecks()` and installs the JSON body change (finding 5), with no way to turn either off.
- FluentValidation validators written for a base class or interface do not run for derived request types, because the behavior resolves `IValidator<TRequest>` for the exact type only.

---

## 3. Multipart requests and antiforgery tokens

### How it's implemented

`PostMultiPart`, `PutMultiPart` and `PatchMultiPart` ([EndpointsExtensions.cs:307](Phoenix.Mediator/Web/EndpointsExtensions.cs#L307)) map the route and call `ConfigureMultiPart` ([line 328](Phoenix.Mediator/Web/EndpointsExtensions.cs#L328)), which:

1. adds the default OpenAPI responses (`AddResponses`);
2. adds `.Accepts<IFormFile>("multipart/form-data")` and `.Accepts<IFormFileCollection>("multipart/form-data")`;
3. adds `RequestSizeLimitAttribute(maxRequestBodySize)`, default **5 MB** ([line 22](Phoenix.Mediator/Web/EndpointsExtensions.cs#L22));
4. adds `.WithRequestTimeout(TimeSpan.FromSeconds(timeoutSeconds))`, default **120 s**;
5. calls `.DisableAntiforgery()` **only** when `disableAntiforgery: true` is passed ([line 340](Phoenix.Mediator/Web/EndpointsExtensions.cs#L340)).

Phoenix adds no antiforgery metadata itself. ASP.NET Core infers "antiforgery token required" for any delegate with an `IFormFile`, `IFormFileCollection`, `IFormCollection` or `[FromForm]` parameter. A plain `app.MapPost("...", (IFormFile file) => ...)` behaved exactly like `PostMultiPart` in every scenario below.

### Observed behavior

Endpoints used:
```csharp
group.PostMultiPart("upload", (IFormFile file) => Results.Ok(new { file.FileName, file.Length }));
group.PostMultiPart("upload-noaf", (IFormFile file) => ..., disableAntiforgery: true);
group.PostMultiPart("manual", async (HttpRequest request) => { var form = await request.ReadFormAsync(); ... });
group.PostMultiPart("small", (IFormFile file) => ..., maxRequestBodySize: 1024, disableAntiforgery: true);
```

Results (identical on .NET 8 and .NET 10):

| App setup | Env | `upload`, no token | `upload`, valid cookie + `RequestVerificationToken` header | `upload-noaf` | `manual`, no token | `small`, 64 KB body |
|---|---|---|---|---|---|---|
| no antiforgery services (typical JWT API) | Production | **500** | — | 200 | 200 | 413 |
| no antiforgery services | Development | **500** | — | 200 | 200 | 413 |
| `AddAntiforgery()` only | Production | **500** | **500** | 200 | 200 | 413 |
| `AddAntiforgery()` only | Development | **500** | **500** | 200 | 200 | 413 |
| `AddAntiforgery()` + `app.UseAntiforgery()` | Production | 400, empty body | 200 | 200 | **200 (not validated)** | 413 |
| `AddAntiforgery()` + `app.UseAntiforgery()` | Development | **500** | 200 | 200 | **200 (not validated)** | 413 |

Every 500 response body was `{"errors":["Unknown error occurred"],"traceId":"..."}`. The server logs show the real causes:

- Without `UseAntiforgery()`:
  ```
  InvalidOperationException: Endpoint HTTP: POST d/upload contains anti-forgery metadata, but a middleware
  was not found that supports anti-forgery. Configure your application startup by adding app.UseAntiforgery() ...
  ```
- With `UseAntiforgery()`, Development, no token:
  ```
  BadHttpRequestException: Invalid anti-forgery token found when reading parameter "IFormFile file" from the request body as form.
  ```

### Finding 12 — High: default multipart endpoints return 500 unless antiforgery is fully configured

**Where:** [EndpointsExtensions.cs:337](Phoenix.Mediator/Web/EndpointsExtensions.cs#L337) (default `disableAntiforgery = false`); README has no antiforgery documentation.

With the defaults, an upload endpoint that takes `IFormFile`/`[FromForm]` works only when the app calls **both** `AddAntiforgery()` and `app.UseAntiforgery()`, and clients send a token. Registering `AddAntiforgery()` alone is not enough; the middleware is not added automatically.

Most Minimal APIs using this package authenticate with bearer tokens and register neither. Every upload then fails with a generic 500 that tells the client nothing. The package offers no helper for issuing tokens, and the README doesn't mention antiforgery or the multipart helpers' parameters.

**Suggested fix:**
- Document the choice explicitly: bearer-token APIs should pass `disableAntiforgery: true`; cookie-authenticated apps need `AddAntiforgery()`, `app.UseAntiforgery()`, a token endpoint, and clients sending the `RequestVerificationToken` header.
- At map time, when `disableAntiforgery` is false and `IAntiforgery` is not registered, throw or log a clear message naming both options.
- Optionally provide a token endpoint helper built on `IAntiforgery.GetAndStoreTokens(HttpContext)`.

### Finding 13 — Medium (security): "antiforgery ON by default" doesn't protect endpoints that read the form manually

**Where:** comment at [EndpointsExtensions.cs:337](Phoenix.Mediator/Web/EndpointsExtensions.cs#L337).

The comment says validation stays on so "cookie-authenticated uploads are not silently exposed to CSRF". That is only true when the delegate has form-bound parameters. The `manual` endpoint, which reads `HttpRequest.ReadFormAsync()`, returned **200 without a token even with `AddAntiforgery()` + `UseAntiforgery()`**. The antiforgery middleware only validates endpoints carrying antiforgery metadata, and only form parameter binding enforces the result.

**Suggested fix:** document the limitation. If protection must really be on by default, add `RequireAntiforgeryTokenAttribute` metadata when `disableAntiforgery` is false, plus an endpoint filter that rejects the request when `IAntiforgeryValidationFeature.IsValid` is false. Both types exist in .NET 8.

### Finding 14 — Medium: `timeoutSeconds` is ignored without timeout middleware; with it, timeouts become 500

**Where:** [EndpointsExtensions.cs:335](Phoenix.Mediator/Web/EndpointsExtensions.cs#L335) and [line 43](Phoenix.Mediator/Web/EndpointsExtensions.cs#L43) (`MapEndpoints` registers the error middleware at the point where it is called).

**Reproduced** with `timeoutSeconds: 1` and a handler that awaits `Task.Delay(3000, ct)`:

| Pipeline | Result |
|---|---|
| no `AddRequestTimeouts()` / `UseRequestTimeouts()` | **200 after 3,005 ms**: timeout silently not enforced |
| `app.UseRequestTimeouts();` then Phoenix middleware (the usual `app.UseRequestTimeouts(); app.MapEndpoints();`) | **500 after ~1,011 ms**, logged as `Error ... [TaskCanceledException]` |
| Phoenix middleware, then `app.UseRequestTimeouts()` | 504 after ~1,009 ms (correct) |

`WithRequestTimeout` only adds metadata; nothing enforces it unless the app registers the request timeout services and middleware. When they are registered in the usual order, the Phoenix middleware catches the cancellation first and converts it to a 500, so the timeout middleware never returns its 504. The default 120 s also covers the whole request, so large uploads over slow connections are cut off.

**Suggested fix:** document that `timeoutSeconds` needs `AddRequestTimeouts()` + `UseRequestTimeouts()`. Stop converting cancellations to 500 (see finding 22), so the timeout middleware can return 504.

### Finding 15 — Low-Medium: `maxRequestBodySize` can't exceed the form limit

**Where:** [EndpointsExtensions.cs:334](Phoenix.Mediator/Web/EndpointsExtensions.cs#L334).

`RequestSizeLimitAttribute` raises the server body-size limit for the endpoint, but form parsing has its own cap, `FormOptions.MultipartBodyLengthLimit`, which defaults to 128 MB. A `maxRequestBodySize` above that still fails.

**Reproduced with a scaled-down limit:** `FormOptions.MultipartBodyLengthLimit = 100_000`, endpoint `maxRequestBodySize: 10_000_000`, 300 KB upload:

| Env | Result |
|---|---|
| Production | 400, empty body |
| Development | 500, logged `BadHttpRequestException: Failed to read parameter "IFormFile file" from the request body as form.` |

**Suggested fix:** also apply the form limit per endpoint (.NET 8+):
```csharp
route.WithFormOptions(multipartBodyLengthLimit: maxRequestBodySize);
```
When hosting behind IIS, `maxAllowedContentLength` (30 MB by default) also applies.

### Finding 16 — Low: size/token failures bypass the error format; `Accepts` metadata overwritten

- An over-limit upload returned **413 with an empty body**, and a missing token in Production returned **400 with an empty body**. Neither uses the documented `{"errors":[...],"traceId":"..."}` shape.
- *(from code)* `.Accepts<IFormFileCollection>` is added after `.Accepts<IFormFile>` ([line 332](Phoenix.Mediator/Web/EndpointsExtensions.cs#L332)). ApiExplorer reads the last `IAcceptsMetadata`, so the first call has no effect.
- *(from code)* `responseDtos` is a plain optional array on the multipart helpers but `params` on `Get`/`Post`/`Put`/`Patch`/`Delete`.

**What works:** the endpoint size limit is enforced (64 KB to a 1 KB endpoint gave 413 in both environments), and form binding works for classes, `init`-only classes, and both kinds of record.

---

## 4. Roles

### How roles are mapped

`RequireRole<TBuilder, TRole>(params TRole[] roles)` ([AuthorizationExtensions.cs:25](Phoenix.Mediator/Web/AuthorizationExtensions.cs#L25)):

1. With no roles, it calls `RequireAuthorization()`: any authenticated user.
2. Otherwise it joins `role.ToString()` values with `","` ([line 35](Phoenix.Mediator/Web/AuthorizationExtensions.cs#L35)) and adds `new AuthorizeAttribute { Roles = "Admin,Manager" }`.
3. ASP.NET Core splits `Roles` on commas, trims each value, and creates one requirement where **any** listed role passes.
4. The requirement calls `User.IsInRole(role)`. That checks claims whose type equals the identity's `RoleClaimType` (by default `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`) and compares values **case-sensitively**.

### Observed behavior

A test authentication handler created role claims from request headers. Endpoints:
```csharp
group.Get("admin", ...).RequireRole(AppRole.Admin);
group.Get("admin-or-manager", ...).RequireRole(AppRole.Admin, AppRole.Manager);
group.MapGroup("admin-group").RequireRole(AppRole.Admin).Get("manager", ...).RequireRole(AppRole.Manager);
group.Get("flags", ...).RequireRole(PermRole.Admin | PermRole.Manager);   // [Flags] enum
group.Get("throws-unauthorized-access", () => { throw new UnauthorizedAccessException("..."); }).RequireRole(AppRole.User);
```

| Endpoint | `IAuthorizeData.Roles` metadata | Caller | Result |
|---|---|---|---|
| `admin` | `"Admin"` | anonymous | 401 |
| | | role `Admin` | 200 |
| | | role `admin` | **403** (case-sensitive) |
| | | `Admin` in a claim of type `"role"` | **403** (wrong claim type) |
| `admin-or-manager` | `"Admin,Manager"` | role `Manager` | 200 (OR) |
| `admin-group/manager` | `"Admin"`, `"Manager"` (two entries) | role `Manager` only | **403** |
| | | role `Admin` only | **403** |
| | | roles `Admin` and `Manager` | 200 (AND) |
| `flags` | `"Admin, Manager"` | role `Manager` | 200 (flags combination became OR) |
| `throws-unauthorized-access` | `"User"` | role `User` | **401, empty body, nothing logged** |

### Finding 17 — Medium: enum names must match role claims exactly, with no mapping

**Where:** [AuthorizationExtensions.cs:35](Phoenix.Mediator/Web/AuthorizationExtensions.cs#L35).

- Values are compared case-sensitively. An identity provider issuing `admin` does not satisfy `AppRole.Admin`.
- Role values that aren't valid C# identifiers (`super-admin`, `Tenant.Admin`, `role:reader`) can't be expressed.
- Renaming an enum member silently changes which users are authorized, and nothing fails at compile time.
- Failures are a bare 403 with no hint, which makes claim-type problems hard to diagnose. Check each app's claim setup: with JwtBearer and `MapInboundClaims = false`, set `TokenValidationParameters.RoleClaimType` to the claim name the token actually uses (for example `"role"`). Roles nested inside another claim, such as Keycloak's `realm_access.roles`, need a claims transformation before `IsInRole` can see them.

**Suggested fix:** support a name mapping, for example `[EnumMember(Value = "super-admin")]` on enum members, or an overload taking `Func<TRole, string>`. Reject names that are empty or contain commas.

### Finding 18 — Medium: `UnauthorizedAccessException` becomes 401 with no body and no log

**Where:** [ExceptionHandlingMiddleware.cs:34](Phoenix.Mediator/Web/Middlewares/ExceptionHandlingMiddleware.cs#L34) and [line 66](Phoenix.Mediator/Web/Middlewares/ExceptionHandlingMiddleware.cs#L66).

The middleware maps `UnauthorizedAccessException` to 401, writes no body, and has no logging call. Reproduced both from an authorized endpoint (above) and from `GET /e/unauthorized-access` throwing `Access to the path 'D:\uploads\a.bin' is denied.`: 401, empty body, no Warning or Error logged.

Two problems:
- Handlers often throw this exception for "you don't own this resource", which is a 403. A 401 tells clients the login is invalid, and many clients respond by refreshing tokens or logging the user out.
- .NET throws the same exception for **file-system permission errors**, for example when saving an uploaded file. A server misconfiguration then looks like an auth failure and leaves nothing in the logs.

**Suggested fix:** stop mapping the BCL exception. Treat it as a 500 with an error log, and provide explicit `UnauthorizedException` (401) / `ForbiddenException` (403) types derived from `HttpResponseException` for handlers. At minimum, log it.

### Finding 19 — Low-Medium: `[Flags]`/undefined values accepted; stacked calls are AND

- A `[Flags]` combination such as `PermRole.Admin | PermRole.Manager` becomes `"Admin, Manager"`, meaning **either** role. A reader of `RequireRole(Admin | Manager)` may expect **both**.
- *(from code)* `PermRole.None` becomes the role `"None"`, and an undefined value such as `(AppRole)42` becomes the role `"42"`.
- Stacking is AND. Two `RequireRole` calls, or a group-level plus an endpoint-level call, add two separate requirements (reproduced above). The README only describes OR within a single call ([README.md:276](README.md#L276)).

**Suggested fix:** reject `[Flags]` enums (`typeof(TRole).IsDefined(typeof(FlagsAttribute), false)`) and undefined values (`Enum.IsDefined`), and document the AND behavior.

---

## 5. Other bugs

### Error-handling middleware

#### Finding 20 — Medium: `BadHttpRequestException` becomes 500 in Development; empty 400 bodies in Production

**Where:** [ExceptionHandlingMiddleware.cs:38](Phoenix.Mediator/Web/Middlewares/ExceptionHandlingMiddleware.cs#L38) (catch-all) and [line 78](Phoenix.Mediator/Web/Middlewares/ExceptionHandlingMiddleware.cs#L78) (status mapping).

In Development, ASP.NET Core throws `BadHttpRequestException` for client errors instead of returning 400 (`RouteHandlerOptions.ThrowOnBadRequest` defaults to true there). The middleware ignores the exception's `StatusCode` and returns 500 with "Unknown error occurred".

| Request | Production | Development |
|---|---|---|
| README quick-start `GetGreetingQuery` without `?name=` | 400, empty body | **500**, `Required parameter "string Name" was not provided from query string.` |
| Malformed JSON body | 400, empty body | **500**, `Failed to read parameter "ClassSetCmd cmd" from the request body as JSON.` |
| Missing antiforgery token (finding 12) | 400, empty body | **500** |
| Form larger than the form limit (finding 15) | 400, empty body | **500** |

Developers see server errors for their own bad requests, and in Production client errors come back without the documented `{errors, traceId}` body.

**Suggested fix:** handle `BadHttpRequestException` explicitly using `ex.StatusCode` and the standard body. To get the same body in Production, `AddMediator` can set `RouteHandlerOptions.ThrowOnBadRequest = true` (configurable) so those failures reach the middleware.

#### Finding 21 — Medium: `ArgumentException` -> 400 and `KeyNotFoundException` -> 404 hide server bugs

**Where:** [ExceptionHandlingMiddleware.cs:78](Phoenix.Mediator/Web/Middlewares/ExceptionHandlingMiddleware.cs#L78).

```csharp
KeyNotFoundException => HttpStatusCode.NotFound,
ArgumentException => HttpStatusCode.BadRequest,
```

`ArgumentException` includes `ArgumentNullException` and `ArgumentOutOfRangeException`, which are usually thrown by server-side code (configuration, EF Core, framework calls), not caused by the client.

| Handler throws | Response |
|---|---|
| `ArgumentNullException("connectionString")` | **400** `{"errors":["Unknown error occurred"],...}` |
| `KeyNotFoundException` (dictionary lookup) | **404** `{"errors":["Unknown error occurred"],...}` |
| `FluentValidation.ValidationException` (from `ValidateAndThrowAsync` inside a handler) | **500** |

Server bugs stop showing up as 5xx in monitoring, and clients are told they sent a bad request.

**Suggested fix:** map only the library's own exception types (`HttpResponseException`, `BadRequestException`, `NotFoundException`). Treat other exceptions as 500. Consider mapping `FluentValidation.ValidationException` to 400 in the Validation package.

#### Finding 22 — Medium: cancellations are logged as errors and returned as 500

**Where:** [ExceptionHandlingMiddleware.cs:38](Phoenix.Mediator/Web/Middlewares/ExceptionHandlingMiddleware.cs#L38).

`OperationCanceledException` falls into the catch-all. A request timeout produced `Error ExceptionHandlingMiddleware: Unhandled exception for POST /t/slow [TaskCanceledException]` and a 500 (finding 14). Client disconnects take the same code path *(from code)*, so every aborted request adds an Error log.

**Suggested fix:** when `context.RequestAborted.IsCancellationRequested`, don't treat the exception as a server error. Rethrow it, so `UseRequestTimeouts` can return 504, or end the response without an Error-level log.

### Logging and Sentry packages

#### Finding 23 — High: log files stop being written after 50 MB per day

**Where:** [LoggingExtension.cs:121](Phoenix.Mediator.Serilog/LoggingExtension.cs#L121) (all three file sinks).

The sinks set `fileSizeLimitBytes: 50_000_000` without `rollOnFileSizeLimit: true`. Serilog then stops writing to a file once it reaches the limit, until the next daily roll.

**Reproduced** with the same sink settings and a 2 KB limit, writing 100 events:

| `rollOnFileSizeLimit` | Files | Bytes | Last event written |
|---|---|---|---|
| false (current) | 1 | 2,038 | **no** (events silently dropped) |
| true | 5 | 8,892 | yes |

This gets worse because `exceptions-.log` receives every Warning, and the middleware logs each `HttpResponseException` at Warning with its stack trace ([ExceptionHandlingMiddleware.cs:46](Phoenix.Mediator/Web/Middlewares/ExceptionHandlingMiddleware.cs#L46)). Every validation failure and 404 therefore writes a stack trace to that file. A burst of bad client requests can fill it and hide real errors for the rest of the day.

**Suggested fix:** add `rollOnFileSizeLimit: true` to all three sinks. Note that `retainedFileCountLimit` counts files, not days, once files also roll by size. Consider logging expected 4xx exceptions without the stack trace, or at Information.

#### Finding 24 — Medium: `TracesSampleRate` is parsed with the server's culture

**Where:** [SentryLoggingExtensions.cs:70](Phoenix.Mediator.Serilog.Sentry/SentryLoggingExtensions.cs#L70), used by both `AddSentry()` and `WriteToSentry()`.

```csharp
double.TryParse(configuration["Sentry:TracesSampleRate"], out var configuredRate)
```

JSON configuration stores `0.2` as the string `"0.2"`, which is then parsed with the current culture.

**Reproduced** with `{"Sentry":{"TracesSampleRate":0.2}}`:

| Server culture | Effective rate |
|---|---|
| en-US | 0.2 |
| **ar-IQ** | **0.1** (parse fails, default used, configuration ignored) |
| fr-FR, ru-RU | 0.1 (ignored) |
| **de-DE, tr-TR** | **1.0** (`"0.2"` parsed as 2, clamped: every request traced) |

This affects hosts whose culture isn't invariant or English, such as Windows servers with Arabic regional settings.

**Suggested fix:**
```csharp
double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var configuredRate)
```

#### Finding 25 — Medium-Low: the README Sentry setup initializes the SDK twice

**Where:** [SentryLoggingExtensions.cs:21](Phoenix.Mediator.Serilog.Sentry/SentryLoggingExtensions.cs#L21) and [line 44](Phoenix.Mediator.Serilog.Sentry/SentryLoggingExtensions.cs#L44); README setup at [README.md:306](README.md#L306).

The README recommends calling both `builder.AddSentry()` (which uses `UseSentry`) and `lc.WriteToSentry(builder.Configuration)`. `WriteToSentry` sets `Dsn` on the Serilog sink, which makes the sink initialize the SDK on its own.

**Reproduced:** `SentrySdk.IsEnabled` was `False` before creating a logger with only `WriteToSentry(config)` and `True` right after, with no `AddSentry()` involved.

The two initializations use different options: `Environment` is set only by `AddSentry`, while `Release` and `MaxBreadcrumbs` are set only by `WriteToSentry`.

**Suggested fix:** when used together with `AddSentry()`, don't initialize the SDK from the sink (`o.InitializeSdk = false`, or don't set `Dsn`). Expose that choice as a parameter.

#### Additional note — Low *(from code)*: file logging defaults and hard-coded levels

**Where:** [LoggingExtension.cs:80](Phoenix.Mediator.Serilog/LoggingExtension.cs#L80).

- `enableFileLogging` defaults to `true`, and `Directory.CreateDirectory({ContentRoot}/logs)` runs during host setup ([line 93](Phoenix.Mediator.Serilog/LoggingExtension.cs#L93)). On read-only container filesystems this fails at startup.
- Minimum levels are hard-coded ([line 104](Phoenix.Mediator.Serilog/LoggingExtension.cs#L104)), so they can't be tuned from `appsettings.json`.

### Wrappers and web helpers

#### Finding 26 — Low-Medium: `MultiResponse<T>` cannot be deserialized

**Where:** [MultiResponse.cs:3](Phoenix.Mediator/Wrappers/MultiResponse.cs#L3).

The only constructor is `(List<T> data, int totalCount, int pageSize)`, but there is no `PageSize` property, only a computed `PagesCount`. System.Text.Json requires every constructor parameter to match a property.

**Reproduced:** serializing gave `{"data":[1,2,3],"totalCount":3,"pagesCount":2}`. Deserializing it threw:
```
InvalidOperationException: Each parameter in the deserialization constructor on type 'MultiResponse`1[System.Int32]'
must bind to an object property or field on deserialization.
```
`SingleResponse<T>` round-trips correctly. .NET clients (Blazor, other services) and integration tests using `ReadFromJsonAsync<MultiResponse<T>>` fail.

**Suggested fix:** add a public `PageSize` property. It is also useful to clients, and it lets the existing constructor bind.

#### Finding 27 — Low: `GroupName` uses culture-sensitive `ToLower()`

**Where:** [BaseEndpointGroup.cs:24](Phoenix.Mediator/Web/BaseEndpointGroup.cs#L24).

**Reproduced:** `new InvoiceEndpoints().GroupName` returned `"invoice"` on en-US and **`"ınvoice"`** (dotless ı) on tr-TR and az-Latn-AZ. Used as `app.MapGroup(GroupName)` (the README pattern), that produces a different public URL depending on the server culture.

*(from code)* Also:
- `Replace("Endpoints", "")` removes every occurrence, not just the suffix.
- The XML doc says the name is used "for grouping Swagger documentation", but the library never applies it (no `WithTags`/`WithGroupName`).
- Because the class name becomes the route prefix, renaming the class changes public URLs.

**Suggested fix:** `ToLowerInvariant()`, strip only a trailing `Endpoints`, and apply `WithTags(GroupName)` or fix the doc.

#### Finding 28 — Low: null `IRequest<T>` results return 204 regardless of configuration; error schema lacks `traceId`

**Where:** [AutoResponseMappingExtensions.cs:56](Phoenix.Mediator/Web/AutoResponseMappingExtensions.cs#L56) and [line 65](Phoenix.Mediator/Web/AutoResponseMappingExtensions.cs#L65); [EndpointsExtensions.cs:168](Phoenix.Mediator/Web/EndpointsExtensions.cs#L168).

- **Reproduced:** with `EmptyResponseStatusCode = Ok`, an `IRequest<string?>` handler returning `null` produced **204** through both `SendAsApiResult(object)` and `SendAsApiResult<TRequest, TResponse>`. OpenAPI inference advertises 200 with a `string` body for that endpoint ([EndpointsExtensions.cs:231](Phoenix.Mediator/Web/EndpointsExtensions.cs#L231)).
- *(from code)* The 400/500 OpenAPI schema is `ErrorsResponse`, which has only `errors`. The middleware's actual body also contains `traceId`.
- *(from code)* `ToApiResult` on an `ErrorResponse` value writes `{errors}` without `traceId` ([line 34](Phoenix.Mediator/Web/AutoResponseMappingExtensions.cs#L34)), a different shape from the middleware.

### Project, docs, architecture

#### Finding 29 — High (process): CI never runs

**Where:** [.github/workflows/ci.yml:5](.github/workflows/ci.yml#L5).

```yaml
on:
  push:
    branches: ["main"]
  pull_request:
    branches: ["main"]
```

The default branch is `master` (`origin/HEAD -> origin/master`), and no `main` branch exists. Pushes and pull requests never trigger CI; tests only run inside `release.yml` when a `v*` tag is pushed.

**Suggested fix:** `branches: ["master"]` for both triggers.

#### Finding 30 — Low: README logging snippets use a namespace that does not exist

**Where:** [README.md:294](README.md#L294) and [README.md:304](README.md#L304): `using Phoenix.Mediator.Extensions;`.

The actual namespaces are `Phoenix.Mediator.Serilog` ([LoggingExtension.cs:13](Phoenix.Mediator.Serilog/LoggingExtension.cs#L13)) and `Phoenix.Mediator.Serilog.Sentry` ([SentryLoggingExtensions.cs:8](Phoenix.Mediator.Serilog.Sentry/SentryLoggingExtensions.cs#L8)). Copy-pasted README code doesn't compile.

#### Finding 31 — Low (architecture): the core package forces ASP.NET Core onto every consumer

**Where:** [Phoenix.Mediator.csproj:35](Phoenix.Mediator/Phoenix.Mediator.csproj#L35) (`FrameworkReference Include="Microsoft.AspNetCore.App"`).

`IRequest`, `IRequestHandler` and `ISender` live in the same package as the web helpers. An Application or Domain class library that only defines requests and handlers therefore depends on the entire ASP.NET Core shared framework, and so do worker services and tests that reference it.

**Suggested fix:** move the abstractions and the mediator itself into a dependency-free package (for example `Phoenix.Mediator.Abstractions`), and keep the web helpers in a package that references ASP.NET Core.

---

## Test coverage gaps

The existing 31 tests don't exercise:
- multipart helpers, antiforgery, request size limits or request timeouts;
- `RequireRole` at runtime (only `RequireAuthorization` metadata is checked);
- form binding, `[AsParameters]` binding, or excluded members sent in a request body;
- `BadHttpRequestException`, cancellation, or `UnauthorizedAccessException` handling;
- anything in `Phoenix.Mediator.Serilog` or `Phoenix.Mediator.Serilog.Sentry` (the test project doesn't reference them);
- internal validators, empty assembly lists, generic types in scanned assemblies, or duplicate handlers.

## What works well

- Pipeline ordering, the scoped mediator, `ISender` forwarding and option validation behave as documented.
- `[AsParameters]` binding is correct for classes and both kinds of record.
- The JSON exclusion works for all JSON-bound shapes and is honored by Microsoft.AspNetCore.OpenApi on .NET 10.
- Endpoint request size limits are enforced (413).
- Error messages are localized correctly (Arabic/English), and error bodies carry `traceId`.

## Suggested fix order

1. **CI:** trigger on `master` (finding 29), so the fixes below get tested.
2. **Logging:** `rollOnFileSizeLimit: true`, and parse `TracesSampleRate` with `CultureInfo.InvariantCulture` (findings 23, 24).
3. **Validation:** `includeInternalTypes: true`, and throw when no assemblies are passed (findings 6, 7).
4. **Error middleware:** handle `BadHttpRequestException` by its status code, stop treating cancellations as 500s, and remove the blanket `ArgumentException`/`KeyNotFoundException`/`UnauthorizedAccessException` mappings (findings 18, 20, 21, 22).
5. **Multipart:** fail fast or document antiforgery, add `WithFormOptions`, and document timeout requirements (findings 12–15).
6. **Binding:** fix the .NET 9+ null case, document the record/class and form limitations, correct the OpenAPI claim, and consider an opt-out (findings 1–5).
7. **Scanning:** skip generic types, reject duplicate handlers, and dispatch `IRequest`-typed sends by runtime type (findings 8–10).
8. **Roles:** add role name mapping, reject `[Flags]`/undefined values, and document AND stacking (findings 17, 19).
9. **Smaller items:** `MultiResponse<T>` (26), `ToLowerInvariant` (27), response consistency (16, 28), double Sentry initialization (25), README namespace (30), abstractions package (31).
