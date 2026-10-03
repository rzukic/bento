using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace Repere.Api.Configuration;

/// <summary>
/// One parsed <c>TRUSTED_PROXIES</c> entry: either a single address or a CIDR network.
/// </summary>
public sealed record TrustedProxyRule(string Source, IPAddress? Address, Microsoft.AspNetCore.HttpOverrides.IPNetwork? Network);

/// <summary>
/// Parses <c>TRUSTED_PROXIES</c> and builds the forwarded-headers options used when the
/// backend sits behind a reverse proxy (TASK-146). When the list is empty the backend
/// must not trust any <c>X-Forwarded-*</c> header, so callers only register the
/// middleware for a non-empty list.
/// </summary>
public static class TrustedProxyConfiguration
{
    public static IReadOnlyList<TrustedProxyRule> Parse(IEnumerable<string> entries)
    {
        var rules = new List<TrustedProxyRule>();
        foreach (var rawEntry in entries)
        {
            var entry = rawEntry?.Trim();
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            rules.Add(ParseEntry(entry));
        }

        return rules;
    }

    public static ForwardedHeadersOptions CreateForwardedHeadersOptions(IEnumerable<string> entries)
    {
        var rules = Parse(entries);
        if (rules.Count == 0)
        {
            throw new InvalidOperationException(
                "Forwarded headers require at least one TRUSTED_PROXIES entry.");
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost
        };

        // ASP.NET trusts loopback by default; replace that with the explicit list only.
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();

        foreach (var rule in rules)
        {
            if (rule.Address is not null)
            {
                options.KnownProxies.Add(rule.Address);
            }
            else if (rule.Network is not null)
            {
                options.KnownNetworks.Add(rule.Network);
            }
        }

        return options;
    }

    private static TrustedProxyRule ParseEntry(string entry)
    {
        var slashIndex = entry.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex < 0)
        {
            if (IPAddress.TryParse(entry, out var address) && IsSupportedFamily(address))
            {
                return new TrustedProxyRule(entry, address, null);
            }

            throw InvalidEntry(entry);
        }

        var prefixText = entry[..slashIndex];
        var lengthText = entry[(slashIndex + 1)..];

        if (!IPAddress.TryParse(prefixText, out var prefix)
            || !IsSupportedFamily(prefix)
            || !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefixLength))
        {
            throw InvalidEntry(entry);
        }

        var maxPrefixLength = prefix.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            throw InvalidEntry(entry);
        }

        return new TrustedProxyRule(entry, null, new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
    }

    private static bool IsSupportedFamily(IPAddress address)
    {
        return address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
    }

    private static InvalidOperationException InvalidEntry(string entry)
    {
        return new InvalidOperationException(
            $"TRUSTED_PROXIES entry '{entry}' is invalid. Use a comma-separated list of IP addresses or CIDR ranges, " +
            "for example 172.18.0.0/16 or 10.0.0.5.");
    }
}
