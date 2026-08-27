using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.Sessions;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Regression coverage for the five-minute browser-session touch race. A
/// concurrency conflict is ambiguous: another request may have refreshed the
/// same active session, or a real revoke may have deleted it. Only an
/// authoritative reload may decide whether authentication remains valid.
/// </summary>
public sealed class BrowserSessionTouchConcurrencyTests : IntegrationTestBase
{
	public BrowserSessionTouchConcurrencyTests(SharedPostgresFixture fixture) : base(fixture) { }

	[Fact]
	public async Task Parallel_touches_keep_both_requests_authenticated()
	{
		var ct = TestContext.Current.CancellationToken;
		var browserSession = await SeedTouchableSessionAsync(ct);
		var policy = new CoordinatedRealmSettings(expectedArrivals: 2);
		var store = Factory.Services.GetRequiredService<IDocumentStore>();

		await using var firstDocumentSession = GetTenantedDocumentSession();
		await using var secondDocumentSession = GetTenantedDocumentSession();
		var first = CreateService(firstDocumentSession, store, policy);
		var second = CreateService(secondDocumentSession, store, policy);

		var firstValidation = first.ValidateSessionAsync(
			browserSession.UserId, browserSession.Id, touch: true, ct);
		var secondValidation = second.ValidateSessionAsync(
			browserSession.UserId, browserSession.Id, touch: true, ct);

		await policy.WaitUntilAllArrivedAsync(ct);
		policy.Release();

		var results = await Task.WhenAll(firstValidation, secondValidation);

		Assert.All(results, result =>
		{
			Assert.NotNull(result);
			Assert.Equal(browserSession.Id, result.Id);
		});

		await using var verify = GetTenantedSession();
		var persisted = await verify.LoadAsync<UserSession>(browserSession.Id, ct);
		Assert.NotNull(persisted);
		Assert.True(persisted.LastActiveAt > browserSession.LastActiveAt);
	}

	[Fact]
	public async Task Revoke_winning_during_touch_still_rejects_the_session()
	{
		var ct = TestContext.Current.CancellationToken;
		var browserSession = await SeedTouchableSessionAsync(ct);
		var policy = new CoordinatedRealmSettings(expectedArrivals: 1);
		var store = Factory.Services.GetRequiredService<IDocumentStore>();

		await using var validatingDocumentSession = GetTenantedDocumentSession();
		var service = CreateService(validatingDocumentSession, store, policy);
		var validation = service.ValidateSessionAsync(
			browserSession.UserId, browserSession.Id, touch: true, ct);

		// ValidateSessionAsync has loaded the old document version and is now
		// paused immediately before its touch write.
		await policy.WaitUntilAllArrivedAsync(ct);
		await using (var revoke = GetTenantedDocumentSession())
		{
			revoke.Delete<UserSession>(browserSession.Id);
			await revoke.SaveChangesAsync(ct);
		}

		policy.Release();

		Assert.Null(await validation);

		await using var verify = GetTenantedSession();
		Assert.Null(await verify.LoadAsync<UserSession>(browserSession.Id, ct));
	}

	private async Task<UserSession> SeedTouchableSessionAsync(CancellationToken ct)
	{
		var now = DateTimeOffset.UtcNow;
		var browserSession = new UserSession
		{
			Id = Guid.NewGuid(),
			UserId = Guid.NewGuid(),
			CreatedAt = now.AddHours(-1),
			LastActiveAt = now.AddMinutes(-10),
			ExpiresAt = now.AddDays(1),
			AbsoluteExpiresAt = now.AddDays(2),
		};

		await using var arrange = GetTenantedDocumentSession();
		arrange.Store(browserSession);
		await arrange.SaveChangesAsync(ct);
		return browserSession;
	}

	private static SessionService CreateService(
		IDocumentSession documentSession,
		IDocumentStore store,
		IRealmSettingsService realmSettings) =>
		new(
			documentSession,
			new FixedTenantSessionFactory(store),
			new StubDeviceInfoService(),
			realmSettings,
			new BrowserSessionConnectionRegistry());

	private sealed class FixedTenantSessionFactory(IDocumentStore store) : ITenantSessionFactory
	{
		public IDocumentSession OpenSession() => store.LightweightSession(TenantConstants.SystemTenantId);
		public IQuerySession OpenQuerySession() => store.QuerySession(TenantConstants.SystemTenantId);
	}

	private sealed class StubDeviceInfoService : IDeviceInfoService
	{
		public DeviceInfo Parse() => DeviceInfo.Unknown;
	}

	private sealed class CoordinatedRealmSettings(int expectedArrivals) : IRealmSettingsService
	{
		private readonly TaskCompletionSource _allArrived =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _release =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _arrivals;

		public async Task<RealmSettingsDoc> LoadAsync(CancellationToken ct = default)
		{
			if (Interlocked.Increment(ref _arrivals) == expectedArrivals)
				_allArrived.TrySetResult();

			await _release.Task.WaitAsync(ct);
			return new RealmSettingsDoc
			{
				BrowserSessions = BrowserSessionPolicy.Defaults,
			};
		}

		public Task WaitUntilAllArrivedAsync(CancellationToken ct) =>
			_allArrived.Task.WaitAsync(ct);

		public void Release() => _release.TrySetResult();

		public Task<RealmSettingsDto> GetDtoAsync(CancellationToken ct = default) =>
			throw new NotSupportedException();

		public Task<PositionSecurityConsequencesDto> PreviewPositionSecurityAsync(
			UpdatePositionSecuritySettingsDto dto,
			CancellationToken ct = default) => throw new NotSupportedException();

		public Task<ErrorOr<RealmSettingsDto>> PatchAsync(
			UpdateRealmSettingsDto dto,
			CancellationToken ct = default) => throw new NotSupportedException();
	}
}
