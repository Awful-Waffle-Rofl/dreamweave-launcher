using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Decal
{
    /// <summary>
    /// The injection APIs stay confined to the isolated x86 helper. The main launcher
    /// (DreamWeaveLauncher.exe) and Launcher.Core.dll must declare none of them, so the launcher cannot
    /// write another process's memory or create a remote thread.
    /// </summary>
    [TestClass]
    public class MainAssemblyNoInjectionTests
    {
        private static readonly HashSet<string> BannedImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VirtualAllocEx",
            "VirtualFreeEx",
            "VirtualProtectEx",
            "WriteProcessMemory",
            "ReadProcessMemory",
            "CreateRemoteThread",
            "CreateRemoteThreadEx",
            "NtCreateThreadEx",
            "RtlCreateUserThread",
            "QueueUserAPC",
            "NtWriteVirtualMemory",
            "NtMapViewOfSection",
            "SetThreadContext",
            "SetWindowsHookEx",
            "SetWindowsHookExA",
            "SetWindowsHookExW",
            "NtQueueApcThread",
            "QueueUserAPC2",
        };

        [TestMethod]
        public void MainLauncherAssembly_DeclaresNoRemoteInjectionImports()
        {
            AssertNoBannedImports(typeof(LauncherPresenter).Assembly);
        }

        [TestMethod]
        public void LauncherCoreAssembly_DeclaresNoRemoteInjectionImports()
        {
            AssertNoBannedImports(typeof(LauncherError).Assembly);
        }

        private static void AssertNoBannedImports(Assembly assembly)
        {
            var hits = new List<string>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    bool isPinvoke = (method.Attributes & MethodAttributes.PinvokeImpl) != 0;
                    string entryPoint = method.Name;
                    DllImportAttribute import = method.GetCustomAttribute<DllImportAttribute>();
                    if (import != null && !string.IsNullOrEmpty(import.EntryPoint))
                    {
                        entryPoint = import.EntryPoint;
                    }

                    if ((isPinvoke || import != null) && (BannedImports.Contains(method.Name) || BannedImports.Contains(entryPoint)))
                    {
                        hits.Add(type.FullName + "." + method.Name + " -> " + entryPoint);
                    }
                }
            }

            Assert.AreEqual(0, hits.Count, assembly.GetName().Name + " declares a remote-injection import: " + string.Join(", ", hits));
        }
    }
}
