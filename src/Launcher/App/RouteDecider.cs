using System.Collections.Generic;
using System.Linq;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.App
{
    /// <summary>Where a normal start goes once the manifest is loaded and the components are evaluated.</summary>
    internal enum StartRoute
    {
        /// <summary>Every required component is satisfied: log in and play.</summary>
        Launcher = 0,

        /// <summary>Something required is missing or differs: run the setup wizard.</summary>
        Wizard = 1,

        /// <summary>No manifest at all (no source and no cache): show the error with Retry.</summary>
        NoManifest = 2,
    }

    /// <summary>Pure routing decision for a normal start.</summary>
    internal static class RouteDecider
    {
        /// <summary>
        /// <see cref="StartRoute.Launcher"/> when every REQUIRED component of <paramref name="manifest"/>
        /// has a <see cref="ComponentState.Satisfied"/> status in <paramref name="statuses"/>, otherwise
        /// <see cref="StartRoute.Wizard"/> (a required component with no status counts as not satisfied).
        /// Optional components never affect the route.
        /// </summary>
        public static StartRoute Decide(LauncherManifest manifest, IReadOnlyList<ComponentStatus> statuses)
        {
            if (manifest == null)
            {
                return StartRoute.NoManifest;
            }

            var byId = new Dictionary<string, ComponentStatus>();
            foreach (ComponentStatus s in statuses ?? new ComponentStatus[0])
            {
                if (s?.Id != null)
                {
                    byId[s.Id] = s;
                }
            }

            bool allRequiredSatisfied = manifest.Components
                .Where(c => c.Required)
                .All(c => byId.TryGetValue(c.Id, out ComponentStatus s) && s.State == ComponentState.Satisfied);
            return allRequiredSatisfied ? StartRoute.Launcher : StartRoute.Wizard;
        }
    }
}
