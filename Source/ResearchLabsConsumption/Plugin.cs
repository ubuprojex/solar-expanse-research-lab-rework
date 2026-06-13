using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.IO;
using System.Reflection;

namespace ResearchLabsConsumption
{
    /// <summary>
    /// Runtime support for the research_labs_mod Teddit labs. The companion YAML defines the labs;
    /// this plugin supplies the behaviour the game's data model can't express for a LabFacility:
    ///   - charges each lab's refinerInput from the body's stockpile once per day (all-or-nothing),
    ///   - zeroes a lab's research bonus on any day it can't afford its inputs (starvation),
    ///   - shows the daily cost in the in-world facility mouseover, and
    ///   - stamps the native noBuildOnAsteroid flag on lab descriptors Teddit created.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.solarexpanse.researchlabsconsumption";
        public const string PluginName = "Research Labs Consumption";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> RateMultiplier;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<bool> DumpGameApi;
        private static ConfigFile PluginConfig;

        private void Awake()
        {
            Log = Logger;

            // Keep the config next to this DLL, in whatever folder BepInEx loaded the plugin
            // from. Hardcoding a "ResearchLabsConsumption" subfolder under PluginPath spawned a
            // second, near-empty folder at runtime whenever the DLL lived elsewhere (e.g. the
            // shipped "Research Labs Rework" folder) — the "hanging folder" this avoids.
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string pluginConfigPath = Path.Combine(pluginDir, "ResearchLabsConsumption.cfg");
            PluginConfig = new ConfigFile(pluginConfigPath, saveOnInit: true);

            Enabled = PluginConfig.Bind(
                "General", "Enabled", true,
                "Master switch. When false, the labs still research but cost no resources.");
            RateMultiplier = PluginConfig.Bind(
                "General", "RateMultiplier", 1.0f,
                "Multiplier applied to every lab's daily input rate. 2 = double cost, 0.5 = half.");
            VerboseLogging = PluginConfig.Bind(
                "General", "VerboseLogging", false,
                "Log every daily deduction. Useful for debugging, very noisy otherwise.");
            DumpGameApi = PluginConfig.Bind(
                "Diagnostics", "DumpGameApi", false,
                "One-shot diagnostic. When true, dumps the members of the game's Facility / " +
                "Lab / Refinery / research types to the BepInEx log at startup, then does nothing " +
                "else. Used to discover the API for starvation-gating research. Set back to false " +
                "after copying the log.");

            PluginConfig.Save();

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll();
            EnsureLabsNoBuildOnAsteroid.TryPatch(harmony);
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded. Config: {pluginConfigPath}");
            foreach (var patched in harmony.GetPatchedMethods())
                Log.LogInfo($"  patched: {patched.DeclaringType?.FullName}.{patched.Name}");

            if (DumpGameApi.Value)
                GameApiDump.Run();
        }
    }
}
