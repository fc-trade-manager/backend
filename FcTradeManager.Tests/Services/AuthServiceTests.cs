using System.IdentityModel.Tokens.Jwt;
using FcTradeManager.Application.DTOs;
using FcTradeManager.Infrastructure.Services;
using FcTradeManager.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FcTradeManager.Tests.Services;

public class AuthServiceTests
{
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:Secret"] = "unit-test-secret-with-more-than-32-bytes-0123456789",
            ["JwtSettings:Issuer"] = "TestIssuer",
            ["JwtSettings:Audience"] = "TestAudience",
            ["JwtSettings:AccessTokenMinutes"] = "15",
            ["JwtSettings:RefreshTokenDays"] = "7",
        })
        .Build();

    private sealed class Ctx : IDisposable
    {
        public TestDb Db { get; } = new();
        public AuthService Svc { get; }

        public Ctx() => Svc = new AuthService(Db.Context, Config());

        public Task<AuthResponseDto> Register(string email = "me@test.com", string username = "me_trader", string display = "Me Trader", string password = "Passw0rd!") =>
            Svc.RegisterAsync(new RegisterDto { Email = email, Username = username, DisplayName = display, Password = password });

        public void Dispose() => Db.Dispose();
    }

    // REGISTER

    [Fact]
    public async Task Register_creates_the_user_and_returns_tokens()
    {
        using var c = new Ctx();

        var r = await c.Register();

        Assert.Equal("me@test.com", r.Email);
        Assert.Equal("me_trader", r.Username);
        Assert.Equal("Me Trader", r.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(r.Token));
        Assert.False(string.IsNullOrWhiteSpace(r.RefreshToken));
        Assert.Single(c.Db.NewContext().Users);
    }

    [Fact]
    public async Task Register_normalizes_email_and_username_to_lowercase_and_trims_the_display_name()
    {
        using var c = new Ctx();

        var r = await c.Register(email: "  Me@Test.COM ", username: " Me_Trader ", display: "  Me Trader  ");

        Assert.Equal("me@test.com", r.Email);
        Assert.Equal("me_trader", r.Username);
        Assert.Equal("Me Trader", r.DisplayName);
    }

    [Fact]
    public async Task Register_stores_a_bcrypt_hash_not_the_password()
    {
        using var c = new Ctx();

        await c.Register(password: "Passw0rd!");

        var user = c.Db.NewContext().Users.Single();
        Assert.NotEqual("Passw0rd!", user.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("Passw0rd!", user.PasswordHash));
    }

    [Fact]
    public async Task Register_with_a_duplicate_email_is_rejected_ignoring_case()
    {
        using var c = new Ctx();
        await c.Register();

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Register(email: "ME@test.com", username: "someone_else"));
    }

    [Fact]
    public async Task Register_with_a_duplicate_username_is_rejected_ignoring_case()
    {
        using var c = new Ctx();
        await c.Register();

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Register(email: "other@test.com", username: "ME_TRADER"));
    }

    [Fact]
    public async Task Register_issues_an_access_token_for_the_new_user_that_lasts_15_minutes()
    {
        using var c = new Ctx();

        var r = await c.Register();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(r.Token);
        Assert.Equal(r.UserId.ToString(), jwt.Subject);
        Assert.Equal("TestIssuer", jwt.Issuer);
        Assert.Contains("TestAudience", jwt.Audiences);
        Assert.InRange(r.ExpiresAt, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
    }

    // LOGIN

    [Fact]
    public async Task Login_with_the_right_credentials_returns_tokens()
    {
        using var c = new Ctx();
        var registered = await c.Register();

        var r = await c.Svc.LoginAsync(new LoginDto { Email = "me@test.com", Password = "Passw0rd!" });

        Assert.Equal(registered.UserId, r.UserId);
        Assert.False(string.IsNullOrWhiteSpace(r.Token));
        Assert.NotEqual(registered.RefreshToken, r.RefreshToken); // a new session token every time
    }

    [Fact]
    public async Task Login_ignores_email_case_and_surrounding_spaces()
    {
        using var c = new Ctx();
        await c.Register();

        var r = await c.Svc.LoginAsync(new LoginDto { Email = "  ME@Test.com ", Password = "Passw0rd!" });

        Assert.Equal("me@test.com", r.Email);
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_unauthorized()
    {
        using var c = new Ctx();
        await c.Register();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            c.Svc.LoginAsync(new LoginDto { Email = "me@test.com", Password = "wrong" }));
    }

    [Fact]
    public async Task Login_with_an_unknown_email_is_unauthorized()
    {
        using var c = new Ctx();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            c.Svc.LoginAsync(new LoginDto { Email = "nobody@test.com", Password = "Passw0rd!" }));
    }

    // REFRESH TOKEN

    [Fact]
    public async Task Only_the_hash_of_the_refresh_token_is_stored()
    {
        using var c = new Ctx();

        var r = await c.Register();

        var stored = c.Db.NewContext().RefreshTokens.Single();
        Assert.NotEqual(r.RefreshToken, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length); // SHA-256 in hex
        Assert.InRange(stored.ExpiresAt, DateTime.UtcNow.AddDays(6), DateTime.UtcNow.AddDays(8));
    }

    [Fact]
    public async Task Refresh_returns_a_new_pair_and_revokes_the_old_token()
    {
        using var c = new Ctx();
        var first = await c.Register();

        var second = await c.Svc.RefreshAsync(first.RefreshToken);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(first.UserId, second.UserId);
        var tokens = c.Db.NewContext().RefreshTokens.OrderBy(t => t.CreatedAt).ToList();
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens.Single(t => t.RevokedAt != null).ReplacedByTokenHash);
        Assert.Single(tokens, t => t.RevokedAt == null);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_is_unauthorized()
    {
        using var c = new Ctx();
        var first = await c.Register();
        await c.Svc.RefreshAsync(first.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Svc.RefreshAsync(first.RefreshToken));
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_every_active_token_of_the_user()
    {
        using var c = new Ctx();
        var first = await c.Register();
        var second = await c.Svc.RefreshAsync(first.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Svc.RefreshAsync(first.RefreshToken)); // theft assumed

        // the legitimate newest token no longer works either
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Svc.RefreshAsync(second.RefreshToken));
        Assert.All(c.Db.NewContext().RefreshTokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Refresh_with_an_unknown_token_is_unauthorized()
    {
        using var c = new Ctx();
        await c.Register();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Svc.RefreshAsync("not-a-real-token"));
    }

    [Fact]
    public async Task Refresh_with_an_expired_token_is_unauthorized()
    {
        using var c = new Ctx();
        var r = await c.Register();
        var stored = c.Db.Context.RefreshTokens.Single();
        stored.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await c.Db.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Svc.RefreshAsync(r.RefreshToken));
    }

    [Fact]
    public async Task Logout_revokes_the_token_so_it_cannot_be_refreshed()
    {
        using var c = new Ctx();
        var r = await c.Register();

        await c.Svc.LogoutAsync(r.RefreshToken);

        Assert.NotNull(c.Db.NewContext().RefreshTokens.Single().RevokedAt);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Svc.RefreshAsync(r.RefreshToken));
    }

    [Fact]
    public async Task Logout_with_an_unknown_token_does_not_fail()
    {
        using var c = new Ctx();

        await c.Svc.LogoutAsync("not-a-real-token");
    }

    [Fact]
    public async Task Logout_only_revokes_that_session()
    {
        using var c = new Ctx();
        var first = await c.Register();
        var otherSession = await c.Svc.LoginAsync(new LoginDto { Email = "me@test.com", Password = "Passw0rd!" });

        await c.Svc.LogoutAsync(first.RefreshToken);

        var refreshed = await c.Svc.RefreshAsync(otherSession.RefreshToken); // still valid
        Assert.False(string.IsNullOrWhiteSpace(refreshed.Token));
    }
}
