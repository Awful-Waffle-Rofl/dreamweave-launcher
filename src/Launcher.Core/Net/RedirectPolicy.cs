using System;

namespace DreamWeave.Launcher.Core.Net
{
    /// <summary>
    /// The launcher's manual redirect rules, shared by every transport under <c>Net/</c>:
    /// at most <see cref="HttpFetcher.MaxRedirects"/> hops, never from https to a non-https URL,
    /// and only http(s) targets. Violations fail as <see cref="LauncherError.SourceHttpError"/>
    /// before the offending location is requested. The single implementation used by both
    /// <see cref="HttpFetcher"/> and <see cref="HttpStreamClient"/>.
    /// </summary>
    internal static class RedirectPolicy
    {
        /// <summary>True for the status codes the launcher treats as redirects.</summary>
        public static bool IsRedirect(int status)
        {
            return status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
        }

        /// <summary>
        /// Validates one redirect hop and returns the absolute next URI.
        /// </summary>
        /// <param name="original">The URI the caller first asked for (named in the too-many-hops message).</param>
        /// <param name="current">The URI that answered with the redirect.</param>
        /// <param name="location">The Location header, or null when absent.</param>
        /// <param name="hopsSoFar">Redirects already followed for this request.</param>
        public static Uri NextHop(Uri original, Uri current, Uri location, int hopsSoFar)
        {
            if (location == null)
            {
                throw new LauncherException(LauncherError.SourceHttpError, current.Host + " sent a redirect without a location.");
            }

            if (hopsSoFar >= HttpFetcher.MaxRedirects)
            {
                throw new LauncherException(LauncherError.SourceHttpError, original.Host + " redirected more than " + HttpFetcher.MaxRedirects + " times.");
            }

            Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
            {
                throw new LauncherException(LauncherError.SourceHttpError, current.Host + " tried to redirect a secure download to an insecure address; refused.");
            }

            if (next.Scheme != Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttp)
            {
                throw new LauncherException(LauncherError.SourceHttpError, current.Host + " redirected to an unsupported address.");
            }

            return next;
        }
    }
}
