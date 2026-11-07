using System;
using System.Collections.Generic;
using System.Net;

namespace DreamWeave.Launcher.Core.Telemetry
{
    /// <summary>
    /// Validates the manifest's <c>telemetry.endpoint</c> before anything is ever sent to it. The
    /// manifest is signed, but telemetry is the one place the launcher sends data OUT, so the
    /// address gets its own checks. An endpoint is accepted only when ALL hold:
    /// <list type="bullet">
    /// <item>it is an absolute <c>https</c> URL on the default port (443);</item>
    /// <item>it has no query string, no fragment and no user info;</item>
    /// <item>its host is not an IP address;</item>
    /// <item>its host shares a registrable domain with the manifest's game-server host
    /// (<c>server.host</c>), or equals a host on the explicit allow list.</item>
    /// </list>
    /// <para>
    /// The registrable domain is an APPROXIMATION without a public-suffix list: the last two DNS
    /// labels, or the last three when the last two form one of the common two-part suffixes in
    /// <see cref="TwoPartSuffixes"/> (for example <c>co.uk</c>). A server host that is an IP
    /// address, or one under a shared-hosting / dynamic-DNS domain (<see cref="SharedHostingDomains"/>, for
    /// example <c>duckdns.org</c>), has no usable registrable domain, so then only the allow list can authorise an
    /// endpoint. Both lists are hand-kept and incomplete; add to them when a case is found.
    /// </para>
    /// </summary>
    public static class TelemetryEndpoint
    {
        private static readonly HashSet<string> TwoPartSuffixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk",
            "com.au", "net.au", "org.au", "co.nz", "org.nz",
            "co.jp", "ne.jp", "or.jp", "com.br", "com.mx",
            "co.za", "co.in", "com.cn", "com.tr", "com.sg",
            "com.ng", "org.ng", "com.ph", "com.my", "com.hk", "com.tw", "com.ar", "com.co", "com.pe",
            "com.ua", "com.vn", "com.pk", "com.eg", "com.sa", "co.il", "co.kr", "co.id", "co.th",
            "org.in", "net.in", "gov.au", "edu.au", "gov.in", "ac.nz", "net.nz", "com.pl", "com.ru",
        };

        /// <summary>
        /// Shared-hosting and dynamic-DNS domains: anyone can register a name under them, so "same
        /// registrable domain" proves nothing. A server or endpoint host under one of these never
        /// matches by domain; only the explicit allow list can authorise it.
        /// </summary>
        private static readonly HashSet<string> SharedHostingDomains = new HashSet<string>(StringComparer.Ordinal)
        {
            "duckdns.org", "no-ip.org", "no-ip.com", "no-ip.biz", "ddns.net", "dyndns.org", "dyndns.com", "hopto.org",
            "zapto.org", "myftp.org", "servegame.com", "freeddns.org", "github.io", "githubusercontent.com", "herokuapp.com",
            "azurewebsites.net", "pages.dev", "workers.dev", "netlify.app", "vercel.app", "onrender.com", "fly.dev",
            "glitch.me", "web.app", "firebaseapp.com", "blogspot.com", "appspot.com", "cloudfront.net", "amazonaws.com",
            "trycloudflare.com", "ngrok.io", "ngrok-free.app", "000webhostapp.com",
        };

        /// <summary>
        /// Returns the validated endpoint, or null when it must not be used.
        /// </summary>
        /// <param name="endpoint">The manifest value (may be null: telemetry disabled).</param>
        /// <param name="serverHost">The manifest's <c>server.host</c>.</param>
        /// <param name="allowedHosts">Extra exact hosts that are trusted regardless of domain (may be null).</param>
        public static Uri Validate(string endpoint, string serverHost, IEnumerable<string> allowedHosts)
        {
            return Validate(endpoint, serverHost, allowedHosts, false);
        }

        /// <summary>As the public overload; tests may additionally allow plain http on a loopback address.</summary>
        internal static Uri Validate(string endpoint, string serverHost, IEnumerable<string> allowedHosts, bool allowLoopbackHttp)
        {
            if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return null;
            }

            string host = uri.IdnHost.ToLowerInvariant();
            if (host.EndsWith(".", StringComparison.Ordinal))
            {
                // A fully-qualified trailing dot is a different spelling of the same name; refuse rather than normalise.
                return null;
            }

            if (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(host, out IPAddress ip) && IPAddress.IsLoopback(ip))
            {
                return uri;
            }

            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || IPAddress.TryParse(host.Trim('[', ']'), out _))
            {
                return null;
            }

            if (allowedHosts != null)
            {
                foreach (string allowed in allowedHosts)
                {
                    if (!string.IsNullOrWhiteSpace(allowed) && string.Equals(allowed.Trim(), host, StringComparison.OrdinalIgnoreCase))
                    {
                        return uri;
                    }
                }
            }

            string serverDomain = RegistrableDomain(serverHost);
            return serverDomain != null && serverDomain == RegistrableDomain(host) ? uri : null;
        }

        /// <summary>The approximate registrable domain of <paramref name="host"/>, or null for an IP address, a single label, or nothing usable.</summary>
        internal static string RegistrableDomain(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }

            string h = host.Trim();
            if (Uri.TryCreate("https://" + h, UriKind.Absolute, out Uri u))
            {
                h = u.IdnHost;
            }

            h = h.Trim('.').ToLowerInvariant();
            if (IPAddress.TryParse(h.Trim('[', ']'), out _))
            {
                return null;
            }

            string[] labels = h.Split('.');
            if (labels.Length < 2)
            {
                return null;
            }

            int take = 2;
            if (labels.Length >= 3 && TwoPartSuffixes.Contains(labels[labels.Length - 2] + "." + labels[labels.Length - 1]))
            {
                take = 3;
            }

            string domain = string.Join(".", labels, labels.Length - take, take);
            return SharedHostingDomains.Contains(domain) ? null : domain;
        }
    }
}
