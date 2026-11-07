using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DreamWeave.Launcher.Core.Launch
{
    /// <summary>
    /// Builds the acclient.exe command line from the manifest's argument template.
    /// <para>
    /// SECURITY NOTE: the password is placed on the client's command line, which any other process
    /// running as the same Windows user can read. That exposure is inherent to the Asheron's Call
    /// client (it accepts the password only as <c>-v</c>) and is shared by every launcher for it.
    /// It must be stated in the privacy statement. See Docs/launching.md.
    /// </para>
    /// <para>
    /// The client's command-line parsing is undocumented, so no escaping scheme is attempted.
    /// Instead, any value that could break out of its quotes is rejected with a plain-English
    /// message. This class never logs and never puts a password into an exception message.
    /// </para>
    /// </summary>
    public static class LaunchArgsBuilder
    {
        /// <summary>
        /// Longest user name accepted. ACE closes the session on a login account name over 50
        /// characters (ACE.Server/Network/Handlers/AuthenticationHandler.cs:46) and the account
        /// column is 50 wide (ACE.Database/Models/Auth/AuthDbContext.cs:85).
        /// </summary>
        public const int MaxUserLength = 50;

        /// <summary>
        /// Longest password accepted. ACE has no explicit password length check (it stores a bcrypt
        /// hash, AuthenticationDatabase.cs:59); 64 is this launcher's own conservative cap, kept
        /// under bcrypt's 72-byte input limit for ASCII.
        /// </summary>
        public const int MaxPasswordLength = 64;

        /// <summary>Longest extra-argument string accepted.</summary>
        public const int MaxExtraArgsLength = 512;

        private const string PasswordMask = "********";

        private static readonly Regex Placeholder = new Regex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);

        private static readonly Regex HostLabel = new Regex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$", RegexOptions.CultureInvariant);

        /// <summary>Expands <paramref name="template"/> and appends <paramref name="extraArgs"/>.</summary>
        /// <exception cref="LauncherException">InvalidCredentials when any input is rejected.</exception>
        public static string Build(string template, string user, string pass, string host, int port, string extraArgs)
        {
            ValidateUser(user);
            ValidatePassword(pass);
            return Expand(template, user, pass, host, port, extraArgs);
        }

        /// <summary>
        /// Same validation and layout as <see cref="Build"/> but with the password replaced by a mask,
        /// so the result is safe to write to a log.
        /// </summary>
        public static string BuildRedacted(string template, string user, string pass, string host, int port, string extraArgs)
        {
            ValidateUser(user);
            ValidatePassword(pass);
            return Expand(template, user, PasswordMask, host, port, extraArgs);
        }

        private static string Expand(string template, string user, string pass, string host, int port, string extraArgs)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                throw Invalid("The launch template is empty.");
            }

            ValidateHost(host);
            if (port < 1 || port > 65535)
            {
                throw Invalid("The server port must be between 1 and 65535.");
            }

            ValidateExtraArgs(extraArgs);

            string portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // One pass over the template, so a value that happens to look like a placeholder is never re-expanded.
            string expanded = Placeholder.Replace(template, m =>
            {
                switch (m.Groups[1].Value)
                {
                    case "user": return user;
                    case "pass": return pass;
                    case "host": return host;
                    case "port": return portText;
                    default:
                        throw Invalid("The launch template uses an unknown placeholder '" + m.Value + "'. Allowed: {user}, {pass}, {host}, {port}.");
                }
            });

            var sb = new StringBuilder(expanded.Trim());
            if (!string.IsNullOrWhiteSpace(extraArgs))
            {
                sb.Append(' ').Append(extraArgs.Trim());
            }

            return sb.ToString();
        }

        private static void ValidateUser(string user)
        {
            if (string.IsNullOrEmpty(user))
            {
                throw Invalid("Enter an account name.");
            }

            if (user.Length > MaxUserLength)
            {
                throw Invalid("The account name is too long (the server allows at most " + MaxUserLength + " characters).");
            }

            foreach (char c in user)
            {
                if (char.IsWhiteSpace(c))
                {
                    throw Invalid("The account name cannot contain spaces or other whitespace.");
                }
            }

            CheckCommon(user, "account name");
        }

        private static void ValidatePassword(string pass)
        {
            if (string.IsNullOrEmpty(pass))
            {
                throw Invalid("Enter a password.");
            }

            if (pass.Length > MaxPasswordLength)
            {
                throw Invalid("The password is too long (at most " + MaxPasswordLength + " characters).");
            }

            CheckCommon(pass, "password");
        }

        // Rules shared by the two values that end up inside double quotes.
        private static void CheckCommon(string value, string what)
        {
            foreach (char c in value)
            {
                if (c == '"')
                {
                    throw Invalid("The " + what + " cannot contain a double quote (\") character.");
                }

                if (char.IsControl(c))
                {
                    throw Invalid("The " + what + " cannot contain control characters.");
                }
            }

            // A backslash immediately before the closing quote would escape it under standard Windows parsing.
            if (value[value.Length - 1] == '\\')
            {
                throw Invalid("The " + what + " cannot end with a backslash (\\).");
            }
        }

        private static void ValidateHost(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                throw Invalid("The server host is empty.");
            }

            if (host[0] == '[')
            {
                IPAddress v6;
                if (host[host.Length - 1] == ']' && host.Length > 2 &&
                    IPAddress.TryParse(host.Substring(1, host.Length - 2), out v6) &&
                    v6.AddressFamily == AddressFamily.InterNetworkV6 &&
                    host.IndexOf('%') < 0)
                {
                    return;
                }

                throw Invalid("The server host is not a valid IPv6 address.");
            }

            if (host.Length > 253)
            {
                throw Invalid("The server host name is too long.");
            }

            string[] labels = host.Split('.');
            bool allNumeric = true;
            foreach (string label in labels)
            {
                if (!HostLabel.IsMatch(label))
                {
                    throw Invalid("The server host '" + Safe(host) + "' is not a valid host name or IP address.");
                }

                foreach (char c in label)
                {
                    if (c < '0' || c > '9')
                    {
                        allNumeric = false;
                        break;
                    }
                }
            }

            if (allNumeric)
            {
                // Looks like dotted-decimal: require a proper IPv4 address.
                int octet;
                if (labels.Length != 4)
                {
                    throw Invalid("The server host '" + Safe(host) + "' is not a valid IPv4 address.");
                }

                foreach (string label in labels)
                {
                    if (label.Length > 3 || !int.TryParse(label, out octet) || octet > 255)
                    {
                        throw Invalid("The server host '" + Safe(host) + "' is not a valid IPv4 address.");
                    }
                }
            }
        }

        private static void ValidateExtraArgs(string extraArgs)
        {
            if (string.IsNullOrEmpty(extraArgs))
            {
                return;
            }

            if (extraArgs.Length > MaxExtraArgsLength)
            {
                throw Invalid("The extra launch arguments are too long.");
            }

            foreach (char c in extraArgs)
            {
                if (char.IsControl(c))
                {
                    throw Invalid("The extra launch arguments cannot contain control characters.");
                }
            }
        }

        // Host text is echoed back in messages; it is not secret, but keep the echo short and printable.
        private static string Safe(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text.Length > 64 ? text.Substring(0, 64) : text)
            {
                sb.Append(char.IsControl(c) ? '?' : c);
            }

            return sb.ToString();
        }

        private static LauncherException Invalid(string detail)
        {
            return new LauncherException(LauncherError.InvalidCredentials, detail);
        }
    }
}
