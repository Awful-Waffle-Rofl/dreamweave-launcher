using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Credentials;
using DreamWeave.Launcher.Core.Launch;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.State;
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

    /// <summary>Where the per-slot account names are kept (state.json in the real app).</summary>
    internal interface ISlotAccountStore
    {
        /// <summary>The saved account name per slot, in slot order (may be shorter than the slot count).</summary>
        IReadOnlyList<string> Load();

        /// <summary>Saves the account name per slot, in slot order.</summary>
        void Save(IReadOnlyList<string> accounts);
    }

    /// <summary><see cref="ISlotAccountStore"/> over <see cref="StateStore"/>. Blocking: call off the UI thread.</summary>
    internal sealed class StateSlotAccounts : ISlotAccountStore
    {
        private readonly StateStore _state;

        public StateSlotAccounts(StateStore state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public IReadOnlyList<string> Load()
        {
            return _state.Read(s => (IReadOnlyList<string>)new List<string>(s.SlotAccounts ?? new List<string>()));
        }

        public void Save(IReadOnlyList<string> accounts)
        {
            var copy = new List<string>(accounts);
            _state.Update(s => s.SlotAccounts = copy);
        }
    }

    /// <summary>What the player typed into one launcher row.</summary>
    internal sealed class SlotInput
    {
        public SlotInput(string account, string password)
        {
            Account = account ?? string.Empty;
            Password = password ?? string.Empty;
        }

        public string Account { get; }

        public string Password { get; }

        /// <summary>True when both the account and the password are filled.</summary>
        public bool IsFilled => Account.Length > 0 && Password.Length > 0;
    }

    /// <summary>What to show in one row when the launcher opens.</summary>
    internal sealed class SlotFill
    {
        public SlotFill(string account, string savedPassword)
        {
            Account = account ?? string.Empty;
            SavedPassword = savedPassword;
        }

        public string Account { get; }

        /// <summary>The password from the credential store, or null.</summary>
        public string SavedPassword { get; }
    }

    /// <summary>The outcome of one row in <see cref="LauncherPresenter.PlayAllAsync"/> / <see cref="LauncherPresenter.PlaySlotAsync"/>.</summary>
    internal sealed class SlotResult
    {
        public SlotResult(int slot, PlayResult result)
        {
            Slot = slot;
            Result = result;
        }

        public int Slot { get; }

        public PlayResult Result { get; }
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
        private readonly ISlotAccountStore _slots;
        private readonly string[] _filled = new string[SlotCount];
        private bool _starting;
        private bool _batch;

        /// <summary>Number of client rows on the launcher screen (Main, Alt 1, Alt 2).</summary>
        public const int SlotCount = 3;

        /// <summary>Pause between two client starts in Play all, so the clients do not race on startup.</summary>
        public static readonly TimeSpan LaunchGap = TimeSpan.FromSeconds(3);

        public LauncherPresenter(ICredentialStore credentials, IGameStarter starter, ITelemetrySink telemetry, Func<LauncherManifest> manifest, Func<string> acFolder, ISlotAccountStore slots = null)
        {
            _slots = slots;
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
        public string FilledFor => _filled[0];

        /// <summary>Records that the password box was just filled with <paramref name="account"/>'s saved password.</summary>
        public void MarkFilled(string account)
        {
            MarkFilled(0, account);
        }

        /// <summary>As <see cref="MarkFilled(string)"/>, for the row <paramref name="slot"/>.</summary>
        public void MarkFilled(int slot, string account)
        {
            _filled[slot] = string.IsNullOrEmpty(account) ? null : account;
        }

        /// <summary>The account whose saved password is in row <paramref name="slot"/>'s password box, or null.</summary>
        public string FilledForSlot(int slot)
        {
            return _filled[slot];
        }

        /// <summary>
        /// Call on every change of the account text or selection. Returns true when the password box
        /// holds another account's saved password (the name no longer matches <see cref="FilledFor"/>,
        /// case-insensitively): the caller must then clear the password and untick Remember, so one
        /// account's saved password is never sent for, or saved under, another account.
        /// </summary>
        public bool AccountChanged(string accountText)
        {
            return AccountChanged(0, accountText);
        }

        /// <summary>As <see cref="AccountChanged(string)"/>, for the row <paramref name="slot"/>.</summary>
        public bool AccountChanged(int slot, string accountText)
        {
            if (_filled[slot] == null || string.Equals(_filled[slot], accountText ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _filled[slot] = null;
            return true;
        }

        /// <summary>The current update decision; <see cref="UpdateGate.None"/> until the check has run.</summary>
        public UpdateGate Update { get; set; } = UpdateGate.None;

        /// <summary>True while a start is in progress.</summary>
        public bool IsStarting => _starting || _batch;

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
        public Task<PlayResult> PlayAsync(string user, string password, bool remember)
        {
            if (!CanPlay)
            {
                return Task.FromResult(Blocked());
            }

            return StartCoreAsync(user, password, remember);
        }

        private PlayResult Blocked()
        {
            return new PlayResult(LauncherError.Cancelled, Update.BlocksPlay ? Update.BannerText : null, false);
        }

        private async Task<PlayResult> StartCoreAsync(string user, string password, bool remember)
        {
            string rule = Validate(user, password);
            if (rule != null)
            {
                return new PlayResult(LauncherError.InvalidCredentials, rule, false);
            }

            _starting = true;
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
                _starting = false;
            }
        }

        // ---- Rows (Main, Alt 1, Alt 2) ----

        /// <summary>
        /// The saved account name for each row, with its saved password when the credential store has one.
        /// A state file from before multi-client support has no row names: the first saved account then
        /// goes into Main, as the single account box used to select it.
        /// </summary>
        public async Task<IReadOnlyList<SlotFill>> LoadSlotsAsync()
        {
            return await Task.Run<IReadOnlyList<SlotFill>>(() =>
            {
                var names = new string[SlotCount];
                try
                {
                    IReadOnlyList<string> saved = _slots?.Load();
                    for (int i = 0; saved != null && i < SlotCount && i < saved.Count; i++)
                    {
                        names[i] = saved[i];
                    }
                }
                catch (LauncherException)
                {
                    // Names are a convenience: start with empty rows.
                }

                if (names.All(string.IsNullOrEmpty))
                {
                    try
                    {
                        IReadOnlyList<string> accounts = _credentials.ListAccounts();
                        if (accounts.Count > 0)
                        {
                            names[0] = accounts[0];
                        }
                    }
                    catch (LauncherException)
                    {
                    }
                }

                var fills = new List<SlotFill>();
                for (int i = 0; i < SlotCount; i++)
                {
                    string password = null;
                    if (!string.IsNullOrEmpty(names[i]))
                    {
                        try
                        {
                            password = _credentials.Get(names[i]);
                        }
                        catch (LauncherException)
                        {
                        }

                        if (password != null)
                        {
                            _filled[i] = names[i];
                        }
                    }

                    fills.Add(new SlotFill(names[i], password));
                }

                return fills;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// The inline error for each row (null: none). A row is checked by <see cref="Validate"/> once both
        /// its boxes are filled; a row whose account repeats an earlier row's (case-insensitively) is an
        /// error whatever its password says.
        /// </summary>
        public string[] ValidateSlots(IReadOnlyList<SlotInput> inputs)
        {
            var errors = new string[SlotCount];
            for (int i = 0; i < SlotCount && i < inputs.Count; i++)
            {
                SlotInput row = inputs[i];
                if (row.Account.Length > 0)
                {
                    for (int j = 0; j < i; j++)
                    {
                        if (string.Equals(inputs[j].Account, row.Account, StringComparison.OrdinalIgnoreCase))
                        {
                            errors[i] = UiText.DuplicateAccount;
                            break;
                        }
                    }
                }

                if (errors[i] == null && row.IsFilled)
                {
                    errors[i] = Validate(row.Account, row.Password);
                }
            }

            return errors;
        }

        /// <summary>Starts just row <paramref name="slot"/>, then remembers the row account names.</summary>
        public async Task<SlotResult> PlaySlotAsync(int slot, IReadOnlyList<SlotInput> inputs, bool remember)
        {
            if (!CanPlay)
            {
                return new SlotResult(slot, Blocked());
            }

            string duplicate = ValidateSlots(inputs)[slot];
            if (duplicate == UiText.DuplicateAccount)
            {
                return new SlotResult(slot, new PlayResult(LauncherError.InvalidCredentials, duplicate, false));
            }

            PlayResult result = await StartCoreAsync(inputs[slot].Account, inputs[slot].Password, remember).ConfigureAwait(false);
            await SaveSlotsAsync(inputs).ConfigureAwait(false);
            return new SlotResult(slot, result);
        }

        /// <summary>
        /// Starts every row whose account and password are both filled, in order, pausing
        /// <paramref name="gap"/> (default <see cref="LaunchGap"/>) after each client that started. A row
        /// that is a duplicate or fails reports its own error and the rest still start. Each finished row
        /// is also reported through <paramref name="progress"/>. Empty rows are skipped without a result.
        /// </summary>
        public async Task<IReadOnlyList<SlotResult>> PlayAllAsync(IReadOnlyList<SlotInput> inputs, bool remember, IProgress<SlotResult> progress = null, TimeSpan? gap = null, Func<TimeSpan, Task> delay = null)
        {
            var results = new List<SlotResult>();
            if (!CanPlay)
            {
                var blocked = new SlotResult(0, Blocked());
                progress?.Report(blocked);
                results.Add(blocked);
                return results;
            }

            TimeSpan pause = gap ?? LaunchGap;
            Func<TimeSpan, Task> wait = delay ?? (t => Task.Delay(t));
            string[] errors = ValidateSlots(inputs);
            _batch = true;
            try
            {
                bool startedOne = false;
                for (int i = 0; i < SlotCount && i < inputs.Count; i++)
                {
                    if (!inputs[i].IsFilled)
                    {
                        continue;
                    }

                    PlayResult result;
                    if (errors[i] != null)
                    {
                        result = new PlayResult(LauncherError.InvalidCredentials, errors[i], false);
                    }
                    else
                    {
                        if (startedOne && pause > TimeSpan.Zero)
                        {
                            await wait(pause).ConfigureAwait(false);
                        }

                        result = await StartCoreAsync(inputs[i].Account, inputs[i].Password, remember).ConfigureAwait(false);
                        startedOne |= result.Error == LauncherError.None;
                    }

                    var row = new SlotResult(i, result);
                    results.Add(row);
                    progress?.Report(row);
                }

                await SaveSlotsAsync(inputs).ConfigureAwait(false);
            }
            finally
            {
                _batch = false;
            }

            return results;
        }

        private Task SaveSlotsAsync(IReadOnlyList<SlotInput> inputs)
        {
            if (_slots == null)
            {
                return Task.FromResult(0);
            }

            var names = new List<string>();
            for (int i = 0; i < SlotCount; i++)
            {
                names.Add(i < inputs.Count ? inputs[i].Account : string.Empty);
            }

            return Task.Run(() =>
            {
                try
                {
                    _slots.Save(names);
                }
                catch (LauncherException)
                {
                    // Only the row names are lost; the game already started.
                }
            });
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
