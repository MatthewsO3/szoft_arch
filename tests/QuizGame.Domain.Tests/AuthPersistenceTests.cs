using Xunit;
using QuizGame.Server.Auth;
using QuizGame.Server.Services;

namespace QuizGame.Domain.Tests;

public class AuthPersistenceTests
{
    // Few iterations: the algorithm is the same, the tests just run fast.
    private static readonly PasswordHasher Hasher = new(iterations: 1_000);

    [Fact]
    public void Hash_then_verify_succeeds_and_wrong_password_fails()
    {
        var hash = Hasher.Hash("s3cret");
        Assert.True(Hasher.Verify("s3cret", hash));
        Assert.False(Hasher.Verify("S3cret", hash));
    }

    [Fact]
    public void Hashes_are_salted()
    {
        Assert.NotEqual(Hasher.Hash("same"), Hasher.Hash("same"));
    }

    [Fact]
    public void Verify_uses_the_stored_iteration_count()
    {
        var old = new PasswordHasher(500).Hash("pw");
        Assert.True(new PasswordHasher(2_000).Verify("pw", old));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("PBKDF2-SHA256$abc$AAAA$AAAA")]
    [InlineData("PBKDF2-SHA256$1000$!!!$!!!")]
    [InlineData("PBKDF2-SHA256$99999999999$AAAA$AAAA")]
    [InlineData("OTHER$1000$AAAA$AAAA")]
    public void Verify_rejects_malformed_hashes_without_throwing(string stored)
    {
        Assert.False(Hasher.Verify("pw", stored));
    }

    [Fact]
    public async Task Store_creates_once_and_matches_case_insensitively()
    {
        var store = new InMemoryGameMasterStore();
        Assert.True(await store.CreateIfMissingAsync("Admin", "h1"));
        Assert.False(await store.CreateIfMissingAsync("  admin ", "h2"));

        var found = await store.FindByUsernameAsync("ADMIN");
        Assert.NotNull(found);
        Assert.Equal("Admin", found!.Username);
        Assert.Equal("h1", found.PasswordHash);
    }

    [Fact]
    public async Task Authenticator_accepts_good_and_rejects_bad_credentials()
    {
        var store = new InMemoryGameMasterStore();
        await AdminSeeder.EnsureAsync(store, Hasher, new SeedOptions { AdminUsername = "admin", AdminPassword = "pw" });
        var auth = new GameMasterAuthenticator(store, Hasher);

        var ok = await auth.AuthenticateAsync("ADMIN", "pw");
        Assert.NotNull(ok);
        Assert.Equal("admin", ok!.Username);

        Assert.Null(await auth.AuthenticateAsync("admin", "wrong"));
        Assert.Null(await auth.AuthenticateAsync("nobody", "pw"));
        Assert.Null(await auth.AuthenticateAsync("", "pw"));
        Assert.Null(await auth.AuthenticateAsync("admin", null));
        Assert.Null(await auth.AuthenticateAsync("admin", new string('x', 1000)));
    }

    [Fact]
    public async Task Seeding_twice_keeps_the_original_account()
    {
        var store = new InMemoryGameMasterStore();
        var seed = new SeedOptions { AdminUsername = "admin", AdminPassword = "pw" };
        Assert.True(await AdminSeeder.EnsureAsync(store, Hasher, seed));
        var id = (await store.FindByUsernameAsync("admin"))!.Id;

        seed.AdminPassword = "changed";
        Assert.False(await AdminSeeder.EnsureAsync(store, Hasher, seed));
        Assert.Equal(id, (await store.FindByUsernameAsync("admin"))!.Id);
    }

    [Fact]
    public async Task Seeding_without_credentials_fails_loudly()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AdminSeeder.EnsureAsync(new InMemoryGameMasterStore(), Hasher, new SeedOptions()));
    }
}
