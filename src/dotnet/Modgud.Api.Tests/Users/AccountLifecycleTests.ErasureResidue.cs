using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.Inbox;
using Modgud.Authentication.Domain;
using Modgud.Domain.Assets;
using Modgud.Domain.Realms;
using Modgud.Domain.RealmSettings;
using Modgud.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// B3-EI-01 / B10-PA04 — a permanent erasure left copies of the user's name and username
/// outside the user's own stream: in the admin inbox items about the user's change requests
/// (the UserLabel parameter), in inbox items addressed to the user, in an asset's
/// UploadedByUsername and in the PublishedBy of page and composition revisions. After the
/// erasure the marker that only this user's name carries is nowhere in the event data, the
/// inbox read model, the asset or the realm settings.
/// </summary>
public partial class AccountLifecycleTests
{
    [Fact]
    public async Task Permanent_erase_leaves_no_copy_of_the_name_in_inbox_assets_or_page_revisions()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = $"Resid{Guid.NewGuid():N}"[..16];
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Era", lastname: marker, acronym: "RSD", email: $"{marker.ToLowerInvariant()}@test.com");
        var userName = user.UserName!;

        var changeRequestId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        IReadOnlyList<Guid> adminItems, ownItems;
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new UserChangeRequest
            {
                Id = changeRequestId, UserId = user.Id, RequestedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow, Status = ChangeRequestStatus.AdminApprovalPending,
            });
            session.Store(new Asset
            {
                Id = assetId, FileName = "logo.png", ContentType = "image/png", Data = [1, 2, 3],
                UploadedAt = DateTimeOffset.UtcNow, UploadedByUserId = user.Id, UploadedByUsername = userName,
            });
            var settings = await session.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct) ?? new RealmSettings();
            settings.PageCompositions ??= [];
            settings.PageCompositions.Add(new PageComposition
            {
                Id = $"comp-{marker}", Name = "Erasure test",
                Versions = [new PageCompositionVersion { Number = 1, Root = "{}", PublishedAt = DateTimeOffset.UtcNow, PublishedBy = userName }],
            });
            settings.PageSlots ??= [];
            settings.PageSlots[$"slot-{marker}"] = new RealmPageSlot
            {
                Variants = [new PageVariant
                {
                    Id = $"var-{marker}", Name = "Erasure test",
                    Revisions = [new PageVariantRevision { Number = 1, Schema = "{}", PublishedAt = DateTimeOffset.UtcNow, PublishedBy = userName }],
                }],
            };
            session.Store(settings);
            await session.SaveChangesAsync(ct);

            var inbox = scope.ServiceProvider.GetRequiredService<IInboxNotifier>();
            adminItems = await inbox.NotifyAsync(InboxKind.AdminChangeRequestSubmitted, [DefaultUser!.Id],
                "inbox.kinds.adminChangeRequestSubmitted.title",
                parameters: new { UserLabel = $"Era {marker} ({userName})", FieldList = "Lastname", ChangeCount = 1 },
                sourceType: "change-request", sourceId: changeRequestId, ct: ct);
            ownItems = await inbox.NotifyAsync(InboxKind.ChangeRequestRejected, [user.Id],
                "inbox.kinds.changeRequestRejected.title",
                parameters: new { ReviewerNote = $"Dear {marker}, please retry" },
                sourceType: "change-request", sourceId: changeRequestId, ct: ct);
        }
        await Factory.WaitForProjectionsAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{new ShortGuid(user.Id)}/permanent")
        {
            Content = JsonContent.Create(new { Reason = "erasure residue test" }),
        };
        using (var erased = await Client.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.NoContent, erased.StatusCode);
        await Factory.WaitForProjectionsAsync();

        await using var query = GetTenantedSession();
        var eventSchema = query.DocumentStore.Options.Events.DatabaseSchemaName;
        var eventHits = await query.QueryAsync<int>(
            $"select count(*)::int from {eventSchema}.mt_events where data::text like ?", $"%{marker}%");
        Assert.Equal(0, eventHits.Single());

        var itemIds = adminItems.Concat(ownItems).ToList();
        Assert.Empty(await query.Query<InboxItemView>().Where(i => itemIds.Contains(i.Id)).ToListAsync(ct));

        var asset = await query.LoadAsync<Asset>(assetId, ct);
        Assert.NotNull(asset);
        Assert.Null(asset!.UploadedByUsername);

        var after = await query.LoadAsync<RealmSettings>(RealmSettings.SingletonId, ct);
        Assert.Null(after!.PageCompositions!.Single(c => c.Id == $"comp-{marker}").Versions.Single().PublishedBy);
        Assert.Null(after.PageSlots![$"slot-{marker}"].Variants.Single().Revisions.Single().PublishedBy);
    }

    [Fact]
    public async Task Inbox_retention_hard_delete_leaves_no_item_parameters_in_the_events()
    {
        // B3-EI-05 — the retention promised to wipe a dismissed admin item together with
        // its event stream, but only archived the stream: the created event kept the
        // requester's label.
        var ct = TestContext.Current.CancellationToken;
        var marker = $"Retn{Guid.NewGuid():N}"[..16];
        Guid itemId;
        using (var scope = Factory.Services.CreateScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxNotifier>();
            itemId = (await inbox.NotifyAsync(InboxKind.AdminChangeRequestSubmitted, [DefaultUser!.Id],
                "inbox.kinds.adminChangeRequestSubmitted.title",
                parameters: new { UserLabel = $"Some {marker}", FieldList = "Lastname", ChangeCount = 1 },
                sourceType: "change-request", sourceId: Guid.NewGuid(), ct: ct)).Single();
            await inbox.DismissByIdsAsync([itemId], ct);
        }
        await Factory.WaitForProjectionsAsync();

        await using (var backdate = GetTenantedDocumentSession())
        {
            var view = (await backdate.LoadAsync<InboxItemView>(itemId, ct))!;
            Assert.NotNull(view.DismissedAt);
            backdate.Store(view with { DismissedAt = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-40), DateTimeKind.Unspecified) });
            await backdate.SaveChangesAsync(ct);
        }

        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IInboxRetentionService>().ExecuteAsync(ct);

        await using var query = GetTenantedSession();
        Assert.Null(await query.LoadAsync<InboxItemView>(itemId, ct));
        var eventSchema = query.DocumentStore.Options.Events.DatabaseSchemaName;
        var eventHits = await query.QueryAsync<int>(
            $"select count(*)::int from {eventSchema}.mt_events where data::text like ?", $"%{marker}%");
        Assert.Equal(0, eventHits.Single());
    }
}
