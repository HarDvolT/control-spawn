namespace Prism.Core.Ipc;

/// <summary>Error texts the app recognises. The wire format is unchanged: these are the strings the service always sent.</summary>
public static class PipeErrors
{
    /// <summary>The service didn't recognise the connecting app as the installed (or, in a dev run, the built) copy.</summary>
    public const string ReadOnly = "Unverified client has read-only access.";
}
