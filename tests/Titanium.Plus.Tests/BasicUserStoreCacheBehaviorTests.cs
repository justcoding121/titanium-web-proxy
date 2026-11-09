using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security;

namespace Titanium.Plus.Tests;

/// <summary>
///     Hashed-login cache overflow and bcrypt verify false path.
/// </summary>
[TestClass]
public class BasicUserStoreCacheBehaviorTests
{
    [TestMethod]
    public void Verify_ClearsCacheWhenFull_AndBcryptRejectsWrongPassword()
    {
        var salt = Encoding.UTF8.GetBytes("twp-cache-salt!!");
        var entries = new List<KeyValuePair<string, string>>(1025);
        for (var i = 0; i < 1025; i++)
        {
            entries.Add(new($"u{i}", Pbkdf2PasswordHash.Format("pw", salt, iterations: 1, hashBytes: 32)));
        }

        var store = new BasicUserStore(entries);
        for (var i = 0; i < 1025; i++)
        {
            Assert.IsTrue(store.Verify($"u{i}", "pw"), $"user u{i}");
        }

        Assert.IsTrue(store.Verify("u0", "pw"), "after Clear the first login must still verify");

        var bcryptHash = "{bcrypt}" + BCrypt.Net.BCrypt.HashPassword("right", workFactor: 4);
        var bcrypt = new BasicUserStore(new Dictionary<string, string> { ["bob"] = bcryptHash });
        Assert.IsTrue(bcrypt.Verify("bob", "right"));
        Assert.IsFalse(bcrypt.Verify("bob", "wrong"));
        Assert.IsFalse(bcrypt.Verify("missing", "right"));
    }
}
