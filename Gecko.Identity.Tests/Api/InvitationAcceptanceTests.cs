using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.Identity.Application.Auth;
using Gecko.Identity.Endpoints;
using Gecko.Identity.Endpoints.Admin;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Tests.Api;

/// <summary>ADR-006 step 5: the only way an account gets a password. Users created here are soft-deleted at the end.</summary>
[Collection(ApiCollection.Name)]
public class InvitationAcceptanceTests(ApiFactory api)
{
    private const string NewPassword = "Laem-Chabang-2026!";

    private async Task<(HttpClient Admin, Guid RoleId, Guid BranchId)> SctSetupAsync(string roleCode, CancellationToken ct)
    {
        var admin = await api.ClientForAsync(ApiFactory.SctAdmin);
        var role = (await admin.GetFromJsonAsync<List<RoleSummary>>("/api/roles", ct))!.Single(r => r.RoleCode == roleCode);
        var branch = (await admin.GetFromJsonAsync<PagedResult<BranchResponse>>("/api/branches", ct))!.Items.First();
        return (admin, role.RoleId, branch.BranchId);
    }

    private static async Task DeleteUserAsync(HttpClient admin, string email, CancellationToken ct)
    {
        var page = await admin.GetFromJsonAsync<PagedResult<UserSummary>>($"/api/users?search={email}", ct);
        foreach (var user in page!.Items.Where(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            await admin.DeleteAsync($"/api/users/{user.UserId}", ct);
    }

    [Fact]
    public async Task New_user_accepts_sets_a_password_and_is_signed_in_with_the_invited_role()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, roleId, branchId) = await SctSetupAsync("GATE_CLERK", ct);
        var email = $"clerk.{Guid.NewGuid():N}@sct.co.th"[..40];
        using var anonymous = api.CreateSessionClient();

        try
        {
            var invitation = (await (await admin.PostAsJsonAsync("/api/invitations", new { email, fullName = "Invited Clerk", roleId, branchId }, ct))
                .Content.ReadFromJsonAsync<InvitationResponse>(ct))!;
            var token = invitation.DevelopmentAcceptToken!;

            var preview = await anonymous.PostAsJsonAsync("/auth/invitations/preview", new { token }, ct);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal(email, (await preview.Content.ReadFromJsonAsync<InvitationPreview>(ct))!.Email);

            var accepted = await anonymous.PostAsJsonAsync("/auth/invitations/accept", new { token, password = NewPassword }, ct);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.NotNull(SessionHttp.RefreshCookieHeader(accepted));
            var accessToken = (await accepted.Content.ReadFromJsonAsync<LoginResponse>(ct))!.AccessToken;

            using var me = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
            me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var claims = (await (await anonymous.SendAsync(me, ct)).Content.ReadFromJsonAsync<MeResponse>(ct))!;
            Assert.Equal([$"GATE_CLERK@{branchId}"], claims.Roles);
            Assert.Equal([branchId.ToString()], claims.Branches);
            Assert.Empty(claims.Permissions);   // branch-scoped role => no tenant-wide permissions

            // The token is single-use, and the password actually works for a normal login.
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/auth/invitations/accept", new { token, password = NewPassword }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/auth/login", new { email, password = NewPassword }, ct)).StatusCode);

            await using var db = TestDatabase.ForSystem();
            var credential = await (
                from c in db.Credentials
                join u in db.Users on c.UserId equals u.UserId
                where u.EmailNormalised == email && c.IsCurrent
                select c).SingleAsync(ct);
            Assert.Equal(PasswordHasher.Argon2Id, credential.Algorithm);
        }
        finally
        {
            await DeleteUserAsync(admin, email, ct);
        }
    }

    [Fact]
    public async Task Provisioned_style_invited_user_is_activated_not_duplicated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, roleId, _) = await SctSetupAsync("VIEWER", ct);
        var email = $"owner.{Guid.NewGuid():N}@sct.co.th"[..40];
        using var anonymous = api.CreateSessionClient();

        // What usp_provision_tenant leaves behind: a user row, INVITED, no credential.
        Guid existingId;
        await using (var system = TestDatabase.ForSystem())
        {
            var invited = new User { TenantId = TestDatabase.Sct, Email = email, EmailNormalised = email, FullName = "Pre-provisioned", UserType = "OPERATOR", Status = "INVITED" };
            system.Users.Add(invited);
            await system.SaveChangesAsync(ct);
            existingId = invited.UserId;
        }

        try
        {
            var created = await admin.PostAsJsonAsync("/api/invitations", new { email, roleId }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);   // INVITED users can be re-invited
            var token = (await created.Content.ReadFromJsonAsync<InvitationResponse>(ct))!.DevelopmentAcceptToken!;

            Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/auth/invitations/accept", new { token, password = NewPassword, fullName = "Now Active" }, ct)).StatusCode);

            await using var db = TestDatabase.ForSystem();
            var rows = await db.Users.Where(u => u.EmailNormalised == email).ToListAsync(ct);
            var user = Assert.Single(rows);
            Assert.Equal(existingId, user.UserId);
            Assert.Equal("ACTIVE", user.Status);
            Assert.Equal("Now Active", user.FullName);
            Assert.NotNull(user.EmailVerifiedAt);
        }
        finally
        {
            await DeleteUserAsync(admin, email, ct);
        }
    }

    [Fact]
    public async Task Email_owned_by_another_tenant_cannot_be_claimed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, roleId, _) = await SctSetupAsync("VIEWER", ct);
        using var anonymous = api.CreateSessionClient();

        // SCT cannot see SSS's users (RLS), so the invitation is created...
        var created = await admin.PostAsJsonAsync("/api/invitations", new { email = ApiFactory.SssAdmin, roleId }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var invitation = (await created.Content.ReadFromJsonAsync<InvitationResponse>(ct))!;

        try
        {
            // ...but accepting it must not take over SSS's owner account.
            var accept = await anonymous.PostAsJsonAsync("/auth/invitations/accept", new { token = invitation.DevelopmentAcceptToken, password = NewPassword }, ct);
            Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        }
        finally
        {
            await admin.DeleteAsync($"/api/invitations/{invitation.InvitationId}", ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/auth/login", new { email = ApiFactory.SssAdmin, password = ApiFactory.Password }, ct)).StatusCode);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public async Task Weak_password_is_a_400(string password)
    {
        using var anonymous = api.CreateSessionClient();
        var response = await anonymous.PostAsJsonAsync("/auth/invitations/accept", new { token = new string('A', 64), password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_token_is_404()
    {
        using var anonymous = api.CreateSessionClient();
        var response = await anonymous.PostAsJsonAsync("/auth/invitations/accept", new { token = SecretTokens.NewInvitationToken(), password = NewPassword }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
