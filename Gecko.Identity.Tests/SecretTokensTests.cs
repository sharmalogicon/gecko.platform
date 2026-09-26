using Gecko.Identity.Application.Auth;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Tests;

public class SecretTokensTests
{
    /// <summary>
    /// usp_provision_tenant stores owner invitations as HASHBYTES('SHA2_256', @nvarchar_token).
    /// If C# hashed differently, no provisioned owner could ever accept their invitation.
    /// Ask SQL Server itself rather than trusting a comment.
    /// </summary>
    [Fact]
    public async Task Hash_matches_sql_server_hashbytes_over_nvarchar()
    {
        var ct = TestContext.Current.CancellationToken;
        var token = SecretTokens.NewInvitationToken();

        await using var db = TestDatabase.ForSystem();
        var sqlHash = await db.Database
            .SqlQuery<byte[]>($"SELECT HASHBYTES('SHA2_256', CAST({token} AS NVARCHAR(100))) AS [Value]")
            .SingleAsync(ct);

        Assert.Equal(sqlHash, SecretTokens.Hash(token));
    }

    [Fact]
    public void Tokens_have_the_documented_shape()
    {
        var invitation = SecretTokens.NewInvitationToken();
        var apiKey = SecretTokens.NewApiKey();

        Assert.Matches("^[0-9A-F]{64}$", invitation);
        Assert.Matches("^gk_live_[0-9a-f]{48}$", apiKey);
        Assert.Equal(12, SecretTokens.DisplayPrefix(apiKey).Length);
    }
}
