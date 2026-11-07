using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace DreamWeave.Launcher.Core.Update
{
    /// <summary>
    /// A Semantic Versioning 2.0.0 version (<c>MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]</c>) with
    /// semver precedence: core numbers compare numerically; a version with a pre-release tag is
    /// LOWER than the same core without one; pre-release identifiers compare left to right
    /// (numeric identifiers numerically, alphanumeric ones in ordinal ASCII order, numeric below
    /// alphanumeric, and a shorter list below a longer one it prefixes); build metadata is
    /// ignored for precedence and equality. A single leading <c>v</c> is accepted.
    /// </summary>
    public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
    {
        private readonly string[] _prerelease;

        private SemVersion(int major, int minor, int patch, string[] prerelease, string build)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            _prerelease = prerelease;
            Build = build;
        }

        /// <summary>Major version.</summary>
        public int Major { get; }

        /// <summary>Minor version.</summary>
        public int Minor { get; }

        /// <summary>Patch version.</summary>
        public int Patch { get; }

        /// <summary>Pre-release identifiers (empty for a release).</summary>
        public IReadOnlyList<string> Prerelease => _prerelease;

        /// <summary>Build metadata after <c>+</c>, or null. Ignored for comparison.</summary>
        public string Build { get; }

        /// <summary>True when the version carries a pre-release tag.</summary>
        public bool IsPrerelease => _prerelease.Length > 0;

        /// <summary>Parses <paramref name="text"/>.</summary>
        /// <exception cref="LauncherException"><see cref="LauncherError.VersionInvalid"/> when it is not a semantic version.</exception>
        public static SemVersion Parse(string text, string what)
        {
            SemVersion v;
            if (!TryParse(text, out v))
            {
                throw new LauncherException(LauncherError.VersionInvalid, "The " + (what ?? "version") + " '" + text + "' is not a valid version number.");
            }

            return v;
        }

        /// <summary>Parses <paramref name="text"/>; false when it is not a semantic version.</summary>
        public static bool TryParse(string text, out SemVersion version)
        {
            version = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string s = text;
            if (s[0] == 'v' || s[0] == 'V')
            {
                s = s.Substring(1);
            }

            string build = null;
            int plus = s.IndexOf('+');
            if (plus >= 0)
            {
                build = s.Substring(plus + 1);
                s = s.Substring(0, plus);
                if (!AllIdentifiersValid(build, false))
                {
                    return false;
                }
            }

            string[] prerelease = new string[0];
            int dash = s.IndexOf('-');
            if (dash >= 0)
            {
                string pre = s.Substring(dash + 1);
                s = s.Substring(0, dash);
                if (!AllIdentifiersValid(pre, true))
                {
                    return false;
                }

                prerelease = pre.Split('.');
            }

            string[] core = s.Split('.');
            if (core.Length != 3)
            {
                return false;
            }

            int major, minor, patch;
            if (!TryCore(core[0], out major) || !TryCore(core[1], out minor) || !TryCore(core[2], out patch))
            {
                return false;
            }

            version = new SemVersion(major, minor, patch, prerelease, build);
            return true;
        }

        /// <summary>
        /// The version of a running assembly: its <see cref="AssemblyInformationalVersionAttribute"/>
        /// when that parses (the SDK may append <c>+commit</c> build metadata, which is ignored), otherwise
        /// the first three parts of its assembly version.
        /// </summary>
        public static SemVersion FromAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            SemVersion v;
            if (info != null && TryParse(info.InformationalVersion, out v))
            {
                return v;
            }

            Version av = assembly.GetName().Version ?? new Version(0, 0, 0);
            return new SemVersion(av.Major, av.Minor, Math.Max(av.Build, 0), new string[0], null);
        }

        /// <inheritdoc />
        public int CompareTo(SemVersion other)
        {
            if (other == null)
            {
                return 1;
            }

            int c = Major.CompareTo(other.Major);
            if (c != 0)
            {
                return c;
            }

            c = Minor.CompareTo(other.Minor);
            if (c != 0)
            {
                return c;
            }

            c = Patch.CompareTo(other.Patch);
            if (c != 0)
            {
                return c;
            }

            // A release ranks above any pre-release of the same core.
            if (_prerelease.Length == 0 && other._prerelease.Length == 0)
            {
                return 0;
            }

            if (_prerelease.Length == 0)
            {
                return 1;
            }

            if (other._prerelease.Length == 0)
            {
                return -1;
            }

            int n = Math.Min(_prerelease.Length, other._prerelease.Length);
            for (int i = 0; i < n; i++)
            {
                c = CompareIdentifier(_prerelease[i], other._prerelease[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return _prerelease.Length.CompareTo(other._prerelease.Length);
        }

        /// <inheritdoc />
        public bool Equals(SemVersion other) => other != null && CompareTo(other) == 0;

        /// <inheritdoc />
        public override bool Equals(object obj) => Equals(obj as SemVersion);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            int h = (Major * 397) ^ (Minor * 31) ^ Patch;
            foreach (string id in _prerelease)
            {
                h = (h * 31) ^ StringComparer.Ordinal.GetHashCode(id);
            }

            return h;
        }

        /// <summary>The canonical text, without build metadata.</summary>
        public override string ToString()
        {
            string core = Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture) + "." + Patch.ToString(CultureInfo.InvariantCulture);
            return _prerelease.Length == 0 ? core : core + "-" + string.Join(".", _prerelease);
        }

        /// <summary>Semver precedence comparison.</summary>
        public static bool operator <(SemVersion a, SemVersion b) => Compare(a, b) < 0;

        /// <summary>Semver precedence comparison.</summary>
        public static bool operator >(SemVersion a, SemVersion b) => Compare(a, b) > 0;

        /// <summary>Semver precedence comparison.</summary>
        public static bool operator <=(SemVersion a, SemVersion b) => Compare(a, b) <= 0;

        /// <summary>Semver precedence comparison.</summary>
        public static bool operator >=(SemVersion a, SemVersion b) => Compare(a, b) >= 0;

        private static int Compare(SemVersion a, SemVersion b)
        {
            if (ReferenceEquals(a, b))
            {
                return 0;
            }

            return a == null ? -1 : a.CompareTo(b);
        }

        private static int CompareIdentifier(string a, string b)
        {
            bool an = IsNumeric(a);
            bool bn = IsNumeric(b);
            if (an && bn)
            {
                // No leading zeros (validated), so a longer digit string is a larger number.
                int byLength = a.Length.CompareTo(b.Length);
                return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
            }

            if (an)
            {
                return -1;
            }

            if (bn)
            {
                return 1;
            }

            int c = string.CompareOrdinal(a, b);
            return c < 0 ? -1 : (c > 0 ? 1 : 0);
        }

        private static bool TryCore(string part, out int value)
        {
            value = 0;
            if (!IsNumeric(part) || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool AllIdentifiersValid(string dotted, bool rejectLeadingZeroNumbers)
        {
            if (dotted.Length == 0)
            {
                return false;
            }

            foreach (string id in dotted.Split('.'))
            {
                if (id.Length == 0)
                {
                    return false;
                }

                foreach (char c in id)
                {
                    bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-';
                    if (!ok)
                    {
                        return false;
                    }
                }

                if (rejectLeadingZeroNumbers && IsNumeric(id) && id.Length > 1 && id[0] == '0')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsNumeric(string s)
        {
            if (s.Length == 0)
            {
                return false;
            }

            foreach (char c in s)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
