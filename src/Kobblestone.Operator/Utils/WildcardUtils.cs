namespace Kobblestone.Operator.Utils;

public static class WildcardUtils
{
    public static bool IsAllowedHost(IEnumerable<string> allowedHosts, string host)
        => allowedHosts.Any(h => h is "*" || host.EndsWith(h.Split("*").Last()));
}