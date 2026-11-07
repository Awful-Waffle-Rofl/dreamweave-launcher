using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Credentials;
using DreamWeave.Launcher.Core.Launch;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Telemetry;

namespace DreamWeave.Launcher.App
{
    /// <summary>Starts the game client; the seam over <see cref="GameLauncher"/>.</summary>
    internal interface IGameStarter
    {
        /// <summary>Starts acclient.exe from <paramref name="acFolder"/> with <paramref name="args"/>.</summary>
        /// <exception cref="LauncherException">As <see cref="GameLauncher.Launch"/>.</exception>
        void Start(string acFolder, string args);
    }

    /// <summary><see cref="IGameStarter"/> over the real <see cref="GameLauncher"/>.</summary>
    internal sealed class GameStarter : IGameStarter
    {
        private readonly GameLauncher _launcher = new GameLauncher();

        public void Start(string acFolder, string args)
        {
            _launcher.Launch(acFolder, args);
        }
    }

    /// <summary>The result of <see cref="LauncherPresenter.PlayAsync"/>.</summary>
    internal sealed class PlayResult
    {
        public PlayResult(LauncherError error, string message, bool passwordNotSaved)
        {
            Error = error;
            Message = message;
            PasswordNotSaved = passwordNotSaved;
        }

        /// <summary><see cref="LauncherError.None"/> when the client was started.</summary>
        public LauncherError Error { get; }

        /// <summary>Text to show (the error table message, or the validation rule).</summary>
        public string Message { get; }

        /// <summary>The game started but "Remember password" could not be honoured.</summary>
        public bool PasswordNotSaved { get; }
    }

    /// <summary>
    /// The launcher window's logic: saved accounts, input validation (the <see cref="LaunchArgsBuilder"/>
    /// rules), the update gate, and Play. Everything blocking runs on the thread pool.
    /// </summary>
    internal sealed class LauncherPresenter
    {
        private readonly ICredentialStore _credentials;
        private readonly IGameStarter _starter;
        private readonly ITelemetrySink _telemetry;
        private readonly Func<LauncherManifest> _manifest;
        private readonly Func<string> _acFolder;

        public LauncherPresenter(ICredentialStore credentials, IGameStarter starter, ITelemetrySink telemetry, Func<LauncherManifest> manifest, Func<string> acFolder)
        {
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _starter = starter ?? throw new ArgumentNullException(nameof(starter));
            _telemetry = telemetry ?? NullTelemetrySink.Instance;
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _acFolder = acFolder ?? throw new ArgumentNullException(nameof(acFolder));
        }

        /// <summary>
        /// The account whose SAVED password is currently in the password box, or null when the box
        /// holds nothing from the credential store (empty, or typed by the player).
        /// </summary>
        public string FilledFor { get; private set; }

        /// <summary>Records that the password box was just filled with <paramref name="account"/>'s saved password.</summary>
        public void MarkFilled(string account)
        {
            FilledFor = string.IsNullOrEmpty(account) ? null : account;
        }

        /// <summary>
        /// Call on every change of the account text or selection. Returns true when the password box
        /// holds another account's saved password (the name no longer matches <see cref="FilledFor"/>,
        /// case-insensitively): the caller must then clear the password and untick Remember, so one
        /// account's saved password is never sent for, or saved under, another account.
        /// </summary>
        public bool AccountChanged(string accountText)
        {
            if (FilledFor == null || string.Equals(FilledFor, accountText ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            FilledFor = null;
            return true;
        }

        /// <summary>The current update decision; <see cref="UpdateGate.None"/> until the check has run.</summary>
        public UpdateGate Update { get; set; } = UpdateGate.None;

        /// <summary>True while a start is in progress.</summary>
        public bool IsStarting { get; private set; }

        /// <summary>Whether Play is enabled.</summary>
        public bool CanPlay => !IsStarting && !Update.BlocksPlay && _manifest() != null;

        /// <summary>Server display name.</summary>
        public string ServerName => _manifest()?.Server?.Name ?? string.Empty;

        /// <summary>Server address as host:port.</summary>
        public string ServerAddress
        {
            get
            {
                ServerInfo s = _manifest()?.Server;
                return s == null ? string.Empty : s.Host + ":" + s.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Saved account names (empty when the credential store cannot be read).</summary>
        public Task<IReadOnlyList<string>> LoadAccountsAsync()
        {
            return Task.Run<IReadOnlyList<string>>(() =>
            {
                try
                {
                    return _credentials.ListAccounts();
                }
                catch (LauncherException)
                {
                    return new string[0];
                }
            });
        }

        /// <summary>The saved password for <paramref name="account"/>, or null.</summary>
        public Task<string> GetSavedPasswordAsync(string account)
        {
            return Task.Run(() =>
            {
                try
                {
                    return string.IsNullOrEmpty(account) ? null : _credentials.Get(account);
                }
                catch (LauncherException)
                {
                    return null;
                }
            });
        }

        /// <summary>
        /// The first rule <paramref name="user"/> / <paramref name="password"/> break, in the builder's own
        /// plain words (printable ASCII, no double quote, no control characters, no trailing backslash,
        /// lengths), or null when both are acceptable.
        /// </summary>
        public string Validate(string user, string password)
        {
            LauncherManifest m = _manifest();
            if (m == null)
            {
                return null;
            }

            try
            {
                LaunchArgsBuilder.BuildRedacted(m.Launch?.ArgTemplate ?? LaunchInfo.DefaultArgTemplate, user, password, m.Server?.Host, m.Server?.Port ?? 0, m.Launch?.ExtraArgs);
                return null;
            }
            catch (LauncherException ex) when (ex.Code == LauncherError.InvalidCredentials)
            {
                return ex.Detail;
            }
            catch (LauncherException ex)
            {
                return ErrorMessages.For(ex.Code);
            }
        }

        /// <summary>Validates, starts the client with the game folder as working directory, then remembers or forgets the password.</summary>
        public async Task<PlayResult> PlayAsync(string user, string password, bool remember)
        {
            if (!CanPlay)
            {
                return new PlayResult(LauncherError.Cancelled, Update.BlocksPlay ? Update.BannerText : null, false);
            }

            string rule = Validate(user, password);
            if (rule != null)
            {
                return new PlayResult(LauncherError.InvalidCredentials, rule, false);
            }

            IsStarting = true;
            Stopwatch clock = Stopwatch.StartNew();
            _telemetry.Record(TelemetrySteps.Launch, TelemetryOutcome.Started);
            try
            {
                LauncherManifest m = _manifest();
                string folder = _acFolder();
                bool passwordNotSaved = await Task.Run(() =>
                {
                    string args = LaunchArgsBuilder.Build(m.Launch?.ArgTemplate ?? LaunchInfo.DefaultArgTemplate, user, password, m.Server.Host, m.Server.Port, m.Launch?.ExtraArgs);
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        throw new LauncherException(LauncherError.ClientNotFound, "No game folder.");
                    }

                    _starter.Start(folder, args);
                    return !TryRemember(user, password, remember);
                }).ConfigureAwait(false);

                _telemetry.Record(TelemetrySteps.Launch, TelemetryOutcome.Succeeded, LauncherError.None, clock.Elapsed);
                return new PlayResult(LauncherError.None, UiText.GameStarted, passwordNotSaved);
            }
            catch (LauncherException ex)
            {
                _telemetry.Record(TelemetrySteps.Launch, TelemetryOutcome.Failed, ex.Code, clock.Elapsed);
                return new PlayResult(ex.Code, ErrorMessages.For(ex.Code), false);
            }
            finally
            {
                IsStarting = false;
            }
        }

        private bool TryRemember(string user, string password, bool remember)
        {
            try
            {
                if (remember)
                {
                    _credentials.Save(user, password);
                }
                else
                {
                    _credentials.Delete(user);
                }

                return true;
            }
            catch (LauncherException)
            {
                return false;
            }
        }
    }
}
