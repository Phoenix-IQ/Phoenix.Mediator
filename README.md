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

Send requests:

```csharp
var result = await sender.Send(new MyRequest(), ct);
```


