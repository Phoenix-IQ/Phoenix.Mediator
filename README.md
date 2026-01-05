# Phoenix.Mediator

`Phoenix.Mediator` is a lightweight mediator implementation with optional pipeline behaviors (validation + Sentry) and a few helpers for ASP.NET Core Minimal APIs.

## Install

- NuGet: `Phoenix.Mediator`

## Basic usage

Register in DI:

```csharp
services.AddMediator();
// or: services.AddMediator(typeof(SomeHandler).Assembly);
```

> Note: `ISender` is registered as a **scoped** service so handlers can safely depend on scoped services (e.g. `DbContext`, current-user services).

When you pass assemblies to `AddMediator(...)`, request handlers **and FluentValidation validators** from those assemblies are registered.

Send requests:

```csharp
var result = await sender.Send(new MyRequest(), ct);
```


