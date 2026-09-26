using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Identity.Application.Auth;
using Gecko.Identity.Endpoints.Admin;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Tests.Api;

[Collection(ApiCollection.Name)]
public class InvitationAndApiTokenTests(ApiFactory api)
{
    [Fact]
    public async Task Invitation_create_resend_revoke()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var gateClerk = (await admin.GetFromJsonAsync<List<RoleSummary>>("/api/roles", ct))!.Single(r => r.RoleCode == "GATE_CLERK");
        var email = $"new.clerk.{Guid.NewGuid():N}@sct.co.th";

        var created = await admin.PostAsJsonAsync("/api/invitations", new { email, fullName = "New Clerk", roleId = gateClerk.RoleId }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var invitation = (await created.Content.ReadFromJsonAsync<InvitationResponse>(ct))!;
        Assert.Equal("PENDING", invitation.Status);
        Assert.NotNull(invitation.DevelopmentAcceptToken);

        // Only the hash is stored, and it is the hash of the token we were given.
        await using (var db = TestDatabase.ForTenant(TestDatabase.Sct))
        {
            var stored = await db.Invitations.Where(i => i.InvitationId == invitation.InvitationId).Select(i => i.TokenHash).SingleAsync(ct);
            Assert.Equal(SecretTokens.Hash(invitation.DevelopmentAcceptToken), stored);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/invitations", new { email, roleId = gateClerk.RoleId }, ct)).StatusCode);

        var resent = (await (await admin.PostAsync($"/api/invitations/{invitation.InvitationId}/resend", null, ct)).Content.ReadFromJsonAsync<InvitationResponse>(ct))!;
        Assert.Equal(1, resent.ResentCount);
        Assert.NotEqual(invitation.DevelopmentAcceptToken, resent.DevelopmentAcceptToken);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/invitations/{invitation.InvitationId}", ct)).StatusCode);
        var revoked = await admin.GetFromJsonAsync<PagedResult<InvitationResponse>>($"/api/invitations?status=REVOKED&search={email}", ct);
        Assert.Single(revoked!.Items);
    }

    [Fact]
    public async Task Cannot_invite_an_existing_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var viewer = (await admin.GetFromJsonAsync<List<RoleSummary>>("/api/roles", ct))!.Single(r => r.RoleCode == "VIEWER");

        var response = await admin.PostAsJsonAsync("/api/invitations", new { email = "Yard.LCB@sct.co.th", roleId = viewer.RoleId }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Api_token_secret_is_shown_once_and_states_stay_distinct()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);

        var created = await admin.PostAsJsonAsync("/api/api-tokens", new { name = "Test feed", moduleCode = "EDI", scopes = new[] { "edi.message.write" } }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = (await created.Content.ReadFromJsonAsync<CreatedApiTokenResponse>(ct))!;
        Assert.StartsWith("gk_live_", body.Secret);
        Assert.Equal(body.Secret[..12], body.Token.TokenPrefix);
        Assert.Equal("ACTIVE", body.Token.Status);

        var listed = await admin.GetStringAsync("/api/api-tokens", ct);
        Assert.DoesNotContain(body.Secret, listed);

        var paused = await admin.PutAsJsonAsync($"/api/api-tokens/{body.Token.ApiTokenId}", new { name = "Test feed", isActive = false }, ct);
        Assert.Equal("PAUSED", (await paused.Content.ReadFromJsonAsync<ApiTokenResponse>(ct))!.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/api-tokens/{body.Token.ApiTokenId}", ct)).StatusCode);
        var resume = await admin.PutAsJsonAsync($"/api/api-tokens/{body.Token.ApiTokenId}", new { name = "Test feed", isActive = true }, ct);
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);

        var tokens = await admin.GetFromJsonAsync<List<ApiTokenResponse>>("/api/api-tokens", ct);
        Assert.Equal("REVOKED", tokens!.Single(t => t.ApiTokenId == body.Token.ApiTokenId).Status);
    }

    [Fact]
    public async Task Unknown_module_is_rejected()
    {
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var response = await admin.PostAsJsonAsync("/api/api-tokens", new { name = "Bad", moduleCode = "NOPE" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
