namespace Prism.Router;

/// <summary>Network or protocol failure; the caller should retry later.</summary>
public class RouterError : Exception
{
    public RouterError(string message) : base(message)
    {
    }

    public RouterError(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Login was refused (wrong password, or another session holds the login).</summary>
public class AuthError : RouterError
{
    public AuthError(string message, bool alreadyLoggedIn = false) : base(message)
    {
        AlreadyLoggedIn = alreadyLoggedIn;
    }

    /// <summary>True when the router says another session already holds the single login.</summary>
    public bool AlreadyLoggedIn { get; }
}

/// <summary>
/// A command outside the router's read-only allow-list was about to be sent. The Python code
/// raises the built-in <c>PermissionError</c>; C# has no equivalent, so this is the port's type.
/// </summary>
public sealed class RouterPermissionError : Exception
{
    public RouterPermissionError(string message) : base(message)
    {
    }
}

/// <summary>Sends one reply body to the router. Tests use a fake so nothing hits the network.</summary>
public interface IRouterTransport
{
    /// <summary>
    /// POSTs the JSON body and returns the parsed reply (a string -> object? map), or null/a
    /// non-map value when the reply is unusable.
    /// </summary>
    object? Send(IReadOnlyDictionary<string, object?> body);
}
