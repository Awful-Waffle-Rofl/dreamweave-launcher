using System.Collections.Generic;

namespace DreamWeave.Launcher.Core.Credentials
{
    /// <summary>
    /// Remembers account passwords between launcher runs. Implementations must never write a
    /// password to a file the launcher owns.
    /// </summary>
    public interface ICredentialStore
    {
        /// <summary>Saves or replaces the password for <paramref name="account"/>.</summary>
        /// <exception cref="LauncherException">CredentialStoreUnavailable when the store cannot be used.</exception>
        void Save(string account, string password);

        /// <summary>The saved password, or null when none is saved for <paramref name="account"/>.</summary>
        string Get(string account);

        /// <summary>Removes the saved password. Returns false when there was none.</summary>
        bool Delete(string account);

        /// <summary>Names of every account with a saved password, sorted.</summary>
        IReadOnlyList<string> ListAccounts();
    }
}
