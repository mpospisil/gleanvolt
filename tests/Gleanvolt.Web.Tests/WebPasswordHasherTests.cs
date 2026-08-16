using Gleanvolt.Web.Auth;

namespace Gleanvolt.Web.Tests;

public class WebPasswordHasherTests
{
    [Fact]
    public void Verifies_a_password_against_its_own_hash()
    {
        var hash = WebPasswordHasher.Hash("correct horse battery staple");

        Assert.True(WebPasswordHasher.Verify(hash, "correct horse battery staple"));
    }

    [Fact]
    public void Rejects_the_wrong_password()
    {
        var hash = WebPasswordHasher.Hash("correct horse battery staple");

        Assert.False(WebPasswordHasher.Verify(hash, "wrong password"));
    }

    [Fact]
    public void Produces_a_different_hash_each_time_for_the_same_password()
    {
        // PasswordHasher salts every hash, so two hashes of the same password must never be equal --
        // otherwise Web:PasswordHash values would be comparable and a leaked config would reveal
        // whether two installations share a password.
        var first = WebPasswordHasher.Hash("hunter2");
        var second = WebPasswordHasher.Hash("hunter2");

        Assert.NotEqual(first, second);
        Assert.True(WebPasswordHasher.Verify(first, "hunter2"));
        Assert.True(WebPasswordHasher.Verify(second, "hunter2"));
    }

    [Fact]
    public void Rejects_an_empty_hash_rather_than_throwing()
    {
        // A misconfigured (blank) Web:PasswordHash must never be satisfiable, including by a blank
        // submission -- the fail-fast startup check exists to stop this state, but Verify itself
        // must not treat "nothing configured" as "no password required".
        Assert.False(WebPasswordHasher.Verify("", "anything"));
    }

    [Fact]
    public void Rejects_an_empty_password()
    {
        var hash = WebPasswordHasher.Hash("hunter2");

        Assert.False(WebPasswordHasher.Verify(hash, ""));
    }
}
