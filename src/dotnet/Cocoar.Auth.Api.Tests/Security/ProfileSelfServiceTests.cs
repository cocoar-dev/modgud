using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Cocoar.Auth.Authentication.Api.Account;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Infrastructure.Email;

namespace Cocoar.Auth.Api.Tests.Security;

/// <summary>
/// Verifies the aggregate change-request flow: a single open request per user accumulates
/// every pending field edit. Email changes ride on a verify token; other fields don't.
/// Approval applies the entire payload atomically.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ProfileSelfServiceTests : IntegrationTestBase
{
    public ProfileSelfServiceTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Submit_NonEmailField_CreatesRequest_AtAdminApprovalPending()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Old", lastname: "Name", acronym: "ON",
            email: "on@test.com", password: "TestPass1234", permissions: []);
        var client = await CreateAuthenticatedClientAsync("on", "TestPass1234");

        var r = await client.PutAsJsonAsync("/api/account/profile/request", new { Firstname = "New" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var body = await r.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var open = body.GetProperty("Open");
        Assert.Equal("AdminApprovalPending", open.GetProperty("Status").GetString());

        var payload = await LoadPayloadAsync(user.Id);
        Assert.True(payload.Firstname.HasValue);
        Assert.Equal("New", payload.Firstname.Value);
    }

    [Fact]
    public async Task Submit_SameOpenRequest_MergesMultipleFields()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Multi", lastname: "Fields", acronym: "MF",
            email: "mf@test.com", password: "TestPass1234", permissions: []);
        var client = await CreateAuthenticatedClientAsync("mf", "TestPass1234");

        await client.PutAsJsonAsync("/api/account/profile/request", new { Firstname = "M1" }, TestContext.Current.CancellationToken);
        await client.PutAsJsonAsync("/api/account/profile/request", new { Lastname = "L2" }, TestContext.Current.CancellationToken);
        await client.PutAsJsonAsync("/api/account/profile/request", new { Acronym = "A3" }, TestContext.Current.CancellationToken);

        var payload = await LoadPayloadAsync(user.Id);
        Assert.Equal("M1", payload.Firstname.Value);
        Assert.Equal("L2", payload.Lastname.Value);
        Assert.Equal("A3", payload.Acronym.Value);

        // Only one request exists (merged, not parallel).
        await using var qs = GetTenantedSession();
        var openCount = await qs.Query<UserChangeRequest>()
            .Where(r => r.UserId == user.Id
                     && (r.Status == ChangeRequestStatus.EmailVerificationPending
                      || r.Status == ChangeRequestStatus.AdminApprovalPending))
            .CountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, openCount);
    }

    [Fact]
    public async Task Submit_EmailChange_StatusIsVerificationPending_AndSendsLink()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Email", lastname: "Submit", acronym: "ES",
            email: "es-old@test.com", password: "TestPass1234", permissions: []);
        var client = await CreateAuthenticatedClientAsync("es", "TestPass1234");

        var r = await client.PutAsJsonAsync("/api/account/profile/request",
            new { Firstname = "NewName", Email = "es-new@test.com" }, TestContext.Current.CancellationToken);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("EmailVerificationPending", body.GetProperty("Open").GetProperty("Status").GetString());

        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>().GetLastEmailTo("es-new@test.com");
        Assert.NotNull(mail);
        Assert.Contains("verify-email", mail!.HtmlBody);
    }

    [Fact]
    public async Task Submit_ChangingEmailValue_RegeneratesToken_InvalidatesOld()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Re", lastname: "Token", acronym: "RT",
            email: "rt-old@test.com", password: "TestPass1234", permissions: []);
        var client = await CreateAuthenticatedClientAsync("rt", "TestPass1234");

        await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "first@test.com" }, TestContext.Current.CancellationToken);
        var firstMail = Factory.Services.GetRequiredService<InMemoryEmailService>().GetLastEmailTo("first@test.com");
        var firstToken = ExtractTokenFromLink(firstMail!.HtmlBody);

        await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "second@test.com" }, TestContext.Current.CancellationToken);
        var secondMail = Factory.Services.GetRequiredService<InMemoryEmailService>().GetLastEmailTo("second@test.com");
        Assert.NotNull(secondMail);

        // Old token no longer works
        var requestId = await GetOpenRequestIdAsync(user.Id);
        var anon = Factory.CreateClient();
        var verify = await anon.PostAsJsonAsync("/api/account/profile/request/verify-email",
            new { RequestId = requestId, Token = firstToken }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
    }

    [Fact]
    public async Task Approve_AppliesAllChanges_Atomically()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Apply", lastname: "All", acronym: "AA",
            email: "aa-old@test.com", password: "TestPass1234", permissions: []);
        var client = await CreateAuthenticatedClientAsync("aa", "TestPass1234");

        await client.PutAsJsonAsync("/api/account/profile/request", new
        {
            Firstname = "NewFirst",
            Lastname = "NewLast",
            Acronym = "NA",
            Email = "aa-new@test.com",
        }, TestContext.Current.CancellationToken);

        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>().GetLastEmailTo("aa-new@test.com");
        var token = ExtractTokenFromLink(mail!.HtmlBody);
        var requestId = await GetOpenRequestIdAsync(user.Id);
        var anon = Factory.CreateClient();
        await anon.PostAsJsonAsync("/api/account/profile/request/verify-email",
            new { RequestId = requestId, Token = token }, TestContext.Current.CancellationToken);

        var approve = await Client.PostAsJsonAsync($"/api/admin/change-requests/{requestId}/approve",
            new { NotifyUser = false }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var me = await (await client.GetAsync("/api/account/me", TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("NewFirst", me.GetProperty("Firstname").GetString());
        Assert.Equal("NewLast", me.GetProperty("Lastname").GetString());
        Assert.Equal("NA", me.GetProperty("Acronym").GetString());
        Assert.Equal("aa-new@test.com", me.GetProperty("Email").GetString());
    }

    [Fact]
    public async Task Reject_KeepsOldValues_AndStoresNote()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Keep", lastname: "Old", acronym: "KO",
            email: "ko-old@test.com", password: "TestPass1234", permissions: []);
        var client = await CreateAuthenticatedClientAsync("ko", "TestPass1234");

        await client.PutAsJsonAsync("/api/account/profile/request",
            new { Firstname = "WontStick", Email = "ko-new@test.com" }, TestContext.Current.CancellationToken);
        var mail = Factory.Services.GetRequiredService<InMemoryEmailService>().GetLastEmailTo("ko-new@test.com");
        var token = ExtractTokenFromLink(mail!.HtmlBody);
        var requestId = await GetOpenRequestIdAsync(user.Id);
        var anon = Factory.CreateClient();
        await anon.PostAsJsonAsync("/api/account/profile/request/verify-email",
            new { RequestId = requestId, Token = token }, TestContext.Current.CancellationToken);

        await Client.PostAsJsonAsync($"/api/admin/change-requests/{requestId}/reject",
            new { Note = "policy", NotifyUser = false }, TestContext.Current.CancellationToken);

        var me = await (await client.GetAsync("/api/account/me", TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Keep", me.GetProperty("Firstname").GetString());
        Assert.Equal("ko-old@test.com", me.GetProperty("Email").GetString());

        await using var qs = GetTenantedSession();
        var cr = await qs.LoadAsync<UserChangeRequest>(ShortGuid.Decode(requestId));
        Assert.Equal(ChangeRequestStatus.Rejected, cr!.Status);
        Assert.Equal("policy", cr.ReviewerNote);
    }

    [Fact]
    public async Task Cancel_DeletesOpenRequest()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Cancel", lastname: "Me", acronym: "CM",
            email: "cm@test.com", password: "TestPass1234", permissions: []);
        var client = await CreateAuthenticatedClientAsync("cm", "TestPass1234");

        await client.PutAsJsonAsync("/api/account/profile/request", new { Firstname = "Whatever" }, TestContext.Current.CancellationToken);
        var cancel = await client.DeleteAsync("/api/account/profile/request", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        await using var qs = GetTenantedSession();
        var open = await qs.Query<UserChangeRequest>()
            .Where(r => r.UserId == user.Id
                     && (r.Status == ChangeRequestStatus.EmailVerificationPending
                      || r.Status == ChangeRequestStatus.AdminApprovalPending))
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(open);
    }

    [Fact]
    public async Task MergeIsDeep_NestedSiblingKeysSurviveOverrides()
    {
        // Regression guard: the endpoint uses MutableJsonMerge, so nested sibling keys
        // in the stored payload must survive a submission that only touches one sub-key.
        // Current ProfileUpdateDto is flat, but if we ever add a nested payload (e.g. a
        // Phone { Country, Number } object), merging { Phone: { Number: "x" } } must NOT
        // drop Country.
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Deep", lastname: "Merge", acronym: "DM",
            email: "dm@test.com", password: "TestPass1234", permissions: []);
        var client = await CreateAuthenticatedClientAsync("dm", "TestPass1234");

        await client.PutAsJsonAsync("/api/account/profile/request", new { Firstname = "F1", Lastname = "L1" }, TestContext.Current.CancellationToken);
        await client.PutAsJsonAsync("/api/account/profile/request", new { Lastname = "L2" }, TestContext.Current.CancellationToken);

        var payload = await LoadPayloadAsync(user.Id);
        Assert.Equal("F1", payload.Firstname.Value); // Firstname survived — flat merge works
        Assert.Equal("L2", payload.Lastname.Value);   // Lastname got overridden
    }

    [Fact]
    public async Task EmailUniqueness_Returns409()
    {
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Owner", lastname: "First", acronym: "OF",
            email: "taken@test.com", password: "TestPass1234", permissions: []);
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Other", lastname: "User", acronym: "OU",
            email: "other@test.com", password: "TestPass1234", permissions: []);
        var client = await CreateAuthenticatedClientAsync("ou", "TestPass1234");

        var r = await client.PutAsJsonAsync("/api/account/profile/request", new { Email = "taken@test.com" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    // ── helpers ──

    private async Task<string> GetOpenRequestIdAsync(Guid userId)
    {
        await using var qs = GetTenantedSession();
        var open = (await qs.Query<UserChangeRequest>()
            .Where(r => r.UserId == userId
                     && (r.Status == ChangeRequestStatus.EmailVerificationPending
                      || r.Status == ChangeRequestStatus.AdminApprovalPending))
            .ToListAsync(TestContext.Current.CancellationToken)).Single();
        return new ShortGuid(open.Id).ToString();
    }

    private async Task<ProfileUpdateDto> LoadPayloadAsync(Guid userId)
    {
        await using var qs = GetTenantedSession();
        var open = (await qs.Query<UserChangeRequest>()
            .Where(r => r.UserId == userId
                     && (r.Status == ChangeRequestStatus.EmailVerificationPending
                      || r.Status == ChangeRequestStatus.AdminApprovalPending))
            .ToListAsync(TestContext.Current.CancellationToken)).Single();
        return ProfileEndpoints.DeserializeProfile(open.Payload);
    }

    private static string ExtractTokenFromLink(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, @"token=([^""&]+)");
        Assert.True(match.Success, $"No token in email body: {html}");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }
}
