namespace Kobblestone.Operator.Constants;

/// <summary>
/// Annotations used to request a change of a `Server` from outside its controller. The `ServerController` (the only
/// writer of the server's status) records the request in the status and removes the annotation afterwards.
/// </summary>
public static class ServerAnnotations
{
    /// <summary>
    /// Requests the server to wake up from hibernation. Set by the router when a player connects.
    /// </summary>
    public const string WakeUp = "kobblestone.io/wake-up";

    /// <summary>
    /// Requests the server to hibernate. Set by the auto-sleep webhook after player inactivity.
    /// </summary>
    public const string Hibernate = "kobblestone.io/hibernate";
}