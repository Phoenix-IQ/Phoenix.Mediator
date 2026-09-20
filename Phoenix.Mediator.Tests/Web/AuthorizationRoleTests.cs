using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Tests.Infrastructure;
using Phoenix.Mediator.Web;
using System.Net;
using System.Runtime.Serialization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Xunit;

namespace Phoenix.Mediator.Tests;

/// <summary>
/// <c>AuthorizationExtensions.RequireRole&lt;TBuilder, TRole&gt;</c> and the role-claim mapping behind it.
/// Every assertion here is about what an endpoint ends up carrying as <see cref="IAuthorizeData"/>, or
/// about the policy the authorization middleware builds from it — those are what decide 401/403.
/// </summary>
public sealed class RoleAuthorizationTests
{
    // ---------------------------------------------------------------------
    // Argument guards
    // ---------------------------------------------------------------------

    // A null builder reaches this method when a call site chains off something that returned null.
    // Returning quietly would leave the endpoint mapped and completely unprotected.
    [Fact]
    public void RequireRole_ThrowsArgumentNullExceptionWhenTheBuilderIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            AuthorizationExtensions.RequireRole<RouteHandlerBuilder, RoleBasic>(null!, RoleBasic.Admin));

        Assert.Equal("builder", exception.ParamName);
    }

    // The empty-roles fast path returns before it ever touches the roles array, so it needs its own
    // guard test: a regression there would hand back a null builder instead of throwing.
    [Fact]
    public void RequireRole_ThrowsArgumentNullExceptionWhenTheBuilderIsNullAndNoRolesAreGiven()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            AuthorizationExtensions.RequireRole<RouteHandlerBuilder, RoleBasic>(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    // params arrays are only non-null by convention: an explicitly null argument, or a variable that
    // turned out to be null, must fail loudly rather than NullReferenceException inside string.Join.
    [Fact]
    public async Task RequireRole_ThrowsArgumentNullExceptionWhenTheRolesArrayIsNull()
    {
        await using var app = CreateApp();
        var endpoint = app.MapGroup("role-tests").Get("null-roles", () => "ok");

        var exception = Assert.Throws<ArgumentNullException>(() =>
            endpoint.RequireRole<RouteHandlerBuilder, RoleBasic>(null!));

        Assert.Equal("roles", exception.ParamName);
    }

    // With both arguments null the message must name the builder — the outer mistake — so the
    // developer is not sent looking at the role list.
    [Fact]
    public void RequireRole_ValidatesTheBuilderBeforeTheRoles()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            AuthorizationExtensions.RequireRole<RouteHandlerBuilder, RoleBasic>(null!, null!));

        Assert.Equal("builder", exception.ParamName);
    }

    // ---------------------------------------------------------------------
    // No roles is RequireAuthorization()
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RequireRole_WithNoRoles_AddsAuthorizationMetadataWithoutRoles()
    {
        await using var app = CreateApp();
        AuthorizationExtensions.RequireRole<RouteHandlerBuilder, RoleBasic>(
            app.MapGroup("role-tests").Get("any-user", () => "ok"));

        // Exactly one entry. NotEmpty would also pass if the call added a role requirement on top of
        // RequireAuthorization(), which is precisely the regression this test exists to catch.
        var authorizeData = Assert.Single(app.Endpoint("role-tests/any-user").Metadata.GetOrderedMetadata<IAuthorizeData>());

        // A non-null Roles here — even an empty string — would mean the empty fast path was skipped.
        Assert.Null(authorizeData.Roles);
        Assert.Null(authorizeData.Policy);
        Assert.Null(authorizeData.AuthenticationSchemes);
    }

    // RequireRole must not smuggle in a policy name or an authentication scheme alongside the roles: a
    // policy name no provider knows throws on the first request, and a hard-coded scheme would pin the
    // endpoint to one authentication handler regardless of how the app is configured.
    [Fact]
    public async Task RequireRole_WithRoles_SetsOnlyTheRolesOnTheAuthorizeData()
    {
        await using var app = CreateApp();
        app.MapGroup("role-tests").Get("roles-only", () => "ok").RequireRole(RoleBasic.Admin, RoleBasic.Manager);

        var authorizeData = Assert.Single(app.Endpoint("role-tests/roles-only").Metadata.GetOrderedMetadata<IAuthorizeData>());

        Assert.Equal("Admin,Manager", authorizeData.Roles);
        Assert.Null(authorizeData.Policy);
        Assert.Null(authorizeData.AuthenticationSchemes);
    }

    // The documented equivalence: an empty role list must produce the same metadata as calling
    // RequireAuthorization() directly, not a stricter or a weaker variant of it.
    [Fact]
    public async Task RequireRole_WithAnEmptyRolesArray_MatchesRequireAuthorization()
    {
        await using var app = CreateApp();
        var group = app.MapGroup("role-tests");
        group.Get("empty-array", () => "ok").RequireRole(Array.Empty<RoleBasic>());
        group.Get("require-authorization", () => "ok").RequireAuthorization();

        var viaRequireRole = app.Endpoint("role-tests/empty-array").Metadata.GetOrderedMetadata<IAuthorizeData>();
        var viaRequireAuthorization = app.Endpoint("role-tests/require-authorization").Metadata.GetOrderedMetadata<IAuthorizeData>();

        // Anchors the comparison: two empty lists are "equal" too, and that would be a RequireRole that
        // protected nothing at all.
        Assert.NotEmpty(viaRequireAuthorization);
        Assert.Equal(viaRequireAuthorization.Count, viaRequireRole.Count);
        Assert.Equal(
            viaRequireAuthorization.Select(data => (data.Roles, data.Policy, data.AuthenticationSchemes)),
            viaRequireRole.Select(data => (data.Roles, data.Policy, data.AuthenticationSchemes)));
    }

    // "Same as RequireAuthorization()" only means anything if the combined policy still denies
    // anonymous callers; losing that requirement would open the endpoint to everyone.
    [Fact]
    public async Task RequireRole_WithNoRoles_ProducesTheDefaultDenyAnonymousPolicy()
    {
        await using var app = CreateApp();
        AuthorizationExtensions.RequireRole<RouteHandlerBuilder, RoleBasic>(
            app.MapGroup("role-tests").Get("default-policy", () => "ok"));

        var policy = await PolicyFor(app, "role-tests/default-policy");

        Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
        Assert.DoesNotContain(policy.Requirements, requirement => requirement is RolesAuthorizationRequirement);
    }

    // ---------------------------------------------------------------------
    // Member name to role-claim value
    // ---------------------------------------------------------------------

    // The member name is the claim value, verbatim and case-sensitive: "admin" would not match a
    // token carrying "Admin".
    [Theory]
    [InlineData(RoleBasic.Admin, "Admin")]
    [InlineData(RoleBasic.Manager, "Manager")]
    [InlineData(RoleBasic.User, "User")]
    public async Task RequireRole_UsesTheMemberNameAsTheRoleClaimValue(RoleBasic role, string expected)
        => Assert.Equal(expected, await RoleClaimValuesFor(role));

    public static TheoryData<RoleBasic[], string> RoleJoinCases => new()
    {
        { new[] { RoleBasic.Admin }, "Admin" },
        { new[] { RoleBasic.Admin, RoleBasic.Manager }, "Admin,Manager" },
        { new[] { RoleBasic.Manager, RoleBasic.Admin }, "Manager,Admin" },
        { new[] { RoleBasic.Admin, RoleBasic.Manager, RoleBasic.User }, "Admin,Manager,User" },
        // A repeated role is kept rather than de-duplicated, and must not swallow its neighbours.
        { new[] { RoleBasic.Admin, RoleBasic.Admin }, "Admin,Admin" },
        { new[] { RoleBasic.User, RoleBasic.Admin, RoleBasic.User }, "User,Admin,User" },
    };

    // ASP.NET Core reads Roles as one comma-separated string, so the join — and its order — is the
    // whole contract of a multi-role call.
    [Theory]
    [MemberData(nameof(RoleJoinCases))]
    public async Task RequireRole_JoinsTheRolesWithCommasInTheOrderGiven(RoleBasic[] roles, string expected)
        => Assert.Equal(expected, await RoleClaimValuesFor(roles));

    // [EnumMember] is the escape hatch for claim values that are not valid C# identifiers. A blank
    // Value counts as "not set" — otherwise an empty attribute would require a role named "".
    [Theory]
    [InlineData(RoleAnnotated.SuperAdmin, "super-admin")]
    [InlineData(RoleAnnotated.SpacedValue, "site admin")]
    [InlineData(RoleAnnotated.PaddedValue, " padded ")]
    [InlineData(RoleAnnotated.Plain, "Plain")]
    [InlineData(RoleAnnotated.BlankValue, "BlankValue")]
    [InlineData(RoleAnnotated.WhitespaceValue, "WhitespaceValue")]
    public async Task RequireRole_PrefersTheEnumMemberValueAndTreatsABlankOneAsAbsent(RoleAnnotated role, string expected)
        => Assert.Equal(expected, await RoleClaimValuesFor(role));

    // Annotated and plain members are routinely mixed in one call; the mapping is per member, not
    // per enum.
    [Fact]
    public async Task RequireRole_MapsEachMemberIndependentlyWhenOnlySomeAreAnnotated()
        => Assert.Equal("super-admin,Plain", await RoleClaimValuesFor(RoleAnnotated.SuperAdmin, RoleAnnotated.Plain));

    // The blank-value fallback is a security control, not tidiness. ASP.NET Core splits Roles on commas
    // and throws away the blank entries, so if a member annotated [EnumMember(Value = "   ")] were taken
    // at face value the endpoint would end up with no role requirement at all — open to every
    // authenticated caller. Asserting the requirement, not just the metadata string, is what catches it:
    // narrowing the check to IsNullOrEmpty would still produce a plausible-looking Roles of "   ".
    [Theory]
    [InlineData(RoleAnnotated.BlankValue, "BlankValue")]
    [InlineData(RoleAnnotated.WhitespaceValue, "WhitespaceValue")]
    public async Task RequireRole_WithABlankEnumMemberValue_StillProducesARoleRequirement(RoleAnnotated role, string expected)
    {
        await using var app = CreateApp();
        app.MapGroup("role-tests").Get("blank-value", () => "ok").RequireRole(role);

        var policy = await PolicyFor(app, "role-tests/blank-value");
        var requirement = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());

        Assert.Equal(new[] { expected }, requirement.AllowedRoles.ToArray());
    }

    // A comma inside one claim value would be read back as two roles, quietly widening the endpoint
    // to a role nobody configured.
    [Fact]
    public async Task RequireRole_ThrowsWhenARoleClaimValueContainsAComma()
    {
        var exception = Assert.IsType<ArgumentException>(await RoleFailureFor(RoleAnnotated.CommaValue));

        Assert.Equal("role", exception.ParamName);
        Assert.Contains("comma", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("admin,manager", exception.Message);
    }

    // The comma check must survive being one of several roles, where the joined string would look
    // superficially valid. The message has to name the offending value too — "one of your roles is
    // bad" sends the developer reading every member of the enum.
    [Fact]
    public async Task RequireRole_ThrowsWhenACommaRoleIsCombinedWithValidRoles()
    {
        var exception = Assert.IsType<ArgumentException>(
            await RoleFailureFor(RoleAnnotated.Plain, RoleAnnotated.CommaValue, RoleAnnotated.SuperAdmin));

        Assert.Equal("role", exception.ParamName);
        Assert.Contains("admin,manager", exception.Message, StringComparison.Ordinal);
        Assert.Contains("comma", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------
    // Undefined values
    // ---------------------------------------------------------------------

    // An undefined value stringifies to its number, so without this guard the endpoint would require
    // a role literally named "42" and reject everybody at runtime instead of failing at startup.
    [Theory]
    [InlineData((RoleBasic)3)]
    [InlineData((RoleBasic)42)]
    [InlineData((RoleBasic)(-1))]
    [InlineData((RoleBasic)int.MaxValue)]
    public async Task RequireRole_ThrowsForUndefinedEnumValues(RoleBasic role)
    {
        var exception = Assert.IsType<ArgumentException>(await RoleFailureFor(role));

        Assert.Equal("role", exception.ParamName);
        Assert.Contains("is not a defined", exception.Message);
        // The enum type has to be named, or the startup failure is impossible to place.
        Assert.Contains(nameof(RoleBasic), exception.Message);
    }

    // default(TRole) is what an unassigned field or a missing configuration value produces. When the
    // enum has no zero member that is not a role at all, and must not be accepted.
    [Fact]
    public async Task RequireRole_ThrowsForTheZeroValueOfAnEnumWithoutAZeroMember()
    {
        var exception = Assert.IsType<ArgumentException>(await RoleFailureFor(default(RoleWithoutZero)));

        Assert.Equal("role", exception.ParamName);
        Assert.Contains(nameof(RoleWithoutZero), exception.Message);
    }

    // The mirror image: zero is only suspicious when it is undefined. RoleBasic.Admin and
    // RoleNegativeNumbers.Guest are both zero and both declared, so both are ordinary roles.
    [Fact]
    public async Task RequireRole_AcceptsAZeroValueThatIsADefinedMember()
    {
        Assert.Equal("Admin", await RoleClaimValuesFor(RoleBasic.Admin));
        Assert.Equal("Guest", await RoleClaimValuesFor(RoleNegativeNumbers.Guest));
    }

    // Validation is eager — it happens during the RequireRole call, not when the endpoint is built —
    // and a rejected call must add nothing at all, so catching the exception cannot leave a
    // half-configured endpoint behind.
    [Fact]
    public async Task RequireRole_AddsNoAuthorizationMetadataWhenOneOfTheRolesIsRejected()
    {
        await using var app = CreateApp();
        var endpoint = app.MapGroup("role-tests").Get("rejected", () => "ok");

        Assert.Throws<ArgumentException>(() => endpoint.RequireRole(RoleBasic.Admin, (RoleBasic)42));

        Assert.Empty(app.Endpoint("role-tests/rejected").Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    // ---------------------------------------------------------------------
    // [Flags] enums
    // ---------------------------------------------------------------------

    // Admin|Manager is not a member: its ToString() is "Admin, Manager", which ASP.NET Core would
    // read as "either role" — the opposite of the "both" the author of the bitwise or meant.
    [Theory]
    [InlineData(RoleFlagsKind.Admin | RoleFlagsKind.Manager)]
    [InlineData(RoleFlagsKind.Admin | RoleFlagsKind.Auditor)]
    [InlineData(RoleFlagsKind.Admin | RoleFlagsKind.Manager | RoleFlagsKind.Auditor)]
    [InlineData((RoleFlagsKind)8)]
    public async Task RequireRole_ThrowsForFlagsCombinations(RoleFlagsKind role)
    {
        var exception = Assert.IsType<ArgumentException>(await RoleFailureFor(role));

        Assert.Equal("role", exception.ParamName);
        Assert.Contains("is not a defined", exception.Message);
    }

    // [Flags] itself is not the problem — a single declared member of a flags enum is a perfectly
    // ordinary role, including the zero member.
    [Theory]
    [InlineData(RoleFlagsKind.None, "None")]
    [InlineData(RoleFlagsKind.Admin, "Admin")]
    [InlineData(RoleFlagsKind.Manager, "Manager")]
    [InlineData(RoleFlagsKind.Auditor, "Auditor")]
    public async Task RequireRole_AcceptsASingleDefinedMemberOfAFlagsEnum(RoleFlagsKind role, string expected)
        => Assert.Equal(expected, await RoleClaimValuesFor(role));

    // The supported way to say "either of these" with a flags enum: separate arguments, not an or.
    [Fact]
    public async Task RequireRole_JoinsSeveralFlagsMembersPassedAsSeparateArguments()
        => Assert.Equal("Admin,Manager", await RoleClaimValuesFor(RoleFlagsKind.Admin, RoleFlagsKind.Manager));

    // ---------------------------------------------------------------------
    // Unusual enum shapes
    // ---------------------------------------------------------------------

    // Role enums are often given explicit numbers so they can be persisted; the numbers must not
    // leak into the claim value.
    [Theory]
    [InlineData(RoleExplicitNumbers.Admin, "Admin")]
    [InlineData(RoleExplicitNumbers.Manager, "Manager")]
    [InlineData(RoleExplicitNumbers.User, "User")]
    public async Task RequireRole_SupportsEnumsWithExplicitNumericValues(RoleExplicitNumbers role, string expected)
        => Assert.Equal(expected, await RoleClaimValuesFor(role));

    // The gaps between explicit numbers are undefined values like any other — including zero, which
    // an enum numbered from 10 never declares.
    [Theory]
    [InlineData((RoleExplicitNumbers)0)]
    [InlineData((RoleExplicitNumbers)11)]
    [InlineData((RoleExplicitNumbers)25)]
    public Task RequireRole_ThrowsForValuesInTheGapsBetweenExplicitNumbers(RoleExplicitNumbers role)
        => AssertUndefinedRoleRejected(role);

    [Theory]
    [InlineData(RoleNegativeNumbers.Banned, "Banned")]
    [InlineData(RoleNegativeNumbers.Guest, "Guest")]
    [InlineData(RoleNegativeNumbers.Member, "Member")]
    public async Task RequireRole_SupportsNegativeEnumValues(RoleNegativeNumbers role, string expected)
        => Assert.Equal(expected, await RoleClaimValuesFor(role));

    [Theory]
    [InlineData((RoleNegativeNumbers)(-6))]
    [InlineData((RoleNegativeNumbers)(-1))]
    public Task RequireRole_ThrowsForUndefinedNegativeEnumValues(RoleNegativeNumbers role)
        => AssertUndefinedRoleRejected(role);

    // A byte-backed enum goes through the same generic Enum.IsDefined and GetField path as an
    // int-backed one; an underlying-type assumption in there would break it.
    [Theory]
    [InlineData(RoleByteKind.Admin, "Admin")]
    [InlineData(RoleByteKind.Manager, "Manager")]
    public async Task RequireRole_SupportsByteBackedEnums(RoleByteKind role, string expected)
        => Assert.Equal(expected, await RoleClaimValuesFor(role));

    [Fact]
    public Task RequireRole_ThrowsForUndefinedByteBackedEnumValues()
        => AssertUndefinedRoleRejected((RoleByteKind)201);

    // Same for a long-backed enum, whose value does not fit in an int.
    [Theory]
    [InlineData(RoleLongKind.Admin, "Admin")]
    [InlineData(RoleLongKind.Archivist, "Archivist")]
    public async Task RequireRole_SupportsLongBackedEnums(RoleLongKind role, string expected)
        => Assert.Equal(expected, await RoleClaimValuesFor(role));

    [Fact]
    public Task RequireRole_ThrowsForUndefinedLongBackedEnumValues()
        => AssertUndefinedRoleRejected((RoleLongKind)9_000_000_001L);

    // ---------------------------------------------------------------------
    // OR (one call) versus AND (two calls)
    // ---------------------------------------------------------------------

    // The documented difference, asserted side by side: one call with two roles means "either role",
    // two calls mean "a role from each". Getting these backwards either locks out legitimate users
    // or hands managers an admin-only endpoint.
    [Fact]
    public async Task RequireRole_OneCallIsOrWhileTwoCallsAreAnd()
    {
        await using var app = CreateApp();
        var group = app.MapGroup("role-tests");
        group.Get("either-role", () => "ok").RequireRole(RoleBasic.Admin, RoleBasic.Manager);
        group.Get("both-roles", () => "ok").RequireRole(RoleBasic.Admin).RequireRole(RoleBasic.Manager);

        var orPolicy = await PolicyFor(app, "role-tests/either-role");
        var andPolicy = await PolicyFor(app, "role-tests/both-roles");

        // OR: one requirement listing both roles, so holding either one satisfies it.
        var orRequirement = Assert.Single(orPolicy.Requirements.OfType<RolesAuthorizationRequirement>());
        Assert.Equal(2, orRequirement.AllowedRoles.Count());
        Assert.Contains("Admin", orRequirement.AllowedRoles);
        Assert.Contains("Manager", orRequirement.AllowedRoles);

        // AND: two requirements of one role each, and every requirement has to be satisfied.
        var andRequirements = andPolicy.Requirements.OfType<RolesAuthorizationRequirement>().ToArray();
        Assert.Equal(2, andRequirements.Length);
        Assert.All(andRequirements, requirement => Assert.Single(requirement.AllowedRoles));
        Assert.Equal(
            new[] { "Admin", "Manager" },
            andRequirements
                .SelectMany(requirement => requirement.AllowedRoles)
                .OrderBy(role => role, StringComparer.Ordinal)
                .ToArray());
    }

    // The metadata view of the same thing: two calls must leave two IAuthorizeData entries, because
    // one entry overwriting the other would silently drop a requirement.
    [Fact]
    public async Task RequireRole_CalledTwice_LeavesTwoAuthorizeDataEntries()
    {
        await using var app = CreateApp();
        app.MapGroup("role-tests").Get("twice", () => "ok")
            .RequireRole(RoleBasic.Admin)
            .RequireRole(RoleBasic.Manager);

        var authorizeData = app.Endpoint("role-tests/twice").Metadata.GetOrderedMetadata<IAuthorizeData>();

        Assert.Equal(2, authorizeData.Count);
        Assert.Equal(
            new[] { "Admin", "Manager" },
            authorizeData.Select(data => data.Roles!).OrderBy(roles => roles, StringComparer.Ordinal).ToArray());
    }

    // The canonical AND: a role on the group plus a role on the endpoint. Both requirements have to
    // survive the merge of group conventions into the endpoint.
    [Fact]
    public async Task RequireRole_OnTheGroupAndOnTheEndpoint_ProducesTwoRequirements()
    {
        await using var app = CreateApp();
        var group = app.MapGroup("role-tests-and").RequireRole(RoleBasic.Admin);
        group.Get("leaf", () => "ok").RequireRole(RoleBasic.Manager);

        var policy = await PolicyFor(app, "role-tests-and/leaf");
        var requirements = policy.Requirements.OfType<RolesAuthorizationRequirement>().ToArray();

        Assert.Equal(2, requirements.Length);
        Assert.Equal(
            new[] { "Admin", "Manager" },
            requirements
                .SelectMany(requirement => requirement.AllowedRoles)
                .OrderBy(role => role, StringComparer.Ordinal)
                .ToArray());
    }

    // ---------------------------------------------------------------------
    // TBuilder preservation and group propagation
    // ---------------------------------------------------------------------

    // TBuilder is what makes the call chain: if it degraded to IEndpointConventionBuilder, every
    // RequireRole(...).WithMetadata(...) in an app would stop compiling. The explicit local type is
    // half the assertion - it would not compile if the return type were widened.
    [Fact]
    public async Task RequireRole_OnARouteHandlerBuilder_ReturnsTheSameBuilderSoTheChainContinues()
    {
        await using var app = CreateApp();
        var endpoint = app.MapGroup("role-tests").Get("chained", () => "ok");

        RouteHandlerBuilder returned = endpoint.RequireRole(RoleBasic.Admin).WithMetadata("role-tests-chain-marker");

        Assert.Same(endpoint, returned);
        Assert.Equal("Admin", RolesOf(app, "role-tests/chained"));
        Assert.Contains(app.Endpoint("role-tests/chained").Metadata, item => item as string == "role-tests-chain-marker");
    }

    // Same for a group: the returned value must still be a RouteGroupBuilder, or endpoints could no
    // longer be mapped onto the result of the call.
    [Fact]
    public async Task RequireRole_OnAGroup_ReturnsTheGroupSoEndpointsCanStillBeMappedOntoIt()
    {
        await using var app = CreateApp();
        var group = app.MapGroup("role-tests-group");

        RouteGroupBuilder returned = group.RequireRole(RoleBasic.Admin);
        returned.Get("mapped-after", () => "ok");

        Assert.Same(group, returned);
        Assert.Equal("Admin", RolesOf(app, "role-tests-group/mapped-after"));
    }

    // Group conventions are applied when the endpoints are materialized, so a RequireRole written
    // below the Map calls still protects them — the order people actually write groups in.
    [Fact]
    public async Task RequireRole_OnAGroup_AlsoReachesEndpointsMappedBeforeTheCall()
    {
        await using var app = CreateApp();
        var group = app.MapGroup("role-tests-group");
        group.Get("mapped-before", () => "ok");

        group.RequireRole(RoleBasic.Manager);

        Assert.Equal("Manager", RolesOf(app, "role-tests-group/mapped-before"));
    }

    // The negative control for the two tests above: the role must not leak onto endpoints that were
    // never mapped into the group, which would turn a public route into a 403.
    [Fact]
    public async Task RequireRole_OnAGroup_DoesNotAffectEndpointsOutsideTheGroup()
    {
        await using var app = CreateApp();
        app.MapGroup("role-tests-secured").RequireRole(RoleBasic.Admin).Get("inside", () => "ok");
        app.MapGroup("role-tests-public").Get("outside", () => "ok");

        Assert.Equal("Admin", RolesOf(app, "role-tests-secured/inside"));
        Assert.Empty(app.Endpoint("role-tests-public/outside").Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    // Nested groups are how a module gates everything under it and then tightens one subtree; both
    // levels have to reach the leaf endpoint.
    [Fact]
    public async Task RequireRole_OnNestedGroups_AccumulatesBothRequirementsOnTheLeafEndpoint()
    {
        await using var app = CreateApp();
        var outer = app.MapGroup("role-tests-outer").RequireRole(RoleBasic.Admin);
        var inner = outer.MapGroup("inner").RequireRole(RoleBasic.Manager);
        inner.Get("leaf", () => "ok");

        var authorizeData = app.Endpoint("role-tests-outer/inner/leaf").Metadata.GetOrderedMetadata<IAuthorizeData>();

        Assert.Equal(2, authorizeData.Count);
        Assert.Equal(
            new[] { "Admin", "Manager" },
            authorizeData.Select(data => data.Roles!).OrderBy(roles => roles, StringComparer.Ordinal).ToArray());
    }

    // ---------------------------------------------------------------------
    // Over real HTTP: the status the caller actually gets back
    // ---------------------------------------------------------------------
    // Everything above asserts what RequireRole records. These run a real request through the
    // authentication and authorization middleware, which is the only place the recorded metadata is
    // proved to mean what it is supposed to mean.

    // The control for every status below: an endpoint that never called RequireRole stays open to
    // anonymous callers in the same app. Without it a 401 could just as well mean the test host was
    // misconfigured — a fallback policy, say — rather than that RequireRole did anything at all.
    [Fact]
    public async Task RequireRole_OverHttp_LeavesEndpointsWithoutItOpenToAnonymousCallers()
    {
        await WithRoleAppAsync(
            group =>
            {
                group.Get("guarded", () => "ok").RequireRole(RoleBasic.Admin);
                group.Get("open", () => "ok");
            },
            async client =>
            {
                Assert.Equal(HttpStatusCode.OK, await SendAsRolesAsync(client, "/role-http/open", roleClaims: null));
                Assert.Equal(HttpStatusCode.Unauthorized, await SendAsRolesAsync(client, "/role-http/guarded", roleClaims: null));
            });
    }

    // 401 versus 403 is a contract clients code against: they refresh a token on 401 and give up on
    // 403. An anonymous caller must get the challenge, an authenticated one without the role the
    // refusal — swapping them sends clients into a token-refresh loop they can never escape.
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Manager", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("Auditor,Admin", HttpStatusCode.OK)]
    public async Task RequireRole_OverHttp_AnswersWithTheStatusForTheCallersRoles(string? roleClaims, HttpStatusCode expected)
    {
        var status = await RoleRequestStatusAsync(
            group => group.Get("single", () => "ok").RequireRole(RoleBasic.Admin),
            "/role-http/single",
            roleClaims);

        Assert.Equal(expected, status);
    }

    // OR, end to end: either role on its own is enough, and a third role is not. The metadata tests can
    // only show the two names ended up in one string; this shows the middleware reads that string as
    // "either" rather than "both".
    [Theory]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("Manager", HttpStatusCode.OK)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    public async Task RequireRole_OverHttp_WithTwoRolesInOneCall_AcceptsEitherRole(string roleClaims, HttpStatusCode expected)
    {
        var status = await RoleRequestStatusAsync(
            group => group.Get("either", () => "ok").RequireRole(RoleBasic.Admin, RoleBasic.Manager),
            "/role-http/either",
            roleClaims);

        Assert.Equal(expected, status);
    }

    // AND, end to end. If two calls collapsed into "either", a group-wide gate plus a tighter role on
    // one endpoint would silently become the weaker of the two.
    [Theory]
    [InlineData("Admin", HttpStatusCode.Forbidden)]
    [InlineData("Manager", HttpStatusCode.Forbidden)]
    [InlineData("Admin,Manager", HttpStatusCode.OK)]
    public async Task RequireRole_OverHttp_WithTwoCalls_RequiresARoleFromEachCall(string roleClaims, HttpStatusCode expected)
    {
        var status = await RoleRequestStatusAsync(
            group => group.Get("both", () => "ok").RequireRole(RoleBasic.Admin).RequireRole(RoleBasic.Manager),
            "/role-http/both",
            roleClaims);

        Assert.Equal(expected, status);
    }

    // A role on the group has to reach the endpoints inside it at request time, not just in metadata —
    // this is how a whole module is gated, and a convention that never makes it into the policy leaves
    // every endpoint in the module open.
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task RequireRole_OverHttp_OnAGroup_ProtectsTheEndpointsInsideIt(string? roleClaims, HttpStatusCode expected)
    {
        var status = await RoleRequestStatusAsync(
            group => group.RequireRole(RoleBasic.Admin).Get("in-group", () => "ok"),
            "/role-http/in-group",
            roleClaims);

        Assert.Equal(expected, status);
    }

    // The README's case-sensitivity warning, in the terms an operator experiences it: a provider that
    // issues "admin" for AppRole.Admin produces a 403 on every request until the member is mapped with
    // [EnumMember]. If this ever started passing, the docs would be wrong and so would every app that
    // trusted them.
    [Theory]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.Forbidden)]
    [InlineData("ADMIN", HttpStatusCode.Forbidden)]
    public async Task RequireRole_OverHttp_MatchesTheRoleClaimCaseSensitively(string roleClaims, HttpStatusCode expected)
    {
        var status = await RoleRequestStatusAsync(
            group => group.Get("case", () => "ok").RequireRole(RoleBasic.Admin),
            "/role-http/case",
            roleClaims);

        Assert.Equal(expected, status);
    }

    // ...and [EnumMember] is the documented fix: the value is what has to match the issued claim, while
    // the C# member name must not be accepted in its place.
    [Theory]
    [InlineData("super-admin", HttpStatusCode.OK)]
    [InlineData("SuperAdmin", HttpStatusCode.Forbidden)]
    public async Task RequireRole_OverHttp_MatchesTheEnumMemberValueRatherThanTheMemberName(string roleClaims, HttpStatusCode expected)
    {
        var status = await RoleRequestStatusAsync(
            group => group.Get("enum-member", () => "ok").RequireRole(RoleAnnotated.SuperAdmin),
            "/role-http/enum-member",
            roleClaims);

        Assert.Equal(expected, status);
    }

    // "Equivalent to RequireAuthorization()" in the only terms that matter: any authenticated caller is
    // admitted whatever roles they hold, and an anonymous one is not.
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("SomeUnrelatedRole", HttpStatusCode.OK)]
    public async Task RequireRole_OverHttp_WithNoRoles_AdmitsAnyAuthenticatedCaller(string? roleClaims, HttpStatusCode expected)
    {
        var status = await RoleRequestStatusAsync(
            group => AuthorizationExtensions.RequireRole<RouteHandlerBuilder, RoleBasic>(
                group.Get("any-authenticated", () => "ok")),
            "/role-http/any-authenticated",
            roleClaims);

        Assert.Equal(expected, status);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static WebApplication CreateApp()
    {
        var builder = TestApps.CreateBuilder();
        builder.Services.AddAuthorization();
        return builder.Build();
    }

    /// <summary>The Roles string that a single <c>RequireRole</c> call put on the endpoint.</summary>
    private static string? RolesOf(WebApplication app, string routeSuffix)
        => Assert.Single(app.Endpoint(routeSuffix).Metadata.GetOrderedMetadata<IAuthorizeData>()).Roles;

    /// <summary>Maps one endpoint guarded by <paramref name="roles"/> and returns its Roles string.</summary>
    private static async Task<string?> RoleClaimValuesFor<TRole>(params TRole[] roles) where TRole : struct, Enum
    {
        await using var app = CreateApp();
        app.MapGroup("role-tests").Get("claims", () => "ok").RequireRole(roles);

        return RolesOf(app, "role-tests/claims");
    }

    /// <summary>The exception <c>RequireRole</c> threw for <paramref name="roles"/>, or null.</summary>
    private static async Task<Exception?> RoleFailureFor<TRole>(params TRole[] roles) where TRole : struct, Enum
    {
        await using var app = CreateApp();
        var endpoint = app.MapGroup("role-tests").Get("invalid", () => "ok");

        return Record.Exception(() => { endpoint.RequireRole(roles); });
    }

    /// <summary>
    /// Asserts <c>RequireRole</c> rejected <paramref name="roles"/> with the undefined-member guard and
    /// not with some other <see cref="ArgumentException"/>, and that the message names the enum — all a
    /// developer has to go on when the host refuses to start.
    /// </summary>
    private static async Task AssertUndefinedRoleRejected<TRole>(params TRole[] roles) where TRole : struct, Enum
    {
        var exception = Assert.IsType<ArgumentException>(await RoleFailureFor(roles));

        Assert.Equal("role", exception.ParamName);
        Assert.Contains("is not a defined", exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(TRole).Name, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a test-server app with authentication and authorization wired up, maps
    /// <paramref name="mapEndpoints"/> into a <c>role-http</c> group, starts it and hands the client to
    /// <paramref name="act"/>.
    /// </summary>
    private static async Task WithRoleAppAsync(Action<RouteGroupBuilder> mapEndpoints, Func<HttpClient, Task> act)
    {
        var builder = TestApps.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddAuthentication(RoleHeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, RoleHeaderAuthenticationHandler>(
                RoleHeaderAuthenticationHandler.SchemeName,
                static _ => { });
        builder.Services.AddAuthorization();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        mapEndpoints(app.MapGroup("role-http"));

        await app.StartAsync();
        using var client = app.GetTestClient();

        await act(client);
    }

    /// <summary>The status one GET gets back. <paramref name="roleClaims"/> null means "no credentials".</summary>
    private static async Task<HttpStatusCode> RoleRequestStatusAsync(
        Action<RouteGroupBuilder> mapEndpoints,
        string route,
        string? roleClaims)
    {
        var status = default(HttpStatusCode);
        await WithRoleAppAsync(
            mapEndpoints,
            async client => { status = await SendAsRolesAsync(client, route, roleClaims); });

        return status;
    }

    private static async Task<HttpStatusCode> SendAsRolesAsync(HttpClient client, string route, string? roleClaims)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (roleClaims is not null)
            request.Headers.TryAddWithoutValidation(RoleHeaderAuthenticationHandler.RolesHeader, roleClaims);

        using var response = await client.SendAsync(request);

        return response.StatusCode;
    }

    /// <summary>The policy the authorization middleware would build for that endpoint.</summary>
    private static async Task<AuthorizationPolicy> PolicyFor(WebApplication app, string routeSuffix)
    {
        var policyProvider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorizeData = app.Endpoint(routeSuffix).Metadata.GetOrderedMetadata<IAuthorizeData>();

        var policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData);

        Assert.NotNull(policy);
        return policy!;
    }
}

/// <summary>
/// Authenticates from a header so a test can say "this caller holds exactly these role claims". The
/// values are taken verbatim — no trimming, no case folding — because the case-sensitivity and
/// <c>[EnumMember]</c> tests turn on the exact spelling reaching <see cref="ClaimsPrincipal.IsInRole"/>.
/// No header at all leaves the caller anonymous, which is what separates a 401 from a 403.
/// </summary>
internal sealed class RoleHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "RoleHeaderScheme";
    public const string RolesHeader = "X-Role-Claims";

    public RoleHeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var header))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, "role-tests-caller"));

        foreach (var role in header.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            identity.AddClaim(new Claim(ClaimTypes.Role, role));

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public enum RoleBasic
{
    Admin,
    Manager,
    User,
}

public enum RoleAnnotated
{
    [EnumMember(Value = "super-admin")]
    SuperAdmin,

    [EnumMember(Value = "site admin")]
    SpacedValue,

    [EnumMember(Value = " padded ")]
    PaddedValue,

    [EnumMember(Value = "")]
    BlankValue,

    [EnumMember(Value = "   ")]
    WhitespaceValue,

    [EnumMember(Value = "admin,manager")]
    CommaValue,

    Plain,
}

[Flags]
public enum RoleFlagsKind
{
    None = 0,
    Admin = 1,
    Manager = 2,
    Auditor = 4,
}

public enum RoleWithoutZero
{
    Admin = 1,
    Manager = 2,
}

public enum RoleExplicitNumbers
{
    Admin = 10,
    Manager = 20,
    User = 30,
}

public enum RoleNegativeNumbers
{
    Banned = -5,
    Guest = 0,
    Member = 5,
}

public enum RoleByteKind : byte
{
    Admin = 1,
    Manager = 200,
}

public enum RoleLongKind : long
{
    Admin = 1,
    Archivist = 9_000_000_000,
}
