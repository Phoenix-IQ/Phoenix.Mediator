using System.Net;
using System.Text;
using System.Text.Json;
#if NET9_0_OR_GREATER
using System.Text.Json.Schema;
#endif
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using Phoenix.Mediator.Mediator;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using Xunit;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// End-to-end coverage for <c>RequestBodyJsonOptionsSetup</c> and the README section
/// "Route, query, and header members in body requests", driven over real HTTP through
/// <c>Microsoft.AspNetCore.TestHost</c>.
/// <para>
/// Everything here is about one guarantee: a mediator-request member marked <c>[FromRoute]</c>,
/// <c>[FromQuery]</c> or <c>[FromHeader]</c> is bound from the request line/headers by the endpoint,
/// never from the JSON body. Unit tests over <c>JsonSerializerOptions</c> cannot prove that - only a
/// request that actually travels through the Minimal API body binder can, because the binder is what
/// decides which serializer options are used and what a rejected member does to the status code.
/// </para>
/// <para>
/// A note on how these tests are kept falsifiable: when the endpoint assigns the route value back
/// (<c>command with { Id = id }</c>), asserting <c>Id == 5</c> proves nothing about the exclusion - the
/// assignment produces 5 whether or not the body value was dropped first. So every such test also asserts
/// on a member the endpoint does <b>not</b> assign, whose body value must therefore have been discarded.
/// </para>
/// </summary>
public sealed class BindingRouteHttpTests
{
    // ---------------------------------------------------------------------------------------------
    // record with init-only members - the shape the README's table says you update with `with { }`
    // ---------------------------------------------------------------------------------------------

    // The headline guarantee: the caller posts "id": 999 to /5 and the handler sees 5. Without the
    // exclusion the body wins over the URL, so anyone can act on a record they cannot reach by URL.
    // `Force` carries the proof: the body sets it, the endpoint never assigns it, so a handler that
    // still sees `true` means the body reached the request.
    [Fact]
    public async Task Post_RecordWithFromRouteMember_HandlerSeesTheRouteValueNotTheBodyValue()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingRouteRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-record-route").Post("{id}",
            (ISender sender, int id, BindingRouteRecord command, CancellationToken ct) =>
                sender.Send<BindingRouteRecord, BindingUnit>(command with { Id = id }, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-record-route/5",
            """{"id":999,"force":true,"name":"x","amount":12.5}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteRecord>();
        Assert.Equal(5, received.Id);
        Assert.False(received.Force);
        Assert.Equal("x", received.Name);
        Assert.Equal(12.5m, received.Amount);
    }

    // The README warns that a forgotten `with { Id = id }` silently yields 0. That is bad, but it is
    // far better than 999: prove the body value never survives even when the endpoint assigns nothing.
    [Fact]
    public async Task Post_RecordWithFromRouteMember_LeavesItAtDefaultWhenTheEndpointDoesNotAssignIt()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingRouteRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-record-unassigned").Post("{id}",
            (ISender sender, BindingRouteRecord command, CancellationToken ct) =>
                sender.Send<BindingRouteRecord, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-record-unassigned/5",
            """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteRecord>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    // Every non-body source in one request: route, query, query-with-an-explicit-name, and header.
    // A regression that only re-enabled one of the four attributes would slip past a route-only test.
    [Fact]
    public async Task Post_Record_IgnoresBodyValuesForEveryNonBodySourceAtOnce()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingRouteRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-record-allsources").Post("{id}",
            (ISender sender, BindingRouteRecord command, CancellationToken ct) =>
                sender.Send<BindingRouteRecord, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-record-allsources/5",
            """{"id":999,"force":true,"q":"body-search","search":"body-search","tenant":"body-tenant","name":"x","amount":3.25}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteRecord>();
        Assert.Equal(0, received.Id);
        Assert.False(received.Force);
        Assert.Null(received.Search);
        Assert.Null(received.Tenant);
        // The unannotated members are untouched - the filter is per member, not per type.
        Assert.Equal("x", received.Name);
        Assert.Equal(3.25m, received.Amount);
    }

    // The positive half for query and header: the values the endpoint reads off the request line and
    // the headers do reach the handler, so excluding them from the body loses nothing.
    [Fact]
    public async Task Post_Record_BindsQueryAndHeaderValuesWhenTheEndpointAssignsThem()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingRouteRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-record-assigned").Post("{id}",
            (ISender sender,
             int id,
             bool force,
             [FromQuery(Name = "q")] string? q,
             [FromHeader(Name = BindingConstants.TenantHeader)] string? tenant,
             BindingRouteRecord command,
             CancellationToken ct) =>
                sender.Send<BindingRouteRecord, BindingUnit>(
                    command with { Id = id, Force = force, Search = q, Tenant = tenant }, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(
            client,
            "/binding-record-assigned/5?force=true&q=real-search",
            """{"id":999,"force":false,"q":"body-search","tenant":"body-tenant","name":"x"}""",
            (BindingConstants.TenantHeader, "real-tenant"));

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteRecord>();
        Assert.Equal(5, received.Id);
        Assert.True(received.Force);
        Assert.Equal("real-search", received.Search);
        Assert.Equal("real-tenant", received.Tenant);
    }

    // The wire name is not what the exclusion keys on. An app that turns camelCase off sends "Id", and a
    // filter that had degenerated into a name match would let the PascalCase spelling straight through.
    [Fact]
    public async Task Post_WhenTheAppTurnsOffCamelCaseNaming_StillExcludesTheMemberUnderItsPascalCaseWireName()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder, static services =>
            services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.PropertyNamingPolicy = null));
        AddCapturingHandler<BindingPositionalRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-pascalcase").Post("{id}",
            (ISender sender, BindingPositionalRecord command, CancellationToken ct) =>
                sender.Send<BindingPositionalRecord, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-pascalcase/5",
            """{"Id":999,"Force":true,"Name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingPositionalRecord>();
        Assert.Equal(0, received.Id);
        Assert.False(received.Force);
        Assert.Equal("x", received.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // positional record - the attribute sits on the constructor parameter, not on the property
    // ---------------------------------------------------------------------------------------------

    // Positional records are the shape most likely to regress: the generated property carries no
    // attribute at all, so the exclusion has to match the constructor parameter by name instead.
    [Fact]
    public async Task Post_PositionalRecord_HandlerSeesTheRouteValueNotTheBodyValue()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingPositionalRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-positional-route").Post("{id}",
            (ISender sender, int id, BindingPositionalRecord command, CancellationToken ct) =>
                sender.Send<BindingPositionalRecord, BindingUnit>(command with { Id = id }, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-positional-route/7",
            """{"id":999,"force":true,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingPositionalRecord>();
        Assert.Equal(7, received.Id);
        Assert.False(received.Force);
        Assert.Equal("x", received.Name);
    }

    // Reading a request body is a STREAMING deserialization: the reader can sit on a buffer that does not
    // yet hold the rest of the payload. A discard converter that called Utf8JsonReader.Skip() there threw
    // "Cannot skip tokens on partial JSON", which the framework turned into a 400 — for exactly the request
    // this feature exists to neutralize, and only over real HTTP, so a synchronous Deserialize(string) test
    // never saw it. This body is large enough to cross the stream buffer many times and parks a deep nested
    // value on the excluded member, which is the shape that needs the most read-ahead to skip.
    [Fact]
    public async Task Post_PositionalRecordWithALargeStreamedBody_DiscardsTheExcludedMemberInsteadOfFailing()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingPositionalRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-positional-large").Post("{id}",
            (ISender sender, int id, BindingPositionalRecord command, CancellationToken ct) =>
                sender.Send<BindingPositionalRecord, BindingUnit>(command with { Id = id }, ct));

        using var client = await StartAsync(app);

        var filler = new string('z', 200_000);
        var body = "{\"name\":\"" + filler + "\",\"id\":{\"nested\":{\"deep\":[\"" + filler + "\",999]}},\"force\":true}";

        var (status, responseBody) = await PostJsonAsync(client, "/binding-positional-large/7", body);

        AssertOk(status, responseBody);
        var received = recorder.Last<BindingPositionalRecord>();
        Assert.Equal(7, received.Id);
        Assert.False(received.Force);
        Assert.Equal(filler, received.Name);
    }

    [Fact]
    public async Task Post_PositionalRecord_LeavesItAtDefaultWhenTheEndpointDoesNotAssignIt()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingPositionalRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-positional-unassigned").Post("{id}",
            (ISender sender, BindingPositionalRecord command, CancellationToken ct) =>
                sender.Send<BindingPositionalRecord, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-positional-unassigned/7",
            """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingPositionalRecord>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    // A constructor parameter is the only shape where System.Text.Json actually calls the discard
    // converter: with no setter, an ordinary property is skipped by the serializer itself. So these four
    // payloads are the converter's own Read path over each token type it has to swallow - a string, a
    // nested object, an array and a null - none of which can be parsed into the `int` the parameter wants.
    [Theory]
    [InlineData("""{"id":"not-a-number","name":"x"}""")]
    [InlineData("""{"id":{"nested":{"deep":[1,2]}},"name":"x"}""")]
    [InlineData("""{"id":[1,2,3],"name":"x"}""")]
    [InlineData("""{"id":null,"name":"x"}""")]
    public async Task Post_PositionalRecordConstructorParameterWithAJsonValueOfTheWrongShape_IsDiscarded(string json)
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingPositionalRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-positional-shape").Post("{id}",
            (ISender sender, BindingPositionalRecord command, CancellationToken ct) =>
                sender.Send<BindingPositionalRecord, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-positional-shape/5", json);

        AssertOk(status, body);
        var received = recorder.Last<BindingPositionalRecord>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    // A reference-type constructor parameter goes down a different branch of the exclusion (IsSetNullable,
    // and a converter whose `default` is null rather than 0), so cover it separately from the `int` one.
    [Fact]
    public async Task Post_PositionalRecordWithAReferenceTypeConstructorParameter_DiscardsTheBodyValue()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingNonNullablePositionalRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-positional-reference").Post("{slug}",
            (ISender sender, BindingNonNullablePositionalRequest command, CancellationToken ct) =>
                sender.Send<BindingNonNullablePositionalRequest, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-positional-reference/real-slug",
            """{"slug":"from-body","name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingNonNullablePositionalRequest>();
        Assert.Null(received.Slug);
        Assert.Equal("x", received.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // hand-written constructors - a class is not a record, but it binds through a constructor all the same
    // ---------------------------------------------------------------------------------------------

    // Get-only properties filled by a constructor: the attribute is on the property, the value arrives
    // through the parameter. This is the shape the README calls "not possible to assign" - it must still
    // be impossible to *inject* from the body, or such a command is writable by any caller and fixable by
    // none.
    [Fact]
    public async Task Post_ClassWithAConstructor_DiscardsTheBodyValueForItsAnnotatedProperty()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingConstructorClass>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-ctor-class").Post("{id}",
            (ISender sender, BindingConstructorClass command, CancellationToken ct) =>
                sender.Send<BindingConstructorClass, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-ctor-class/5", """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingConstructorClass>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    // Here the attribute is on the constructor parameter `id` while the property is `Id`, so the exclusion
    // only fires if the parameter lookup is case-insensitive - which is the normal C# convention for a
    // hand-written constructor. An ordinal comparison would leave this request writable from the body.
    [Fact]
    public async Task Post_ClassWithTheAttributeOnACamelCasedConstructorParameter_StillDiscardsTheBodyValue()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingParameterAttributeClass>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-ctor-param").Post("{id}",
            (ISender sender, BindingParameterAttributeClass command, CancellationToken ct) =>
                sender.Send<BindingParameterAttributeClass, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-ctor-param/5", """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingParameterAttributeClass>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    // The streaming path again, for the other constructor-bound shape and with the excluded member placed
    // *after* a payload far larger than any read buffer: by then the reader is mid-stream, which is exactly
    // the state in which Skip() used to throw and turn a valid request into a 400.
    [Fact]
    public async Task Post_ClassWithAConstructorAndALargeStreamedBody_DiscardsTheTrailingExcludedMember()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingParameterAttributeClass>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-ctor-large").Post("{id}",
            (ISender sender, BindingParameterAttributeClass command, CancellationToken ct) =>
                sender.Send<BindingParameterAttributeClass, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var filler = new string('z', 200_000);
        var body = "{\"name\":\"" + filler + "\",\"id\":{\"nested\":[\"" + filler + "\",999]}}";

        var (status, responseBody) = await PostJsonAsync(client, "/binding-ctor-large/5", body);

        AssertOk(status, responseBody);
        var received = recorder.Last<BindingParameterAttributeClass>();
        Assert.Equal(0, received.Id);
        Assert.Equal(filler, received.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // class with settable properties - the README's third writable shape (`command.Id = id;`)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Post_ClassWithSettableProperties_HandlerSeesTheRouteValueNotTheBodyValue()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingRouteClass>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-class-route").Post("{id}",
            (ISender sender,
             int id,
             [FromHeader(Name = BindingConstants.TenantHeader)] string? tenant,
             BindingRouteClass command,
             CancellationToken ct) =>
            {
                command.Id = id;
                command.Tenant = tenant;
                return sender.Send<BindingRouteClass, BindingUnit>(command, ct);
            });

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(
            client,
            "/binding-class-route/11",
            """{"id":999,"force":true,"tenant":"body-tenant","name":"x"}""",
            (BindingConstants.TenantHeader, "real-tenant"));

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteClass>();
        Assert.Equal(11, received.Id);
        Assert.Equal("real-tenant", received.Tenant);
        Assert.False(received.Force);
        Assert.Equal("x", received.Name);
    }

    [Fact]
    public async Task Post_ClassWithSettableProperties_LeavesThemAtDefaultWhenTheEndpointDoesNotAssignThem()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingRouteClass>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-class-unassigned").Post("{id}",
            (ISender sender, BindingRouteClass command, CancellationToken ct) =>
                sender.Send<BindingRouteClass, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-class-unassigned/11",
            """{"id":999,"force":true,"tenant":"body-tenant","name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteClass>();
        Assert.Equal(0, received.Id);
        Assert.False(received.Force);
        Assert.Null(received.Tenant);
    }

    // ---------------------------------------------------------------------------------------------
    // which types the resolver is allowed to touch
    // ---------------------------------------------------------------------------------------------

    // IRequest (no response) is a mediator request too. Excluding only IRequest<T> would leave every
    // fire-and-forget command writable from the body - `Force` is the member that proves it, because the
    // endpoint assigns Id and so would hide a regression there.
    [Fact]
    public async Task Post_VoidRequest_AlsoKeepsItsRouteMemberOutOfTheBody()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        builder.Services.AddTransient<IRequestHandler<BindingVoidRequest>, BindingHost<object>.CapturingVoidHandler<BindingVoidRequest>>();
        await using var app = builder.Build();

        app.MapGroup("binding-void").Post("{id}",
            async (ISender sender, int id, BindingVoidRequest command, CancellationToken ct) =>
            {
                await sender.Send<BindingVoidRequest>(command with { Id = id }, ct);
                return Results.Ok();
            });

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-void/3", """{"id":999,"force":true,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingVoidRequest>();
        Assert.Equal(3, received.Id);
        Assert.False(received.Force);
        Assert.Equal("x", received.Name);
    }

    // The resolver must only touch IRequest/IRequest<T>. An ordinary DTO that happens to carry
    // [FromRoute] keeps ASP.NET Core's own semantics, where the attribute does nothing to body binding.
    [Fact]
    public async Task Post_PlainDtoThatIsNotAMediatorRequest_StillBindsItsFromRouteMemberFromTheBody()
    {
        var builder = CreateBindingBuilder(new BindingRecorder());
        await using var app = builder.Build();

        BindingPlainDto? captured = null;
        app.MapGroup("binding-plain-dto").Post("{id}", (BindingPlainDto dto) =>
        {
            captured = dto;
            return Results.Ok();
        });

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-plain-dto/5", """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        Assert.Equal(999, captured!.Id);
        Assert.Equal("x", captured.Name);
    }

    // The exclusion follows the type, not the position in the payload: the request's own member is
    // dropped while the identically-named member of a nested DTO - which is not a mediator request and
    // has no other way in - is still bound. Getting this wrong in either direction is silent data loss.
    [Fact]
    public async Task Post_RequestWithANestedDto_ExcludesOnlyTheRequestsOwnAnnotatedMember()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingNestedRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-nested").Post("{id}",
            (ISender sender, BindingNestedRequest command, CancellationToken ct) =>
                sender.Send<BindingNestedRequest, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-nested/5",
            """{"id":999,"child":{"id":7,"name":"c"}}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingNestedRequest>();
        Assert.Equal(0, received.Id);
        Assert.Equal(7, received.Child!.Id);
        Assert.Equal("c", received.Child.Name);
    }

    // Apps routinely declare their own marker interface (`IAuditedCommand : IRequest<T>`) and implement
    // that. The probe has to look through the whole interface graph, not just the directly declared one.
    [Fact]
    public async Task Post_RequestImplementingADerivedRequestInterface_IsStillTreatedAsAMediatorRequest()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingDerivedInterfaceRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-derived-interface").Post("{id}",
            (ISender sender, BindingDerivedInterfaceRequest command, CancellationToken ct) =>
                sender.Send<BindingDerivedInterfaceRequest, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-derived-interface/5",
            """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingDerivedInterfaceRequest>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    // A base command carrying the shared [FromRoute] member, overridden in the concrete command without
    // repeating the attribute: the attribute lookup has to walk to the base declaration (inherit: true),
    // or the whole hierarchy quietly goes back to reading its tenant/id from the body.
    [Fact]
    public async Task Post_RequestInheritingTheAnnotatedMemberFromItsBase_StillExcludesIt()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingOverridingRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-inherited").Post("{id}",
            (ISender sender, BindingOverridingRequest command, CancellationToken ct) =>
                sender.Send<BindingOverridingRequest, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-inherited/5", """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingOverridingRequest>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    // The source keys on the IFromRouteMetadata/IFromQueryMetadata/IFromHeaderMetadata interfaces, not on
    // Microsoft's concrete attribute classes, so an app's own binding-source attribute counts too. A check
    // narrowed to `is FromRouteAttribute` would still pass every other test in this file.
    [Fact]
    public async Task Post_MemberWithACustomBindingSourceAttribute_IsAlsoKeptOutOfTheBody()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingCustomSourceRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-custom-source").Post("{tenant}",
            (ISender sender, BindingCustomSourceRequest command, CancellationToken ct) =>
                sender.Send<BindingCustomSourceRequest, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-custom-source/real-tenant",
            """{"tenant":"body-tenant","name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingCustomSourceRequest>();
        Assert.Null(received.Tenant);
        Assert.Equal("x", received.Name);
    }

    // The contract is "not read from AND not written to the body": a request echoed back as the
    // response must not leak the members the client was told not to send.
    [Fact]
    public async Task Post_MediatorRequestEchoedAsTheResponse_OmitsTheExcludedMembers()
    {
        var builder = CreateBindingBuilder(new BindingRecorder());
        await using var app = builder.Build();

        app.MapGroup("binding-echo").Post("{id}",
            (int id, BindingRouteRecord command) => Results.Ok(command with { Id = id, Tenant = "acme" }));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-echo/5", """{"name":"x","amount":1.5}""");

        AssertOk(status, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("id", out _));
        Assert.False(root.TryGetProperty("force", out _));
        Assert.False(root.TryGetProperty("tenant", out _));
        Assert.Equal("x", root.GetProperty("name").GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // the exclusion is scoped to the Minimal API JSON options only
    // ---------------------------------------------------------------------------------------------

    // Logging, message queues and outbound HTTP calls all use their own JsonSerializerOptions. If the
    // exclusion leaked into those, a logged command would silently lose its id.
    [Fact]
    public void Serialize_WithPlainJsonSerializerOptions_StillIncludesTheExcludedMembers()
    {
        var plain = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(
            new BindingRouteRecord { Id = 5, Force = true, Tenant = "acme", Name = "x" },
            plain);

        Assert.Contains("\"id\":5", json, StringComparison.Ordinal);
        Assert.Contains("\"tenant\":\"acme\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_WithPlainJsonSerializerOptions_StillReadsTheExcludedMembers()
    {
        var plain = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var httpOptions = CreateHttpSerializerOptions();
        const string json = """{"id":5,"tenant":"acme","name":"x"}""";

        var withPlainOptions = JsonSerializer.Deserialize<BindingRouteRecord>(json, plain);
        var withHttpOptions = JsonSerializer.Deserialize<BindingRouteRecord>(json, httpOptions);

        Assert.Equal(5, withPlainOptions!.Id);
        Assert.Equal("acme", withPlainOptions.Tenant);
        // Same type, same JSON, different options: only the Minimal API options drop the members.
        Assert.Equal(0, withHttpOptions!.Id);
        Assert.Null(withHttpOptions.Tenant);
    }

    // ---------------------------------------------------------------------------------------------
    // the JSON contract itself, shape by shape
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Walks the Minimal API JSON contract for every request shape in this file and checks the four
    /// things the exclusion actually does - no getter, no setter, not required, and a converter that
    /// throws the body value away - plus, for the members that carry no binding-source attribute, that
    /// none of it happened to them. The HTTP tests above each cover one shape end to end; this is the
    /// one place where a shape nobody wrote an HTTP test for cannot slip through unfiltered, and where
    /// the negative half (a plain DTO, an ordinary member, a [FromBody] member) is stated outright.
    /// </summary>
    [Fact]
    public void HttpJsonOptions_EveryRequestShape_DropsExactlyItsNonBodyMembers()
    {
        var serializerOptions = CreateHttpSerializerOptions();

        var expectations = new (Type RequestType, string[] Excluded, string[] Kept)[]
        {
            (typeof(BindingRouteRecord), ["Id", "Force", "Search", "Tenant"], ["Name", "Amount"]),
            (typeof(BindingPositionalRecord), ["Id", "Force"], ["Name"]),
            (typeof(BindingRouteClass), ["Id", "Force", "Tenant"], ["Name"]),
            (typeof(BindingVoidRequest), ["Id", "Force"], ["Name"]),
            (typeof(BindingFormRequest), ["Id"], ["Name"]),
            (typeof(BindingRequiredMemberRequest), ["Id"], ["Name"]),
            (typeof(BindingNonNullableMemberRequest), ["Slug"], ["Name"]),
            (typeof(BindingNonNullablePositionalRequest), ["Slug"], ["Name"]),
            (typeof(BindingConstructorClass), ["Id"], ["Name"]),
            (typeof(BindingParameterAttributeClass), ["Id"], ["Name"]),
            (typeof(BindingCustomSourceRequest), ["Tenant"], ["Name"]),
            (typeof(BindingOverridingRequest), ["Id"], ["Name"]),
            (typeof(BindingDerivedInterfaceRequest), ["Id"], ["Name"]),
            (typeof(BindingNestedRequest), ["Id"], ["Child"]),
            // [FromBody] is a body source, so it is kept - and the route member next to it is not.
            (typeof(BindingAsParametersBodyRequest), ["Id"], ["Body"]),
            // Every member is bound outside the body, so its body contract is empty - which is exactly
            // why this shape has to be bound with [AsParameters] rather than from the body.
            (typeof(BindingAsParametersRequest), ["Id", "Filter", "Tenant"], []),
            // Not mediator requests: the resolver must not touch them even though they carry [FromRoute].
            (typeof(BindingPlainDto), [], ["Id", "Name"]),
            (typeof(BindingNestedDto), [], ["Id", "Name"]),
        };

        foreach (var (requestType, excluded, kept) in expectations)
        {
            var typeInfo = serializerOptions.GetTypeInfo(requestType);

            foreach (var memberName in excluded)
            {
                var property = MemberContract(typeInfo, memberName);
                Assert.Null(property.Get);
                Assert.Null(property.Set);
                Assert.False(property.IsRequired);
                Assert.NotNull(property.CustomConverter);
            }

            foreach (var memberName in kept)
            {
                var property = MemberContract(typeInfo, memberName);
                Assert.NotNull(property.Get);
                Assert.Null(property.CustomConverter);
            }
        }
    }

    // OpenAPI tooling, source generators and anything else in the resolver chain may add properties that
    // have no CLR member behind them. The wrapper iterates every property of a mediator request, so one
    // with no AttributeProvider must be stepped over rather than dereferenced - otherwise adding such a
    // resolver turns every request body in the app into a 500.
    [Fact]
    public void HttpJsonOptions_WhenAnotherResolverAddsAPropertyWithNoClrMember_LeavesItAloneAndStillExcludes()
    {
        var serializerOptions = CreateHttpSerializerOptions(static services =>
            services.ConfigureHttpJsonOptions(static options =>
            {
                var resolver = new DefaultJsonTypeInfoResolver();
                resolver.Modifiers.Add(static typeInfo =>
                {
                    if (typeInfo.Type != typeof(BindingRouteRecord))
                        return;

                    var synthetic = typeInfo.CreateJsonPropertyInfo(typeof(string), "synthetic");
                    synthetic.Get = static _ => "from-the-other-resolver";
                    typeInfo.Properties.Add(synthetic);
                });

                options.SerializerOptions.TypeInfoResolver = resolver;
            }));

        var json = JsonSerializer.Serialize(
            new BindingRouteRecord { Id = 5, Tenant = "acme", Name = "x" },
            serializerOptions);

        Assert.Contains("\"synthetic\":\"from-the-other-resolver\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tenant\"", json, StringComparison.Ordinal);
    }

    // An app is allowed to freeze the Minimal API serializer options in its own configure callback. The
    // setup runs afterwards and cannot wrap frozen options, and it chooses to step aside rather than
    // throw - so the app starts, and the members are bound from the body as if the feature were off.
    // This pins the degradation deliberately: if it ever becomes a loud startup failure instead, this
    // test is where that decision has to be made rather than discovered in production.
    [Fact]
    public void HttpJsonOptions_WhenTheAppFreezesTheSerializerOptions_DegradesInsteadOfThrowingAtStartup()
    {
        var serializerOptions = CreateHttpSerializerOptions(static services =>
            services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.MakeReadOnly()));

        Assert.True(serializerOptions.IsReadOnly);

        var request = JsonSerializer.Deserialize<BindingRouteRecord>("""{"id":5,"name":"x"}""", serializerOptions);

        Assert.Equal(5, request!.Id);
        Assert.Equal("x", request.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // what an excluded member does with a body value it cannot hold
    // ---------------------------------------------------------------------------------------------

    // The value is skipped, not parsed, so a client that sends garbage for a member it should not have
    // sent at all still gets its request handled instead of an unexplainable 400.
    [Theory]
    [InlineData("""{"id":"not-a-number","name":"x"}""")]
    [InlineData("""{"id":{"nested":1},"name":"x"}""")]
    [InlineData("""{"id":[1,2,3],"name":"x"}""")]
    [InlineData("""{"id":true,"name":"x"}""")]
    [InlineData("""{"id":null,"name":"x"}""")]
    public async Task Post_ExcludedMemberWithAJsonValueOfTheWrongShape_IsSkippedInsteadOfReturning400(string json)
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingRouteRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-wrong-shape").Post("{id}",
            (ISender sender, int id, BindingRouteRecord command, CancellationToken ct) =>
                sender.Send<BindingRouteRecord, BindingUnit>(command with { Id = id }, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-wrong-shape/5", json);

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteRecord>();
        Assert.Equal(5, received.Id);
        Assert.Equal("x", received.Name);
    }

    // The counterpart: ordinary members are still validated by System.Text.Json, so the exclusion has
    // not turned the whole request type into a "skip anything you cannot parse" contract.
    [Fact]
    public async Task Post_UnannotatedMemberWithAJsonValueOfTheWrongShape_StillReturnsBadRequest()
    {
        var builder = CreateBindingBuilder(new BindingRecorder());
        await using var app = builder.Build();

        app.MapGroup("binding-body-invalid").Post("{id}", (BindingRouteRecord command) => Results.Ok());

        using var client = await StartAsync(app);

        var (status, _) = await PostJsonAsync(client, "/binding-body-invalid/5", """{"amount":{"nested":1}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ---------------------------------------------------------------------------------------------
    // required / nullable interaction - an excluded member must never become a binding failure
    // ---------------------------------------------------------------------------------------------

    // `required` makes System.Text.Json reject a body that omits the member. Since the client is not
    // supposed to send it at all, leaving IsRequired alone would turn every correct call into a 400.
    [Fact]
    public async Task Post_RequiredAnnotatedMember_OmittedFromTheBody_DoesNotReturnBadRequest()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingRequiredMemberRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-required-omitted").Post("{id}",
            (ISender sender, BindingRequiredMemberRequest command, CancellationToken ct) =>
                sender.Send<BindingRequiredMemberRequest, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-required-omitted/5", """{"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingRequiredMemberRequest>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    [Fact]
    public async Task Post_RequiredAnnotatedMember_PresentInTheBody_IsStillDiscarded()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingRequiredMemberRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-required-present").Post("{id}",
            (ISender sender, BindingRequiredMemberRequest command, CancellationToken ct) =>
                sender.Send<BindingRequiredMemberRequest, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-required-present/5", """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        Assert.Equal(0, recorder.Last<BindingRequiredMemberRequest>().Id);
    }

#if NET9_0_OR_GREATER
    // An init-only property has its setter removed, so System.Text.Json skips the member itself and never
    // produces a value to null-check: the interesting part here is only that turning the option on does
    // not break the common shape. The constructor-parameter case below is the one the fix exists for.
    [Fact]
    public async Task Post_NonNullableAnnotatedMember_WithRespectNullableAnnotations_DoesNotReturnBadRequest()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder, static services =>
            services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.RespectNullableAnnotations = true));
        AddCapturingHandler<BindingNonNullableMemberRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-nonnullable").Post("{slug}",
            (ISender sender, BindingNonNullableMemberRequest command, CancellationToken ct) =>
                sender.Send<BindingNonNullableMemberRequest, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-nonnullable/real-slug",
            """{"slug":"from-body","name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingNonNullableMemberRequest>();
        Assert.Equal(string.Empty, received.Slug);
        Assert.Equal("x", received.Name);
    }

    // This is what IsSetNullable is for. A non-nullable constructor parameter DOES get a value read for
    // it, and the discarding converter hands back null; with RespectNullableAnnotations on, System.Text.Json
    // rejects that ("the constructor parameter 'Slug' doesn't allow null values") and the framework turns
    // the JsonException into a 400 - for a body that is doing nothing worse than repeating the route value.
    [Fact]
    public async Task Post_NonNullableConstructorParameter_WithRespectNullableAnnotations_DoesNotReturnBadRequest()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder, static services =>
            services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.RespectNullableAnnotations = true));
        AddCapturingHandler<BindingNonNullablePositionalRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-nonnullable-positional").Post("{slug}",
            (ISender sender, BindingNonNullablePositionalRequest command, CancellationToken ct) =>
                sender.Send<BindingNonNullablePositionalRequest, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-nonnullable-positional/real-slug",
            """{"slug":"from-body","name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingNonNullablePositionalRequest>();
        Assert.Null(received.Slug);
        Assert.Equal("x", received.Name);
    }

    // Microsoft.AspNetCore.OpenApi builds the request body schema from these same serializer options,
    // so "the route parameter and the body schema are disjoint" is what a client actually reads.
    [Fact]
    public void HttpJsonOptions_ExcludedMembers_AreAbsentFromTheRequestBodySchema()
    {
        var serializerOptions = CreateHttpSerializerOptions();

        var properties = JsonSchemaExporter.GetJsonSchemaAsNode(serializerOptions, typeof(BindingRouteRecord))["properties"]!
            .AsObject()
            .Select(static property => property.Key)
            .ToArray();

        Assert.Contains("name", properties);
        Assert.Contains("amount", properties);
        Assert.DoesNotContain("id", properties);
        Assert.DoesNotContain("force", properties);
        Assert.DoesNotContain("search", properties);
        Assert.DoesNotContain("tenant", properties);
    }

    [Fact]
    public void HttpJsonOptions_PositionalRecordSchema_LeavesOutTheConstructorParameterMembers()
    {
        var serializerOptions = CreateHttpSerializerOptions();

        var properties = JsonSchemaExporter.GetJsonSchemaAsNode(serializerOptions, typeof(BindingPositionalRecord))["properties"]!
            .AsObject()
            .Select(static property => property.Key)
            .ToArray();

        Assert.Contains("name", properties);
        Assert.DoesNotContain("id", properties);
        Assert.DoesNotContain("force", properties);
    }

    // A `required` member is normally listed under the schema's "required" keyword, which makes a
    // generated client send it - the one thing the caller must not do here.
    [Fact]
    public void HttpJsonOptions_RequiredExcludedMember_IsNotAdvertisedAsRequiredInTheSchema()
    {
        var serializerOptions = CreateHttpSerializerOptions();

        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(serializerOptions, typeof(BindingRequiredMemberRequest));

        var properties = schema["properties"]!.AsObject().Select(static property => property.Key).ToArray();
        Assert.Contains("name", properties);
        Assert.DoesNotContain("id", properties);

        var required = schema["required"]?.AsArray().Select(static node => node!.GetValue<string>()).ToArray()
            ?? Array.Empty<string>();
        Assert.DoesNotContain("id", required);
    }
#endif

    // ---------------------------------------------------------------------------------------------
    // [AsParameters] - the README's alternative, where ASP.NET Core binds every member itself
    // ---------------------------------------------------------------------------------------------

    // No body at all: route, query and header each populate their own member, so the endpoint needs no
    // manual assignment and cannot forget one. This is framework binding rather than library behavior -
    // it is here because the README offers it as the alternative, and a change that broke it would leave
    // the documentation recommending something that no longer works.
    [Fact]
    public async Task Get_AsParametersRequest_PopulatesRouteQueryAndHeaderMembersWithNoBody()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingAsParametersRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-asparams").Get("{id}",
            (ISender sender, [AsParameters] BindingAsParametersRequest request, CancellationToken ct) =>
                sender.Send<BindingAsParametersRequest, BindingUnit>(request, ct));

        using var client = await StartAsync(app);

        var (status, body) = await GetAsync(client, "/binding-asparams/42?filter=abc",
            (BindingConstants.TenantHeader, "acme"));

        AssertOk(status, body);
        var received = recorder.Last<BindingAsParametersRequest>();
        Assert.Equal(42, received.Id);
        Assert.Equal("abc", received.Filter);
        Assert.Equal("acme", received.Tenant);
    }

    // The optional members stay null rather than failing the request, which is what makes a partially
    // filled [AsParameters] request usable for filters and tenancy headers.
    [Fact]
    public async Task Get_AsParametersRequest_LeavesOptionalMembersNullWhenTheQueryAndHeaderAreAbsent()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingAsParametersRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-asparams-optional").Get("{id}",
            (ISender sender, [AsParameters] BindingAsParametersRequest request, CancellationToken ct) =>
                sender.Send<BindingAsParametersRequest, BindingUnit>(request, ct));

        using var client = await StartAsync(app);

        var (status, body) = await GetAsync(client, "/binding-asparams-optional/42");

        AssertOk(status, body);
        var received = recorder.Last<BindingAsParametersRequest>();
        Assert.Equal(42, received.Id);
        Assert.Null(received.Filter);
        Assert.Null(received.Tenant);
    }

    // The README's second pattern: [FromRoute] on one member, [FromBody] on another. The route value
    // and the JSON body land in different members, so neither can overwrite the other - and the JSON
    // contract the library builds for the same type must not have removed the [FromBody] member too.
    [Fact]
    public async Task Post_AsParametersRequestWithFromBody_BindsTheRouteAndTheBodySeparately()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingAsParametersBodyRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-asparams-body").Post("{id}",
            (ISender sender, [AsParameters] BindingAsParametersBodyRequest request, CancellationToken ct) =>
                sender.Send<BindingAsParametersBodyRequest, BindingUnit>(request, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-asparams-body/9", """{"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingAsParametersBodyRequest>();
        Assert.Equal(9, received.Id);
        Assert.Equal("x", received.Body.Name);
    }

    // A route parameter a request refers to must actually exist. Mapping "{studentId}" for a request
    // whose member is [FromRoute] Id is a typo that should surface while routes are built, not as an
    // id that is quietly always 0 in production.
    [Fact]
    public async Task Map_AsParametersRequest_WhenTheRoutePatternNamesADifferentParameter_FailsWhileBuildingEndpoints()
    {
        var builder = CreateBindingBuilder(new BindingRecorder());
        AddCapturingHandler<BindingAsParametersRequest>(builder.Services);
        await using var app = builder.Build();

        // Both the mapping call and the endpoint materialisation sit inside the lambda: the framework
        // is free to validate at either point, and the contract is that it does not stay silent.
        var exception = Record.Exception(() =>
        {
            app.MapGroup("binding-asparams-mismatch").Get("{studentId}",
                (ISender sender, [AsParameters] BindingAsParametersRequest request, CancellationToken ct) =>
                    sender.Send<BindingAsParametersRequest, BindingUnit>(request, ct));

            _ = app.RouteEndpoints();
        });

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("route parameter", exception!.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // the documented form-binding limitation
    // ---------------------------------------------------------------------------------------------

    // Documented limitation, not a bug: only the JSON body is filtered, so a form-bound request still
    // reads its [FromRoute] member from a form field and a caller CAN post Id=999 to /5. The README
    // tells you to assign the route value after binding in form endpoints; this locks the behaviour in
    // so the docs and the code cannot drift apart silently.
    [Fact]
    public async Task Post_FormBoundRequest_StillReadsItsFromRouteMemberFromTheFormField()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder);
        AddCapturingHandler<BindingFormRequest>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-form").Post("{id}",
            (ISender sender, [FromForm] BindingFormRequest command, CancellationToken ct) =>
                sender.Send<BindingFormRequest, BindingUnit>(command, ct))
            .DisableAntiforgery();

        using var client = await StartAsync(app);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Id", "999"),
            new KeyValuePair<string, string>("Name", "from-form"),
        });

        var (status, body) = await SendAsync(client, HttpMethod.Post, "/binding-form/5", form, []);

        AssertOk(status, body);
        var received = recorder.Last<BindingFormRequest>();
        Assert.Equal(999, received.Id);
        Assert.Equal("from-form", received.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // the resolver has to survive whatever the app does to the JSON type-info resolver
    // ---------------------------------------------------------------------------------------------

    // AddProblemDetails() inserts into TypeInfoResolverChain, which makes TypeInfoResolver return the
    // options' own live chain. Wrapping that chain in place makes the wrapper call itself until the
    // stack overflows, so this round trip working at all is the regression being guarded - and the
    // endpoint deliberately assigns nothing, so the exclusion is still being proved alongside it.
    [Fact]
    public async Task Post_WhenTheAppCallsAddProblemDetails_StillExcludesTheRouteMemberAndServesTheRequest()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder, static services => services.AddProblemDetails());
        AddCapturingHandler<BindingRouteRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-problem-details").Post("{id}",
            (ISender sender, BindingRouteRecord command, CancellationToken ct) =>
                sender.Send<BindingRouteRecord, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-problem-details/5", """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteRecord>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    [Fact]
    public async Task Post_WhenTheAppReplacesTheTypeInfoResolver_StillExcludesTheRouteMember()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder, static services =>
            services.ConfigureHttpJsonOptions(static options =>
                options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver()));
        AddCapturingHandler<BindingRouteRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-replaced-resolver").Post("{id}",
            (ISender sender, BindingRouteRecord command, CancellationToken ct) =>
                sender.Send<BindingRouteRecord, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-replaced-resolver/5", """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteRecord>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    [Fact]
    public async Task Post_WhenTheAppInsertsIntoTheTypeInfoResolverChain_StillExcludesTheRouteMember()
    {
        var recorder = new BindingRecorder();
        var builder = CreateBindingBuilder(recorder, static services =>
            services.ConfigureHttpJsonOptions(static options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, new DefaultJsonTypeInfoResolver())));
        AddCapturingHandler<BindingRouteRecord>(builder.Services);
        await using var app = builder.Build();

        app.MapGroup("binding-chained-resolver").Post("{id}",
            (ISender sender, BindingRouteRecord command, CancellationToken ct) =>
                sender.Send<BindingRouteRecord, BindingUnit>(command, ct));

        using var client = await StartAsync(app);

        var (status, body) = await PostJsonAsync(client, "/binding-chained-resolver/5", """{"id":999,"name":"x"}""");

        AssertOk(status, body);
        var received = recorder.Last<BindingRouteRecord>();
        Assert.Equal(0, received.Id);
        Assert.Equal("x", received.Name);
    }

    // ---------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------

    private static WebApplicationBuilder CreateBindingBuilder(BindingRecorder recorder, Action<IServiceCollection>? configureServices = null)
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMediator();
        builder.Services.AddSingleton(recorder);
        configureServices?.Invoke(builder.Services);
        return builder;
    }

    /// <summary>
    /// Registers the capturing handler for one request type. The handler lives inside an open generic
    /// host type so the assembly scans other test files run skip it: it has a constructor dependency,
    /// which a namespace-scope handler is not allowed to have here.
    /// </summary>
    private static void AddCapturingHandler<TRequest>(IServiceCollection services)
        where TRequest : IRequest<BindingUnit>
    {
        services.AddTransient<IRequestHandler<TRequest, BindingUnit>, BindingHost<object>.CapturingHandler<TRequest>>();
    }

    private static async Task<HttpClient> StartAsync(WebApplication app)
    {
        await app.StartAsync();
        return app.GetTestClient();
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string url, params (string Name, string Value)[] headers)
        => SendAsync(client, HttpMethod.Get, url, null, headers);

    private static Task<(HttpStatusCode Status, string Body)> PostJsonAsync(HttpClient client, string url, string json, params (string Name, string Value)[] headers)
        => SendAsync(client, HttpMethod.Post, url, JsonBody(json), headers);

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        HttpContent? content,
        (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, url);
        if (content is not null)
            request.Content = content;

        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Fails with the response body attached, so a 400 says why instead of just "expected OK".</summary>
    private static void AssertOk(HttpStatusCode status, string body)
    {
        Assert.True(status == HttpStatusCode.OK, $"Expected 200 OK but got {(int)status}. Body: {body}");
    }

    private static JsonSerializerOptions CreateHttpSerializerOptions(Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddMediator();
        configureServices?.Invoke(services);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
    }

    /// <summary>
    /// The JSON contract for one CLR member. Matching case-insensitively is what lets the expectations
    /// be written with the C# member names while the contract carries the camelCase wire names.
    /// </summary>
    private static JsonPropertyInfo MemberContract(JsonTypeInfo typeInfo, string memberName)
    {
        return typeInfo.Properties.Single(property =>
            string.Equals(property.Name, memberName, StringComparison.OrdinalIgnoreCase));
    }
}

// =================================================================================================
// Types used only by this file. Every one is prefixed "Binding", and the only handlers live inside
// the open generic BindingHost<TMarker> so the assembly-wide scans other test files run skip them.
// =================================================================================================

public static class BindingConstants
{
    public const string TenantHeader = "X-Binding-Tenant";
}

/// <summary>Response type for the capturing handlers - deliberately not a mediator request itself.</summary>
public sealed record BindingUnit(string Status);

/// <summary>Records the request instance the handler was actually given.</summary>
public sealed class BindingRecorder
{
    private readonly object gate = new();
    private object? received;

    public void Capture(object request)
    {
        lock (gate)
            received = request;
    }

    public T Last<T>()
    {
        lock (gate)
        {
            if (received is T typed)
                return typed;

            throw new InvalidOperationException(
                $"Expected the handler to have received a {typeof(T).Name}, but it received {received?.GetType().Name ?? "nothing"}.");
        }
    }
}

/// <summary>
/// Handlers with constructor dependencies have to be nested in an open generic type: the assembly scan
/// skips anything with generic parameters, and a namespace-scope handler taking a BindingRecorder would
/// break every other test that registers this assembly and validates the container on build.
/// </summary>
public static class BindingHost<TMarker>
{
    public sealed class CapturingHandler<TRequest>(BindingRecorder recorder) : IRequestHandler<TRequest, BindingUnit>
        where TRequest : IRequest<BindingUnit>
    {
        public Task<BindingUnit> Handle(TRequest request, CancellationToken cancellationToken)
        {
            recorder.Capture(request!);
            return Task.FromResult(new BindingUnit("ok"));
        }
    }

    public sealed class CapturingVoidHandler<TRequest>(BindingRecorder recorder) : IRequestHandler<TRequest>
        where TRequest : IRequest
    {
        public Task Handle(TRequest request, CancellationToken cancellationToken)
        {
            recorder.Capture(request!);
            return Task.CompletedTask;
        }
    }
}

/// <summary>A record with init-only members: updated in the endpoint with <c>command with { Id = id }</c>.</summary>
public sealed record BindingRouteRecord : IRequest<BindingUnit>
{
    [FromRoute]
    public int Id { get; init; }

    [FromQuery]
    public bool Force { get; init; }

    [FromQuery(Name = "q")]
    public string? Search { get; init; }

    [FromHeader(Name = BindingConstants.TenantHeader)]
    public string? Tenant { get; init; }

    public string? Name { get; init; }

    public decimal Amount { get; init; }
}

/// <summary>A positional record: the attributes land on the constructor parameters, not the properties.</summary>
public sealed record BindingPositionalRecord([FromRoute] int Id, [FromQuery] bool Force, string? Name) : IRequest<BindingUnit>;

/// <summary>A class with settable properties: updated in the endpoint with <c>command.Id = id;</c>.</summary>
public sealed class BindingRouteClass : IRequest<BindingUnit>
{
    [FromRoute]
    public int Id { get; set; }

    [FromQuery]
    public bool Force { get; set; }

    [FromHeader(Name = BindingConstants.TenantHeader)]
    public string? Tenant { get; set; }

    public string? Name { get; set; }
}

/// <summary>A request with no response - IRequest, not IRequest&lt;T&gt;.</summary>
public sealed record BindingVoidRequest : IRequest
{
    [FromRoute]
    public int Id { get; init; }

    [FromQuery]
    public bool Force { get; init; }

    public string? Name { get; init; }
}

/// <summary>Deliberately NOT a mediator request: the resolver must leave it alone.</summary>
public sealed class BindingPlainDto
{
    [FromRoute]
    public int Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>A mediator request whose body carries a nested DTO with an identically annotated member.</summary>
public sealed record BindingNestedRequest : IRequest<BindingUnit>
{
    [FromRoute]
    public int Id { get; init; }

    public BindingNestedDto? Child { get; init; }
}

/// <summary>The nested DTO: not a mediator request, so its own <c>[FromRoute]</c> member stays in the body.</summary>
public sealed class BindingNestedDto
{
    [FromRoute]
    public int Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>An app-defined marker interface over <see cref="IRequest{TResponse}"/>.</summary>
public interface IBindingDomainRequest : IRequest<BindingUnit>
{
}

/// <summary>Reaches <c>IRequest&lt;T&gt;</c> only through <see cref="IBindingDomainRequest"/>.</summary>
public sealed record BindingDerivedInterfaceRequest : IBindingDomainRequest
{
    [FromRoute]
    public int Id { get; init; }

    public string? Name { get; init; }
}

/// <summary>A base command that owns the annotated member for a whole family of requests.</summary>
public abstract class BindingInheritedBase : IRequest<BindingUnit>
{
    [FromRoute]
    public virtual int Id { get; set; }
}

/// <summary>Overrides the member without repeating the attribute.</summary>
public sealed class BindingOverridingRequest : BindingInheritedBase
{
    public override int Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>An app's own binding-source attribute: a route source that is not Microsoft's type.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BindingCustomRouteAttribute : Attribute, IFromRouteMetadata
{
    /// <summary>Null means "the member's own name", which is all this test needs from the metadata.</summary>
    public string? Name => null;
}

/// <summary>Carries the custom binding-source attribute instead of <c>[FromRoute]</c>.</summary>
public sealed record BindingCustomSourceRequest : IRequest<BindingUnit>
{
    [BindingCustomRoute]
    public string? Tenant { get; init; }

    public string? Name { get; init; }
}

/// <summary>A class bound through a hand-written constructor, with the attribute on the property.</summary>
public sealed class BindingConstructorClass : IRequest<BindingUnit>
{
    public BindingConstructorClass(int id, string? name)
    {
        Id = id;
        Name = name;
    }

    [FromRoute]
    public int Id { get; }

    public string? Name { get; }
}

/// <summary>The same shape with the attribute on the camelCased constructor parameter instead.</summary>
public sealed class BindingParameterAttributeClass : IRequest<BindingUnit>
{
    public BindingParameterAttributeClass([FromRoute] int id, string? name)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }

    public string? Name { get; }
}

/// <summary>Bound with <c>[AsParameters]</c>: ASP.NET Core fills every member from the request itself.</summary>
public sealed class BindingAsParametersRequest : IRequest<BindingUnit>
{
    [FromRoute]
    public int Id { get; set; }

    [FromQuery]
    public string? Filter { get; set; }

    [FromHeader(Name = BindingConstants.TenantHeader)]
    public string? Tenant { get; set; }
}

/// <summary>The README's alternative shape: a route member plus an explicit <c>[FromBody]</c> member.</summary>
public sealed record BindingAsParametersBodyRequest([FromRoute] int Id, [FromBody] BindingBodyDto Body) : IRequest<BindingUnit>;

public sealed class BindingBodyDto
{
    public string? Name { get; set; }
}

/// <summary>Bound from a form, where the README says the route member is still read from a form field.</summary>
public sealed class BindingFormRequest : IRequest<BindingUnit>
{
    [FromRoute]
    public int Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>A <c>required</c> excluded member: its absence from the body must not be a binding failure.</summary>
public sealed record BindingRequiredMemberRequest : IRequest<BindingUnit>
{
    [FromRoute]
    public required int Id { get; init; }

    public string? Name { get; init; }
}

/// <summary>A non-nullable excluded property: discarding its value must not trip nullable-annotation checks.</summary>
public sealed record BindingNonNullableMemberRequest : IRequest<BindingUnit>
{
    [FromRoute]
    public string Slug { get; init; } = string.Empty;

    public string? Name { get; init; }
}

/// <summary>
/// The same member as a non-nullable <b>constructor parameter</b>, which is the shape that actually has a
/// value read for it and so is the one nullable annotations can reject.
/// </summary>
public sealed record BindingNonNullablePositionalRequest([FromRoute] string Slug, string? Name) : IRequest<BindingUnit>;
