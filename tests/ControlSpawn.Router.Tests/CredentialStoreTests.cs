using System.Text;

namespace ControlSpawn.Router.Tests;

public class CredentialStoreTests
{
    private sealed class FakeProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => Encoding.UTF8.GetBytes("enc:" + Convert.ToBase64String(data));

        public byte[] Unprotect(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data);
            return Convert.FromBase64String(text["enc:".Length..]);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "controlspawn-router-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void PasswordRoundTrip()
    {
        var directory = TempDirectory();
        try
        {
            var store = new CredentialStore(directory, new FakeProtector());
            store.SavePassword("hunter2");
            Assert.Equal("hunter2", store.LoadPassword());

            // A fresh instance reads the same encrypted file.
            var reopened = new CredentialStore(directory, new FakeProtector());
            Assert.Equal("hunter2", reopened.LoadPassword());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SessionRoundTripAndForget()
    {
        var directory = TempDirectory();
        try
        {
            var store = new CredentialStore(directory, new FakeProtector());
            store.SaveSession(new string('S', 64));
            Assert.Equal(new string('S', 64), store.LoadSession());

            store.SaveSession("");
            Assert.Null(store.LoadSession());

            store.SavePassword("secret");
            store.SaveSession("abc");
            store.ForgetPassword();
            Assert.Null(store.LoadPassword());
            Assert.Null(store.LoadSession());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingOrBrokenFileReturnsNull()
    {
        var directory = TempDirectory();
        try
        {
            var store = new CredentialStore(directory, new FakeProtector());
            Assert.Null(store.LoadPassword());

            File.WriteAllText(Path.Combine(directory, CredentialStore.CredentialsFile), "not json");
            Assert.Null(store.LoadPassword());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
