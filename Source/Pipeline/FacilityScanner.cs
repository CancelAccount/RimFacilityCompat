using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施扫描器：
    /// 1. 独立收集所有 CompProperties_Facility；
    /// 2. 收集所有 CompProperties_AffectedByFacilities 及其已有链接；
    /// 3. 已知建筑族按继承关系归类，普通 Building 按已有关系拆分，未引用设施单独列出。
    /// </summary>
    public static class FacilityScanner
    {
        private const string UnassignedCategory = "FacilityCompat.Unassigned";

        private sealed class KnownCategory
        {
            public readonly string typeName;
            public readonly string translationKey;

            public KnownCategory(string typeName, string translationKey)
            {
                this.typeName = typeName;
                this.translationKey = translationKey;
            }
        }

        /// <summary>
        /// 顺序从具体类型到宽泛类型，避免 ResearchBench/CryptoBed 等先被父类类别截获。
        /// </summary>
        private static readonly List<KnownCategory> KnownCategories = new()
        {
            new KnownCategory("Building_CryptoBed", "FC.CatCryptoBed"),
            new KnownCategory("Building_ResearchBench", "FC.CatResearchBench"),
            new KnownCategory("Building_GeneAssembler", "FC.CatGeneAssembler"),
            new KnownCategory("Building_GravEngine", "FC.CatGravEngine"),
            new KnownCategory("Building_HoldingPlatform", "FC.CatHoldingPlatform"),
            new KnownCategory("Building_Bed", "FC.CatBed"),
            new KnownCategory("Building_WorkTable", "FC.CatWorkTable"),
        };

        /// <summary>DLC / Core 的 modContentPack.Name → 翻译 key 映射</summary>
        private static readonly Dictionary<string, string> OfficialModKeys = new()
        {
            ["Core"] = "FC.SourceCore",
            ["Royalty"] = "FC.SourceRoyalty",
            ["Ideology"] = "FC.SourceIdeology",
            ["Biotech"] = "FC.SourceBiotech",
            ["Anomaly"] = "FC.SourceAnomaly",
            ["Odyssey"] = "FC.SourceOdyssey",
        };

        /// <summary>
        /// 扫描所有类别，返回 类别标识 → CategoryInfo。
        /// 已知类别继续使用原显示名作为标识，以兼容现有设置；动态普通建筑组使用稳定前缀。
        /// </summary>
        public static Dictionary<string, CategoryInfo> ScanAll()
        {
            var result = new Dictionary<string, CategoryInfo>();
            var allDefs = DefDatabase<ThingDef>.AllDefs
                .Where(def => def?.thingClass != null)
                .ToList();

            var facilityDefs = allDefs
                .Where(HasFacilityComp)
                .ToList();

            var affectedDefs = allDefs
                .Where(HasAffectedByFacilitiesComp)
                .ToList();

            var targetLinks = affectedDefs.ToDictionary(
                def => def,
                GetValidLinkedFacilities);

            // 已知建筑族和自定义非 Building 类型。
            foreach (var def in affectedDefs.Where(def => def.thingClass != typeof(Building)))
            {
                var category = ResolveTypedCategory(def.thingClass!);
                var info = GetOrCreateCategory(result, category.key, category.displayName,
                    category.baseType.Name);

                AddTarget(info, def);
                foreach (var facilityDef in targetLinks[def])
                    AddFacility(info, facilityDef);
            }

            // 已知建筑族需要包含尚未声明设施 comp 的同族目标，才能实现跨 Mod 兼容。
            foreach (var info in result.Values.Where(info => info.thingClassName != typeof(Building).Name))
            {
                var baseType = ResolveBaseType(info.thingClassName);
                if (baseType == null) continue;

                foreach (var def in allDefs.Where(def => baseType.IsAssignableFrom(def.thingClass!)))
                    AddTarget(info, def);
            }

            // 普通 Building 不能整体扩展；只按已有“目标—设施”关系的连通分量建组。
            AddGenericBuildingGroups(result,
                affectedDefs.Where(def => def.thingClass == typeof(Building)).ToList(),
                targetLinks);

            // 独立扫描得到但没有被任何目标引用的设施进入待分配区。
            var referencedFacilities = new HashSet<string>(
                targetLinks.Values
                    .SelectMany(list => list)
                    .Select(def => def.defName));

            var unassigned = facilityDefs
                .Where(def => !referencedFacilities.Contains(def.defName))
                .ToList();

            if (unassigned.Count > 0)
            {
                var info = GetOrCreateCategory(result, UnassignedCategory,
                    "FC.CatUnassigned".Translate(), "");
                foreach (var facilityDef in unassigned)
                    AddFacility(info, facilityDef);
            }

            foreach (var info in result.Values)
            {
                info.targets = info.targets
                    .OrderBy(target => target.label)
                    .ThenBy(target => target.defName)
                    .ToList();

                foreach (var source in info.facilities.Keys.ToList())
                    info.facilities[source] = info.facilities[source]
                        .Distinct()
                        .OrderBy(defName => defName)
                        .ToList();
            }

            return result;
        }

        private static bool HasFacilityComp(ThingDef def)
            => def.comps?.Any(comp => comp is CompProperties_Facility) == true;

        private static bool HasAffectedByFacilitiesComp(ThingDef def)
            => def.comps?.Any(comp => comp is CompProperties_AffectedByFacilities) == true;

        private static List<ThingDef> GetValidLinkedFacilities(ThingDef def)
        {
            return def.comps?
                .OfType<CompProperties_AffectedByFacilities>()
                .Where(comp => comp.linkableFacilities != null)
                .SelectMany(comp => comp.linkableFacilities)
                .Where(facilityDef => facilityDef != null)
                .GroupBy(facilityDef => facilityDef.defName)
                .Select(group => group.First())
                .ToList()
                ?? new List<ThingDef>();
        }

        private static (string key, string displayName, Type baseType) ResolveTypedCategory(Type type)
        {
            var known = KnownCategories.FirstOrDefault(category =>
            {
                var knownType = ResolveType(category.typeName);
                return knownType != null && knownType.IsAssignableFrom(type);
            });

            if (known != null)
            {
                var displayName = known.translationKey.Translate().ToString();
                return (displayName, displayName, ResolveType(known.typeName)!);
            }

            // 未知自定义类型维持原有按类名分组行为。
            return (type.Name, type.Name, type);
        }

        private static Type? ResolveBaseType(string className)
        {
            var known = KnownCategories.FirstOrDefault(category =>
                category.typeName == className);
            if (known != null)
                return ResolveType(known.typeName);

            return ResolveType(className);
        }

        private static Type? ResolveType(string className)
        {
            return DefDatabase<ThingDef>.AllDefs
                .Select(def => def.thingClass)
                .FirstOrDefault(type => type?.Name == className);
        }

        private static void AddGenericBuildingGroups(
            Dictionary<string, CategoryInfo> result,
            List<ThingDef> genericTargets,
            Dictionary<ThingDef, List<ThingDef>> targetLinks)
        {
            var remaining = new HashSet<ThingDef>(
                genericTargets.Where(def => targetLinks[def].Count > 0));

            while (remaining.Count > 0)
            {
                var first = remaining.First();
                remaining.Remove(first);

                var componentTargets = new List<ThingDef> { first };
                var componentFacilities = new Dictionary<string, ThingDef>();
                var queue = new Queue<ThingDef>();
                queue.Enqueue(first);

                while (queue.Count > 0)
                {
                    var target = queue.Dequeue();
                    foreach (var facilityDef in targetLinks[target])
                        componentFacilities[facilityDef.defName] = facilityDef;

                    var connected = remaining
                        .Where(candidate => targetLinks[candidate]
                            .Any(facilityDef => componentFacilities.ContainsKey(facilityDef.defName)))
                        .ToList();

                    foreach (var candidate in connected)
                    {
                        remaining.Remove(candidate);
                        componentTargets.Add(candidate);
                        queue.Enqueue(candidate);
                    }
                }

                var anchor = componentFacilities.Keys.OrderBy(defName => defName).First();
                var categoryKey = $"Building:{anchor}";
                var displayName = string.Format("FC.CatLinkedBuilding".Translate(), anchor);
                var info = GetOrCreateCategory(result, categoryKey, displayName, typeof(Building).Name);

                foreach (var target in componentTargets)
                    AddTarget(info, target);
                foreach (var facilityDef in componentFacilities.Values)
                    AddFacility(info, facilityDef);
            }
        }

        private static CategoryInfo GetOrCreateCategory(
            Dictionary<string, CategoryInfo> result,
            string categoryKey,
            string displayName,
            string thingClassName)
        {
            if (result.TryGetValue(categoryKey, out var info))
                return info;

            info = new CategoryInfo
            {
                displayName = displayName,
                thingClassName = thingClassName,
            };
            result[categoryKey] = info;
            return info;
        }

        private static void AddTarget(CategoryInfo info, ThingDef def)
        {
            if (!info.targets.Any(target => target.defName == def.defName))
                info.targets.Add(new BuildingTarget(def.defName, def.label, GetModSourceName(def)));
        }

        private static void AddFacility(CategoryInfo info, ThingDef facilityDef)
        {
            var source = GetModSourceName(facilityDef);
            if (!info.facilities.TryGetValue(source, out var facilities))
            {
                facilities = new List<string>();
                info.facilities[source] = facilities;
            }

            if (!facilities.Contains(facilityDef.defName))
                facilities.Add(facilityDef.defName);
        }

        private static string GetModSourceName(ThingDef def)
        {
            if (def.modContentPack == null)
                return "FC.SourceUnknown".Translate();

            if (def.modContentPack.IsOfficialMod
                && OfficialModKeys.TryGetValue(def.modContentPack.Name, out var key))
                return key.Translate();

            return def.modContentPack.Name;
        }
    }
}
