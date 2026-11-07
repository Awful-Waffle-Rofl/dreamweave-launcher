using System.Collections.Generic;

namespace DreamWeave.Launcher.Core.Manifests
{
    /// <summary>
    /// The production manifest-signing keys compiled into the launcher.
    /// EMPTY until the owner generates the offline production key with
    /// <c>ManifestTool keygen</c>; with an empty set every manifest is rejected (fail closed).
    /// To add a key, paste the <c>keyId:X:Y</c> line keygen prints:
    /// <code>TrustedKey.Parse("dw-0123456789abcdef:BASE64X:BASE64Y"),</code>
    /// Keep the outgoing key listed during a rotation until every manifest is re-signed.
    /// </summary>
    public static class TrustedKeys
    {
        /// <summary>
        /// Compiled-in rollback floor: no manifest with a lower serial is ever accepted, even on a
        /// fresh install with no state or cache. Raise it in a launcher release to retire old,
        /// still-validly-signed manifests. Never below 1.
        /// </summary>
        public const long MinimumSerial = 1;

        /// <summary>The compiled-in production key set.</summary>
        public static IReadOnlyList<TrustedKey> Production { get; } = new TrustedKey[]
        {
        };
    }
}
