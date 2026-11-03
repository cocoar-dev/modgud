using System.Net.Http.Headers;
using System.Security.Claims;
using System.Reflection;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Management;
using Modgud.Domain.OAuth.Storage;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Modgud.Tests.Unit.OAuth.Dpop;

namespace Modgud.Tests.Unit.Api;

public sealed class ManagementDpopProofValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private const string Token = "test-only-access-token";
    private const string Uri = "https://management.example/proxy/api/operation";
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class State
    {
        public int Calls;
        public int Disposed;
        public string Mode = "ok";
        public DateTimeOffset? Expiry;
        public CancellationTokenSource? Cancellation;
    }
    private sealed class Replay(State state) : IDpopReplayStore, IDisposable
    {
        public Task<bool> TryRecordAsync(string jti, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct)
        {
            state.Calls++;
            state.Expiry = expiresAt;
            if (state.Mode == "exception") throw new InvalidOperationException("Deliberate replay-store failure.");
            if (state.Mode == "cancel") state.Cancellation!.Cancel();
            return Task.FromResult(state.Mode == "ok");
        }
        public void Dispose() => state.Disposed++;
    }

    private static ServiceProvider Services(State state) => new ServiceCollection()
        .AddScoped<IDpopReplayStore>(_ => new Replay(state))
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    private static ManagementDpopProofValidator Validator(IServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), new Clock());
    private static DefaultHttpContext Http(string? proof = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("management.example");
        http.Request.PathBase = "/proxy";
        http.Request.Path = "/api/operation";
        http.Request.Method = "POST";
        if (proof is not null) http.Request.Headers["DPoP"] = proof;
        return http;
    }
    private static ClaimsPrincipal Caller(string? cnf) => new(new ClaimsIdentity(
        cnf is null ? [] : [new Claim("cnf", cnf)], "unit"));
    private static ClaimsPrincipal Bound(DpopKey key) => Caller(JsonSerializer.Serialize(new { jkt = key.Jkt }));

    [Fact]
    public async Task Unbound_bearer_does_not_record_a_proof_and_unbound_dpop_is_rejected()
    {
        var state = new State();
        await using var services = Services(state);
        var validator = Validator(services);
        Assert.True(await validator.ValidateAsync(Http(), Caller(null), new("Bearer", Token)));
        Assert.False(await validator.ValidateAsync(Http(), Caller(null), new("DPoP", Token)));
        Assert.False(await validator.ValidateAsync(Http(), Caller(null), new("Basic", Token)));
        Assert.Equal(0, state.Calls);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"jkt\":1}")]
    [InlineData("{\"jkt\":\"\"}")]
    public async Task Malformed_confirmation_is_not_downgraded_to_unbound(string cnf)
    {
        var state = new State();
        await using var services = Services(state);
        Assert.False(await Validator(services).ValidateAsync(Http(), Caller(cnf), new("DPoP", Token)));
        Assert.False(await Validator(services).ValidateAsync(Http(), Caller(cnf), new("Bearer", Token)));
        Assert.Equal(0, state.Calls);
    }

    [Fact]
    public async Task Duplicate_confirmation_claims_and_bearer_binding_are_rejected_without_recording()
    {
        using var key = DpopKey.CreateEc();
        var caller = Bound(key);
        ((ClaimsIdentity)caller.Identity!).AddClaim(new Claim("cnf", JsonSerializer.Serialize(new { jkt = key.Jkt })));
        var state = new State();
        await using var services = Services(state);
        var proof = key.CreateProof(Now, htu: Uri, ath: DpopKey.ComputeAth(Token));
        Assert.False(await Validator(services).ValidateAsync(Http(proof), caller, new("DPoP", Token)));
        Assert.False(await Validator(services).ValidateAsync(Http(proof), Bound(key), new("Bearer", Token)));
        var duplicateJkt = Caller("{\"jkt\":\"wrong\",\"jkt\":\"" + key.Jkt + "\"}");
        Assert.False(await Validator(services).ValidateAsync(Http(proof), duplicateJkt, new("DPoP", Token)));
        Assert.Equal(0, state.Calls);
    }

    [Theory]
    [InlineData(-65, true)]
    [InlineData(-66, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task Inclusive_time_boundary_and_pathbase_use_the_existing_validator_window(int seconds, bool valid)
    {
        using var key = DpopKey.CreateEc();
        var state = new State();
        await using var services = Services(state);
        var iat = Now.AddSeconds(seconds);
        var proof = key.CreateProof(iat, htu: Uri, ath: DpopKey.ComputeAth(Token));
        Assert.Equal(valid, await Validator(services).ValidateAsync(Http(proof), Bound(key), new("DPoP", Token)));
        Assert.Equal(valid ? 1 : 0, state.Calls);
        Assert.Equal(valid ? 1 : 0, state.Disposed);
        if (valid) Assert.Equal(iat.AddSeconds(65), state.Expiry);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("exception")]
    public async Task Uncertain_replay_state_fails_closed_and_discards_its_scope(string mode)
    {
        using var key = DpopKey.CreateEc();
        var state = new State { Mode = mode };
        await using var services = Services(state);
        var proof = key.CreateProof(Now, htu: Uri, ath: DpopKey.ComputeAth(Token));
        Assert.False(await Validator(services).ValidateAsync(Http(proof), Bound(key), new("DPoP", Token)));
        Assert.Equal(1, state.Calls);
        Assert.Equal(1, state.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_or_during_replay_never_returns_a_success(bool during)
    {
        using var key = DpopKey.CreateEc();
        using var cancellation = new CancellationTokenSource();
        var state = new State { Mode = "cancel", Cancellation = cancellation };
        await using var services = Services(state);
        var proof = key.CreateProof(Now, htu: Uri, ath: DpopKey.ComputeAth(Token));
        var http = Http(proof);
        http.RequestAborted = cancellation.Token;
        if (!during) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Validator(services).ValidateAsync(http, Bound(key), new("DPoP", Token)));
        Assert.Equal(during ? 1 : 0, state.Calls);
        Assert.Equal(during ? 1 : 0, state.Disposed);
    }

    [Fact]
    public async Task Multiple_proof_headers_are_rejected_before_the_store()
    {
        using var key = DpopKey.CreateEc();
        var proof = key.CreateProof(Now, htu: Uri, ath: DpopKey.ComputeAth(Token));
        var http = Http();
        http.Request.Headers["DPoP"] = new Microsoft.Extensions.Primitives.StringValues([proof, proof]);
        var state = new State();
        await using var services = Services(state);
        Assert.False(await Validator(services).ValidateAsync(http, Bound(key), new("DPoP", Token)));
        Assert.Equal(0, state.Calls);
    }

    [Theory]
    [InlineData("https://management.example/api/operation", false)]
    [InlineData("https://management.example/proxy/api/operation?ignored=yes#fragment", true)]
    public async Task Pathbase_is_required_but_query_and_fragment_do_not_bind_the_proof(string uri, bool expected)
    {
        using var key = DpopKey.CreateEc();
        var state = new State();
        await using var services = Services(state);
        var proof = key.CreateProof(Now, htu: uri, ath: DpopKey.ComputeAth(Token));
        Assert.Equal(expected, await Validator(services).ValidateAsync(Http(proof), Bound(key), new("DPoP", Token)));
        Assert.Equal(expected ? 1 : 0, state.Calls);
    }

    private sealed class AsyncReplay(State state) : IDpopReplayStore, IAsyncDisposable
    {
        public Task<bool> TryRecordAsync(string jti, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct)
        {
            state.Calls++;
            return state.Mode == "exception"
                ? Task.FromException<bool>(new InvalidOperationException("Deliberate async store failure."))
                : Task.FromResult(state.Mode != "reject");
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            state.Disposed++;
            if (state.Mode == "dispose") throw new InvalidOperationException("Deliberate async disposal failure.");
        }
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("reject", false)]
    [InlineData("exception", false)]
    [InlineData("dispose", false)]
    public async Task Async_only_replay_scope_is_awaited_even_after_failure(string mode, bool expected)
    {
        using var key = DpopKey.CreateEc();
        var state = new State { Mode = mode };
        await using var services = new ServiceCollection()
            .AddScoped<IDpopReplayStore>(_ => new AsyncReplay(state))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var proof = key.CreateProof(Now, htu: Uri, ath: DpopKey.ComputeAth(Token));
        Assert.Equal(expected, await Validator(services).ValidateAsync(Http(proof), Bound(key), new("DPoP", Token)));
        Assert.Equal(1, state.Calls);
        Assert.Equal(1, state.Disposed);
    }

    // DispatchProxy keeps this fake at the exact Marten methods the real replay
    // store uses. All unexpected calls fail the test rather than emulate a DB.
    private class ReplaySessionProxy : DispatchProxy
    {
        public string Failure = "load";
        public int Loads;
        public int Inserts;
        public int Saves;
        public int Disposals;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "LoadAsync":
                    Loads++;
                    return Failure == "load"
                        ? Task.FromException<DpopReplayEntry?>(new InvalidOperationException("Deliberate load failure."))
                        : Task.FromResult<DpopReplayEntry?>(null);
                case "DeleteWhere": return null;
                case "Insert": Inserts++; return null;
                case "SaveChangesAsync":
                    Saves++;
                    return Task.FromException(new InvalidOperationException("Deliberate save failure."));
                case "DisposeAsync": Disposals++; return ValueTask.CompletedTask;
                case "Dispose": Disposals++; return null;
                default: throw new InvalidOperationException("Unexpected session member: " + method?.Name);
            }
        }
    }

    [Theory]
    [InlineData("load")]
    [InlineData("save")]
    public async Task Actual_replay_store_read_and_save_failures_are_closed_and_session_disposed(string failure)
    {
        using var key = DpopKey.CreateEc();
        var session = DispatchProxy.Create<IDocumentSession, ReplaySessionProxy>();
        var proxy = (ReplaySessionProxy)(object)session;
        proxy.Failure = failure;
        await using var services = new ServiceCollection()
            .AddScoped<IDocumentSession>(_ => session)
            .AddScoped<IDpopReplayStore, MartenDpopReplayStore>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var proof = key.CreateProof(Now, htu: Uri, ath: DpopKey.ComputeAth(Token));
        Assert.False(await Validator(services).ValidateAsync(Http(proof), Bound(key), new("DPoP", Token)));
        Assert.Equal(1, proxy.Loads);
        Assert.Equal(failure == "save" ? 1 : 0, proxy.Inserts);
        Assert.Equal(failure == "save" ? 1 : 0, proxy.Saves);
        Assert.Equal(1, proxy.Disposals);
    }
}
