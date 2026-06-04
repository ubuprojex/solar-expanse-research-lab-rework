using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Data.ScriptableObject;
using Game.ObjectInfoDataScripts;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;

namespace ResearchLabsConsumption
{
    /// <summary>
    /// One-shot, opt-in reflection dump (Diagnostics.DumpGameApi). Writes the member
    /// surface of the game's facility / lab / refinery / research types to the BepInEx
    /// log so we can pin down two unknown calls without a decompiler on hand:
    ///   1. how a refinery is gated to "idle" when it has no inputs (the on/off + working
    ///      state on the Facility runtime type), and
    ///   2. how lab research-per-hour is aggregated (the research manager / speed method).
    /// Nothing here changes game state; it only reads type metadata.
    /// </summary>
    internal static class GameApiDump
    {
        // Members worth flagging when scanning broadly. Used to keep the research/refiner
        // scan from drowning the log in unrelated methods.
        private static readonly string[] Keywords =
        {
            "research", "science", "lab", "refin", "bonus", "perhour", "per_hour",
            "speed", "input", "output", "enable", "disabl", "active", "work", "idle",
            "running", "paused", "on", "off", "starv", "produc",
        };

        internal static void Run()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("================ ResearchLabsConsumption GAME API DUMP ================");
                sb.AppendLine("Copy everything between the ==== markers and paste it back.");
                sb.AppendLine();

                // 1. The runtime Facility type (element of ObjectInfoData.ListFacility) and its
                //    base chain — this is where any enable/disable + working-state members live.
                Type facilityType = ResolveFacilityType();
                if (facilityType != null)
                {
                    sb.AppendLine($"-- Facility runtime type chain (from ObjectInfoData.ListFacility): {facilityType.FullName}");
                    foreach (var t in BaseChain(facilityType))
                        DumpType(sb, t, declaredOnly: true, filtered: false);
                }
                else
                {
                    sb.AppendLine("-- Could not resolve Facility runtime type from ObjectInfoData.ListFacility.");
                }
                sb.AppendLine();

                // 2. The two concrete behaviour classes we care about.
                DumpType(sb, typeof(LabFacility), declaredOnly: true, filtered: false);
                DumpType(sb, typeof(RefineryFacility), declaredOnly: true, filtered: false);
                sb.AppendLine();

                // 3. ObjectInfoData members that mention research/lab/refiner — the daily
                //    update calls into something here, and the research total likely lives nearby.
                sb.AppendLine($"-- {typeof(ObjectInfoData).FullName} (research/lab/refiner-related members only)");
                DumpMembers(sb, typeof(ObjectInfoData), filtered: true);
                sb.AppendLine();

                // 4. Broad scan: any game-namespace type whose name points at research/refining,
                //    with only the keyword-matching members shown.
                sb.AppendLine("-- Game-namespace types named *Research* / *Lab* / *Refin* (keyword members only)");
                ScanResearchTypes(sb);
                sb.AppendLine();

                // 5. Build / placement gates — which method actually authorises building a
                //    facility on a body (CanAddFacility isn't it; the build proceeds anyway).
                sb.AppendLine("==== BUILD / PLACEMENT GATES ====");
                DumpBuildGates(sb);
                sb.AppendLine("==== END BUILD / PLACEMENT GATES ====");

                sb.AppendLine("================ END GAME API DUMP ================");

                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameApiDump] failed: {ex}");
            }
        }

        // Name fragments that catch the build/placement/validation surface.
        private static readonly string[] BuildSubs =
        {
            "build", "construct", "canadd", "addfacility", "valid", "place", "canbuild", "where",
        };

        private static void DumpBuildGates(StringBuilder sb)
        {
            sb.AppendLine($"-- {typeof(ObjectInfoData).FullName} (build/add/placement/validation methods)");
            DumpMatching(sb, typeof(ObjectInfoData), BuildSubs, includeBase: false);
            sb.AppendLine();

            sb.AppendLine($"-- {typeof(FacilityBaseDescriptor).FullName} (build/can/valid members, incl. base)");
            DumpMatching(sb, typeof(FacilityBaseDescriptor), BuildSubs, includeBase: true);
            sb.AppendLine();

            // The UI window/button that actually starts a build is the most likely real gate.
            sb.AppendLine("-- Game.UI types named *Facility*/*Build*/*Shop* (build/click/can/valid/add methods)");
            string[] uiSubs = { "build", "click", "can", "valid", "add", "construct", "select", "onbtn", "onbutton" };
            int cap = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t?.Namespace == null || !t.Namespace.StartsWith("Game.UI"))
                        continue;
                    string n = t.Name.ToLowerInvariant();
                    if (!(n.Contains("facilit") || n.Contains("build") || n.Contains("shop")))
                        continue;
                    if (cap++ > 40)
                    {
                        sb.AppendLine("   ...(UI type cap reached)");
                        return;
                    }
                    sb.AppendLine($"   [{t.FullName}]");
                    DumpMatching(sb, t, uiSubs, includeBase: false);
                }
            }
        }

        private static void DumpMatching(StringBuilder sb, Type t, string[] subs, bool includeBase)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            if (!includeBase)
                flags |= BindingFlags.DeclaredOnly;

            bool any = false;
            foreach (var m in Safe(() => t.GetMethods(flags)))
            {
                if (m.IsSpecialName || !NameMatches(m.Name, subs))
                    continue;
                string ps = string.Join(", ", m.GetParameters().Select(x => $"{Pretty(x.ParameterType)} {x.Name}"));
                sb.AppendLine($"   method {Pretty(m.ReturnType)} {m.Name}({ps})");
                any = true;
            }
            foreach (var p in Safe(() => t.GetProperties(flags)))
            {
                if (!NameMatches(p.Name, subs))
                    continue;
                sb.AppendLine($"   prop   {Pretty(p.PropertyType)} {p.Name}");
                any = true;
            }
            if (!any)
                sb.AppendLine("   (no matching members)");
        }

        private static bool NameMatches(string name, string[] subs)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            string n = name.ToLowerInvariant();
            foreach (var s in subs)
                if (n.Contains(s))
                    return true;
            return false;
        }

        private static Type ResolveFacilityType()
        {
            Type owner = typeof(ObjectInfoData);
            Type listType =
                owner.GetProperty("ListFacility", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.PropertyType
                ?? owner.GetField("ListFacility", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType;
            if (listType == null)
                return null;
            if (listType.IsArray)
                return listType.GetElementType();
            if (listType.IsGenericType)
                return listType.GetGenericArguments().FirstOrDefault();
            // Fall back to the IEnumerable<T> it implements.
            var en = listType.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return en?.GetGenericArguments().FirstOrDefault();
        }

        private static IEnumerable<Type> BaseChain(Type t)
        {
            while (t != null && t != typeof(object))
            {
                // Stop at Unity/engine bases — their members are noise.
                if (t.FullName == "UnityEngine.MonoBehaviour" ||
                    t.FullName == "UnityEngine.Behaviour" ||
                    t.FullName == "UnityEngine.Component" ||
                    t.FullName == "UnityEngine.Object")
                    yield break;
                yield return t;
                t = t.BaseType;
            }
        }

        private static void ScanResearchTypes(StringBuilder sb)
        {
            int typeCount = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || t.Namespace == null)
                        continue;
                    if (!(t.Namespace.StartsWith("Game") || t.Namespace.StartsWith("ScriptableObject") || t.Namespace.StartsWith("Data")))
                        continue;
                    string n = t.Name.ToLowerInvariant();
                    if (!(n.Contains("research") || n.Contains("lab") || n.Contains("refin") || n.Contains("science")))
                        continue;
                    // Skip the ones already dumped in full.
                    if (t == typeof(LabFacility) || t == typeof(RefineryFacility))
                        continue;
                    if (typeCount++ > 60)
                    {
                        sb.AppendLine("   ...(type cap reached; ask for more if needed)");
                        return;
                    }
                    DumpMembers(sb, t, filtered: true);
                }
            }
        }

        private static void DumpType(StringBuilder sb, Type t, bool declaredOnly, bool filtered)
        {
            sb.AppendLine($"-- {t.FullName}  (base: {t.BaseType?.FullName})");
            DumpMembers(sb, t, filtered, declaredOnly);
        }

        private static void DumpMembers(StringBuilder sb, Type t, bool filtered, bool declaredOnly = false)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            if (declaredOnly)
                flags |= BindingFlags.DeclaredOnly;

            bool any = false;

            foreach (var f in Safe(() => t.GetFields(flags)))
            {
                string line = $"   field  {Pretty(f.FieldType)} {f.Name}";
                if (!filtered || Matches(f.Name) || Matches(f.FieldType.Name))
                { sb.AppendLine(line); any = true; }
            }
            foreach (var p in Safe(() => t.GetProperties(flags)))
            {
                string acc = $"{(p.CanRead ? "get;" : "")}{(p.CanWrite ? "set;" : "")}";
                string line = $"   prop   {Pretty(p.PropertyType)} {p.Name} {{ {acc} }}";
                if (!filtered || Matches(p.Name) || Matches(p.PropertyType.Name))
                { sb.AppendLine(line); any = true; }
            }
            foreach (var m in Safe(() => t.GetMethods(flags)))
            {
                if (m.IsSpecialName)
                    continue; // skip property/event accessors
                string ps = string.Join(", ", m.GetParameters().Select(x => $"{Pretty(x.ParameterType)} {x.Name}"));
                string line = $"   method {Pretty(m.ReturnType)} {m.Name}({ps})";
                if (!filtered || Matches(m.Name) || Matches(m.ReturnType.Name))
                { sb.AppendLine(line); any = true; }
            }

            if (filtered && !any)
                sb.AppendLine("   (no keyword-matching members)");
        }

        private static bool Matches(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            string n = name.ToLowerInvariant();
            foreach (var k in Keywords)
                if (n.Contains(k))
                    return true;
            return false;
        }

        private static string Pretty(Type t)
        {
            if (t == null)
                return "?";
            if (!t.IsGenericType)
                return t.Name;
            string baseName = t.Name.Contains("`") ? t.Name.Substring(0, t.Name.IndexOf('`')) : t.Name;
            return $"{baseName}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
        }

        private static IEnumerable<T> Safe<T>(Func<T[]> getter)
        {
            T[] arr;
            try { arr = getter(); }
            catch { yield break; }
            foreach (var x in arr)
                yield return x;
        }
    }
}
