using System;
using DreamWeave.Launcher.Core.ClientPrefs;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// Seeds the client's windowed preferences default before a launch; the seam over
    /// <see cref="ClientPreferencesSeeder"/> so the presenter can be tested with a counting fake.
    /// </summary>
    internal interface IClientPreferencesSeeder
    {
        /// <summary>
        /// Writes the windowed default only when the client has not written its own preferences file yet.
        /// Never overwrites one, and never throws.
        /// </summary>
        ClientPreferencesSeedResult Seed();
    }

    /// <summary>
    /// <see cref="IClientPreferencesSeeder"/> over the real <see cref="ClientPreferencesSeeder"/>, targeting
    /// <c>&lt;MyDocuments&gt;\Asheron's Call\UserPreferences.ini</c> as the user (the launcher runs
    /// unelevated, which is exactly where the client reads and writes its own preferences).
    /// </summary>
    internal sealed class ClientPreferencesSeederService : IClientPreferencesSeeder
    {
        private readonly ClientPreferencesSeeder _seeder;

        /// <param name="log">Receives one launcher.log line about the outcome (never credentials).</param>
        public ClientPreferencesSeederService(Action<string> log = null)
        {
            _seeder = new ClientPreferencesSeeder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), null, log);
        }

        public ClientPreferencesSeedResult Seed()
        {
            return _seeder.Seed();
        }
    }
}
