using System;
using System.ComponentModel;
using System.IO;

namespace DreamWeave.DecalLaunch
{
    /// <summary>
    /// DreamWeaveDecalLaunch: an isolated x86 helper that starts acclient.exe with the player's own
    /// Decal loaded, so the main launcher never imports any process-injection API. It is the normal way
    /// Asheron's Call launchers load Decal and is covered by the repo owner's exemption for this launcher.
    ///
    /// Usage: DreamWeaveDecalLaunch.exe &lt;gameFolder&gt; &lt;injectDllPath&gt;
    ///   - argv[0] game folder (must contain acclient.exe)
    ///   - argv[1] full path to the player's Decal Inject.dll
    ///   - stdin   the acclient.exe argument string (contains the password; never on the command line)
    ///   - stdout  "PID=&lt;n&gt;" on success
    ///   - stderr  one stable token per failure (and "inject-failed" when the client started without Decal)
    ///   - exit    0 on success; a distinct non-zero code per failure (see <see cref="DecalLaunchResult"/>)
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args == null || args.Length != 2)
            {
                return Fail(DecalLaunchResult.ExitBadUsage, DecalLaunchResult.TokenBadUsage);
            }

            // The Inject.dll argument must be the one in the folder Decal's own registry entry names.
            int validation = DecalLaunchInputs.Validate(args[0], args[1], DecalAgentRegistry.ReadAgentPath, out DecalLaunchInputs inputs, out string token);
            if (validation != DecalLaunchResult.ExitSuccess)
            {
                return Fail(validation, token);
            }

            // The password arrives on stdin, never on the command line where other processes could read it.
            string acclientArgs = ReadAllStdin().Trim('\r', '\n');

            int pid;
            bool injectionFailed;
            try
            {
                pid = NativeDecalInjector.Launch(inputs, acclientArgs, out injectionFailed);
            }
            catch (Win32Exception ex)
            {
                int exit = DecalLaunchResult.ForCreateProcessError(ex.NativeErrorCode, out string failure);
                return Fail(exit, failure);
            }

            Console.Out.WriteLine(DecalLaunchResult.PidLine(pid));
            Console.Out.Flush();
            if (injectionFailed)
            {
                Console.Error.WriteLine(DecalLaunchResult.TokenInjectFailed);
                Console.Error.Flush();
            }

            return DecalLaunchResult.ExitSuccess;
        }

        private static int Fail(int exitCode, string token)
        {
            try
            {
                Console.Error.WriteLine(token);
                Console.Error.Flush();
            }
            catch (IOException)
            {
                // Nothing reads our stderr: the exit code still carries the failure.
            }

            return exitCode;
        }

        private static string ReadAllStdin()
        {
            try
            {
                using (Stream input = Console.OpenStandardInput())
                using (var reader = new StreamReader(input, System.Text.Encoding.UTF8))
                {
                    return reader.ReadToEnd() ?? string.Empty;
                }
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }
}
