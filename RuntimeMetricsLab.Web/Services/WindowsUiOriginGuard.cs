using System.Net;

namespace RuntimeMetricsLab.Web.Services;

public static class WindowsUiOriginGuard
{
    public static bool Allows(string host, string scheme, string? authority, string? origin, string? fetchSite)
    {
        if (string.IsNullOrWhiteSpace(authority)) return false;
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !(IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)))
            return false;
        if (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var supplied) &&
            Uri.TryCreate($"{scheme}://{authority}", UriKind.Absolute, out var actual) &&
            string.Equals(supplied.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(supplied.Host, actual.Host, StringComparison.OrdinalIgnoreCase) && supplied.Port == actual.Port;
    }
}
