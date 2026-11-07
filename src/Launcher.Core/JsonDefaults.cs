using System.Globalization;
using Newtonsoft.Json;

namespace DreamWeave.Launcher.Core
{
    /// <summary>Shared Newtonsoft.Json settings so every reader/writer behaves the same way.</summary>
    internal static class JsonDefaults
    {
        /// <summary>Maximum nesting depth accepted from any JSON input.</summary>
        public const int MaxDepth = 32;

        /// <summary>Settings for launcher-owned files (state). Unknown members are ignored.</summary>
        public static JsonSerializerSettings Create()
        {
            return new JsonSerializerSettings
            {
                Culture = CultureInfo.InvariantCulture,
                DateParseHandling = DateParseHandling.None,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Include,
                MaxDepth = MaxDepth,
                TypeNameHandling = TypeNameHandling.None,
                Formatting = Formatting.Indented,
            };
        }
    }
}
