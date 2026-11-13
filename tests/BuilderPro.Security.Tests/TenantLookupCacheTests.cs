using System.Security.Claims;
using BuilderPro.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BuilderPro.Security.Tests;

public sealed class TenantLookupCacheTests
{
    private const string Issuer = "http://idp.test/realms/builderpro";

    /// <summary>Counts calls, answers whatever <see cref="Answer"/> says.</summary>
    private sealed class CountingLookup : ITenantLookup
    {
        public int Calls { get; private set; }
        public TenantLookup Answer { get; set; } = TenantLookup.Ok("t-acme", ["quotes.read"], "c-alice");

        public Task<TenantLookup> ResolveAsync(HttpContext http, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }

    /// <summary>A cache that is down: every call throws.</summary>
    private sealed class BrokenCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("redis down");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("redis down");
        public void Refresh(string key) => throw new InvalidOperationException();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException();
        public void Remove(string key) => throw new InvalidOperationException();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("redis down");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new InvalidOperationException();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("redis down");
    }

    private static IDistributedCache NewCache() => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static CachingTenantLookup Caching(ITenantLookup inner, IDistributedCache cache) =>
        new(inner, cache, new AuthOptions(), TimeSpan.FromMinutes(1), NullLogger<CachingTenantLookup>.Instance);

    private static DefaultHttpContext Caller(string sub, string? tenantHint = null)
    {
        var claims = new List<Claim> { new("iss", Issuer), new("sub", sub) };
        if (tenantHint is not null) claims.Add(new Claim("tenant_id", tenantHint));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
    }

    private static string KeyOf(string sub) => ExternalIdentityKey.ForIssuerAndSubject(Issuer, sub);

    [Fact]
    public async Task Second_lookup_is_served_from_the_cache()
    {
        var inner = new CountingLookup();
        var lookup = Caching(inner, NewCache());

        var first = await lookup.ResolveAsync(Caller("alice"), default);
        var second = await lookup.ResolveAsync(Caller("alice"), default);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(first.TenantId, second.TenantId);
        Assert.Equal("c-alice", second.CustomerId);
        Assert.True(second.HasScope("quotes.read"));
    }

    [Fact]
    public async Task Users_and_tenant_hints_get_separate_entries()
    {
        var inner = new CountingLookup();
        var lookup = Caching(inner, NewCache());

        await lookup.ResolveAsync(Caller("alice"), default);
        await lookup.ResolveAsync(Caller("bob"), default);
        await lookup.ResolveAsync(Caller("alice", tenantHint: "t-acme"), default);

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Refusals_are_never_cached()
    {
        var inner = new CountingLookup { Answer = TenantLookup.Fail(403, "no_membership") };
        var lookup = Caching(inner, NewCache());

        await lookup.ResolveAsync(Caller("erin"), default);
        inner.Answer = TenantLookup.Ok("t-acme");   // erin just got a membership
        var after = await lookup.ResolveAsync(Caller("erin"), default);

        Assert.Equal(2, inner.Calls);
        Assert.Equal("t-acme", after.TenantId);   // no waiting for a TTL
    }

    [Fact]
    public async Task Invalidation_makes_a_revoke_take_effect_immediately()
    {
        var inner = new CountingLookup();
        var cache = NewCache();
        var lookup = Caching(inner, cache);
        await lookup.ResolveAsync(Caller("alice"), default);
        await lookup.ResolveAsync(Caller("alice", "t-acme"), default);

        // Identity revokes alice's t-acme membership and invalidates.
        inner.Answer = TenantLookup.Fail(403, "no_membership");
        await new DistributedTenantCacheInvalidator(cache, NullLogger<DistributedTenantCacheInvalidator>.Instance)
            .InvalidateAsync(KeyOf("alice"), "t-acme", default);

        Assert.Equal("no_membership", (await lookup.ResolveAsync(Caller("alice"), default)).Code);
        Assert.Equal("no_membership", (await lookup.ResolveAsync(Caller("alice", "t-acme"), default)).Code);
    }

    [Fact]
    public async Task Invalidation_leaves_other_users_cached()
    {
        var inner = new CountingLookup();
        var cache = NewCache();
        var lookup = Caching(inner, cache);
        await lookup.ResolveAsync(Caller("bob"), default);

        await new DistributedTenantCacheInvalidator(cache, NullLogger<DistributedTenantCacheInvalidator>.Instance)
            .InvalidateAsync(KeyOf("alice"), "t-acme", default);
        await lookup.ResolveAsync(Caller("bob"), default);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Broken_cache_falls_through_to_identity()
    {
        var inner = new CountingLookup();
        var lookup = Caching(inner, new BrokenCache());

        var result = await lookup.ResolveAsync(Caller("alice"), default);

        Assert.True(result.Resolved);
        Assert.Equal(1, inner.Calls);
        // ...and invalidating against a broken cache doesn't throw either.
        await new DistributedTenantCacheInvalidator(new BrokenCache(), NullLogger<DistributedTenantCacheInvalidator>.Instance)
            .InvalidateAsync("k", "t", default);
    }
}

public sealed class TenantHintTests
{
    private static DefaultHttpContext With(string? header, string? tokenHint = null)
    {
        var claims = new List<System.Security.Claims.Claim> { new("iss", "http://idp.test/realms/builderpro"), new("sub", "dana") };
        if (tokenHint is not null) claims.Add(new System.Security.Claims.Claim("tenant_id", tokenHint));
        var ctx = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "Test")) };
        if (header is not null) ctx.Request.Headers[TenantHint.Header] = header;
        return ctx;
    }

    [Theory]
    [InlineData("t-acme", null, "t-acme")]        // picker wins
    [InlineData("t-acme", "t-globex", "t-acme")]  // ...even over the token claim
    [InlineData(null, "t-globex", "t-globex")]    // no picker: token claim
    [InlineData("bad id!", "t-globex", "t-globex")] // malformed picker ignored
    [InlineData(null, null, null)]
    public void Effective_hint(string? header, string? tokenHint, string? expected)
    {
        var http = With(header, tokenHint);
        var user = ClaimsNormalizer.FromPrincipal(http.User, new AuthOptions());
        Assert.Equal(expected, TenantHint.From(http, user));
    }

    [Fact]
    public async Task The_cache_keeps_one_entry_per_picked_store()
    {
        var calls = 0;
        var inner = new LambdaLookup(h => { calls++; return TenantLookup.Ok(h.Request.Headers[TenantHint.Header].ToString()); });
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var lookup = new CachingTenantLookup(inner, cache, new AuthOptions(), TimeSpan.FromMinutes(1), NullLogger<CachingTenantLookup>.Instance);

        var acme = await lookup.ResolveAsync(With("t-acme"), default);
        var globex = await lookup.ResolveAsync(With("t-globex"), default);
        var acmeAgain = await lookup.ResolveAsync(With("t-acme"), default);

        Assert.Equal(("t-acme", "t-globex", "t-acme"), (acme.TenantId, globex.TenantId, acmeAgain.TenantId));
        Assert.Equal(2, calls);   // third call was a cache hit for the acme key
    }

    private sealed class LambdaLookup(Func<HttpContext, TenantLookup> f) : ITenantLookup
    {
        public Task<TenantLookup> ResolveAsync(HttpContext http, CancellationToken ct) => Task.FromResult(f(http));
    }
}
