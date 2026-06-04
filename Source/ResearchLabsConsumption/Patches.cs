using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Data.ScriptableObject;
using Game.ObjectInfoDataScripts;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;
using Game.UI.Windows.Windows;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;

namespace ResearchLabsConsumption
{
    /// <summary>Per-lab "was it fed last daily tick" flag, used to gate research.</summary>
    internal sealed class LabFedState
    {
        public bool Starved;
    }

    /// <summary>
    /// Shared runtime state. Keyed on the Facility instance via a ConditionalWeakTable so it
    /// follows the facility for its lifetime and is collected automatically when the facility
    /// is scrapped — no manual cleanup, no leaked references.
    /// </summary>
    internal static class LabRuntime
    {
        internal static readonly ConditionalWeakTable<Facility, LabFedState> States =
            new ConditionalWeakTable<Facility, LabFedState>();
    }

    /// <summary>
    /// Reads (and caches) the refinerInput Teddit stores on a facility descriptor. The game only
    /// natively *uses* refinerData on a RefineryFacility, but Teddit fills it in for our labs too,
    /// so this is where both the consumption pass and the tooltip pass get their per-day costs.
    /// Fully reflection-based so nothing here is hard-coded to a facility ID.
    /// </summary>
    internal static class RefinerInputs
    {
        internal sealed class Entry
        {
            public ResourceDefinition Resource;
            public double RatePerDay;
        }

        private static readonly Dictionary<FacilityBaseDescriptor, List<Entry>> Cache =
            new Dictionary<FacilityBaseDescriptor, List<Entry>>();

        private static readonly FieldInfo RefinerField = FindField(typeof(FacilityBaseDescriptor), "refinerData");

        internal static bool Available => RefinerField != null;

        internal static List<Entry> Get(FacilityBaseDescriptor descriptor)
        {
            if (Cache.TryGetValue(descriptor, out var cached))
                return cached;

            var parsed = Parse(descriptor);
            Cache[descriptor] = parsed;
            return parsed;
        }

        private static List<Entry> Parse(FacilityBaseDescriptor descriptor)
        {
            var result = new List<Entry>();
            if (RefinerField == null)
                return result;
            try
            {
                object refinerData = RefinerField.GetValue(descriptor);
                if (refinerData == null)
                    return result;

                Type rdType = refinerData.GetType();
                var fi = rdType.GetField("input", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                      ?? rdType.GetField("Input", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object listObj = fi != null
                    ? fi.GetValue(refinerData)
                    : rdType.GetProperty("Input", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(refinerData, null);

                if (!(listObj is IEnumerable items))
                    return result;

                foreach (object item in items)
                {
                    if (item == null)
                        continue;
                    var resFi = item.GetType().GetField("resource");
                    var rateFi = item.GetType().GetField("ratePerDay");
                    var res = resFi?.GetValue(item) as ResourceDefinition;
                    double rate = rateFi != null ? Convert.ToDouble(rateFi.GetValue(item)) : 0.0;
                    if (res != null && rate > 0.0)
                        result.Add(new Entry { Resource = res, RatePerDay = rate });
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ResearchLabsConsumption] Failed to read refinerInput for {descriptor?.ID}: {ex.Message}");
            }

            return result;
        }

        private static FieldInfo FindField(Type type, string fieldName)
        {
            while (type != null)
            {
                var fi = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (fi != null)
                    return fi;
                type = type.BaseType;
            }
            return null;
        }

        /// <summary>Best-effort human label for a resource: rich icon+name, then plain name, then ID.</summary>
        internal static string Label(ResourceDefinition res)
        {
            if (res == null)
                return "?";
            try
            {
                var s = res.GetType().GetProperty("IconWithLinkString", BindingFlags.Public | BindingFlags.Instance)?.GetValue(res, null) as string;
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(res.Name))
                    return res.Name;
            }
            catch { }
            return res.ID;
        }
    }

    /// <summary>
    /// Postfix on the per-day, per-(body+company) life-support update. For every enabled
    /// LabFacility that carries a refinerInput, charge rate * enabledCount per day — but
    /// only if the body can afford the FULL daily cost of every input (all-or-nothing, the
    /// way a refinery idles when starved). The result is recorded per facility so the
    /// research-gating patch below can drop the bonus on a starved lab.
    ///
    /// Real refineries are skipped (the game already ticks those), so there is no
    /// double-charging.
    /// </summary>
    [HarmonyPatch(typeof(ObjectInfoData), "UpdateLifeSupport")]
    internal static class ObjectInfoDataConsumeLabInputs
    {
        private static void Postfix(ObjectInfoData __instance)
        {
            if (__instance?.ListFacility == null || !RefinerInputs.Available || !Plugin.Enabled.Value)
                return;

            float multiplier = Plugin.RateMultiplier.Value;
            if (multiplier <= 0f)
                return;

            foreach (var facility in __instance.ListFacility)
            {
                var descriptor = facility?.facilityDescriptor;
                if (descriptor == null)
                    continue;

                // Only labs — never touch real refineries (the game ticks those itself).
                if (descriptor.FacilityItemClass != typeof(LabFacility))
                    continue;

                var inputs = RefinerInputs.Get(descriptor);
                if (inputs.Count == 0)
                    continue;

                var state = LabRuntime.States.GetOrCreateValue(facility);

                // Only enabled units consume and research. A disabled lab (Enabled == 0)
                // already contributes no bonus natively, so it should cost nothing and is
                // not considered "starved".
                long active = facility.Enabled;
                if (active <= 0)
                {
                    state.Starved = false;
                    continue;
                }

                // All-or-nothing: the lab runs only if the body can cover every input's full
                // daily cost. Otherwise it idles — no consumption, no research.
                bool affordable = true;
                foreach (var input in inputs)
                {
                    if (input.Resource == null || input.RatePerDay <= 0.0)
                        continue;

                    double needed = input.RatePerDay * active * multiplier;
                    if (__instance.CheckResources(input.Resource) < needed)
                    {
                        affordable = false;
                        if (Plugin.VerboseLogging.Value)
                            Plugin.Log.LogInfo(
                                $"[ResearchLabsConsumption] {descriptor.ID} starved: needs {needed:0.###} {input.Resource.ID}, idling (no research).");
                        break;
                    }
                }

                if (!affordable)
                {
                    state.Starved = true;
                    continue;
                }

                foreach (var input in inputs)
                {
                    if (input.Resource == null || input.RatePerDay <= 0.0)
                        continue;

                    double needed = input.RatePerDay * active * multiplier;
                    __instance.RemoveResource(input.Resource, needed);

                    if (Plugin.VerboseLogging.Value)
                        Plugin.Log.LogInfo(
                            $"[ResearchLabsConsumption] {descriptor.ID} x{active}: -{needed:0.###} {input.Resource.ID}");
                }

                state.Starved = false;
            }
        }
    }

    /// <summary>
    /// Postfix on LabFacility.GetBonusFromLab — the per-lab research contribution. When the
    /// consumption pass above has flagged this lab as starved (couldn't afford its inputs),
    /// zero its bonus so a resource-less lab grants no research, exactly like turning it off.
    /// Labs with no refinerInput are never flagged, so vanilla and the buffed base lab are
    /// unaffected.
    /// </summary>
    [HarmonyPatch(typeof(LabFacility), "GetBonusFromLab")]
    internal static class LabFacilityGateResearch
    {
        private static void Postfix(LabFacility __instance, ref double __result)
        {
            if (!Plugin.Enabled.Value || __instance == null || __result == 0.0)
                return;

            if (LabRuntime.States.TryGetValue(__instance, out var state) && state.Starved)
                __result = 0.0;
        }
    }

    /// <summary>
    /// Postfix on Facility.GetRunningFacilityStats — the rows shown in the in-world facility
    /// mouseover. The build menu already lists a lab's refinerInput (via the Refiner ability),
    /// but the running-facility tooltip is class-gated and skips it for a LabFacility, so the
    /// cost wasn't visible there. We append the per-day consumption rows for any lab that has a
    /// refinerInput, including an "(idle — no inputs)" note when the lab is currently starved.
    /// </summary>
    [HarmonyPatch(typeof(Facility), "GetRunningFacilityStats")]
    internal static class FacilityShowConsumptionInTooltip
    {
        private static void Postfix(Facility __instance, ref List<ValueTuple<string, string>> __result)
        {
            if (__instance == null || !RefinerInputs.Available)
                return;

            var descriptor = __instance.facilityDescriptor;
            if (descriptor == null || descriptor.FacilityItemClass != typeof(LabFacility))
                return;

            var inputs = RefinerInputs.Get(descriptor);
            if (inputs.Count == 0)
                return;

            if (__result == null)
                __result = new List<ValueTuple<string, string>>();

            float multiplier = Plugin.Enabled.Value ? Plugin.RateMultiplier.Value : 0f;
            long active = __instance.Enabled;
            long units = active > 0 ? active : 1;
            bool starved = Plugin.Enabled.Value
                           && LabRuntime.States.TryGetValue(__instance, out var state)
                           && state.Starved;

            foreach (var input in inputs)
            {
                if (input.Resource == null || input.RatePerDay <= 0.0)
                    continue;

                double perDay = multiplier > 0f ? input.RatePerDay * units * multiplier : input.RatePerDay * units;
                string value = $"-{perDay:0.##}/day";
                if (starved)
                    value += " (idle — no inputs)";

                __result.Add(new ValueTuple<string, string>(RefinerInputs.Label(input.Resource), value));
            }
        }
    }

    /// <summary>
    /// Ensures the game's native `noBuildOnAsteroid` flag is set on lab descriptors.
    ///
    /// The Teddit YAML sets this flag for patched (existing) facilities like build_lab, but Teddit's
    /// facility-creation path only applies a fixed allow-list of fields, so the flag never lands on
    /// the new tier-2 labs. We stamp it ourselves on every LabFacility descriptor, just before the
    /// build picker filters its list (ChoseFacilityWindow.FilterByObjectInfo) — that filter is what
    /// reads noBuildOnAsteroid, and it always runs before the player can build. Setting the real
    /// field keeps every system (menu, AI, tooltips) consistent, exactly like the patched base lab.
    ///
    /// Patched manually (not via PatchAll), wrapped in try/catch, so a miss can never abort the
    /// plugin's other patches.
    /// </summary>
    internal static class EnsureLabsNoBuildOnAsteroid
    {
        private static readonly FieldInfo NoBuildField =
            AccessTools.Field(typeof(FacilityBaseDescriptor), "noBuildOnAsteroid");

        private static bool _stamped;

        internal static void TryPatch(Harmony harmony)
        {
            try
            {
                MethodBase target = AccessTools.Method(typeof(ChoseFacilityWindow), "FilterByObjectInfo");
                if (target == null)
                {
                    Plugin.Log.LogWarning("[ResearchLabsConsumption] ChoseFacilityWindow.FilterByObjectInfo not found; tier-2 asteroid restriction disabled.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(EnsureLabsNoBuildOnAsteroid), nameof(StampLabsPrefix)));
                Plugin.Log.LogInfo($"[ResearchLabsConsumption] Asteroid restriction hooked on {target.DeclaringType?.FullName}.{target.Name}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ResearchLabsConsumption] Failed to hook FilterByObjectInfo: {ex.Message}");
            }
        }

        private static void StampLabsPrefix()
        {
            if (_stamped || NoBuildField == null)
                return;

            try
            {
                var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
                var list = allSO?.AllFacility?.List;
                if (list == null)
                    return; // not ready yet; retry next time the picker opens

                int stamped = 0;
                foreach (var descriptor in list)
                {
                    if (descriptor == null || descriptor.FacilityItemClass != typeof(LabFacility))
                        continue;
                    if (!(bool)NoBuildField.GetValue(descriptor))
                    {
                        NoBuildField.SetValue(descriptor, true);
                        stamped++;
                    }
                }

                _stamped = true;
                Plugin.Log.LogInfo($"[ResearchLabsConsumption] Stamped noBuildOnAsteroid on {stamped} lab descriptor(s).");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ResearchLabsConsumption] Failed to stamp noBuildOnAsteroid: {ex.Message}");
            }
        }
    }
}
