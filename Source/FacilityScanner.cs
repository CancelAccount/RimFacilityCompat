using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施扫描器：遍历所有带 CompProperties_AffectedByFacilities 的建筑，
    /// 按 thingClass 分类，收集各类别中的设施和目标
    /// </summary>
    public static class FacilityScanner
    {
        /// <summary>thingClass → 翻译 key 映射</summary>
        private static readonly Dictionary<string, string> KnownCategoryKeys = new()
        {
            ["Building_Bed"] = "FC.CatBed",
            ["Building_WorkTable"] = "FC.CatWorkTable",
            ["Building_ResearchBench"] = "FC.CatResearchBench",
            ["Building_CryptoBed"] = "FC.CatCryptoBed",
            ["Building_GeneAssembler"] = "FC.CatGeneAssembler",
            ["Building_GravEngine"] = "FC.CatGravEngine",
            ["Building_HoldingPlatform"] = "FC.CatHoldingPlatform",
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
        /// 扫描所有类别，返回 类别名 → CategoryInfo
        /// </summary>
        public static Dictionary<string, CategoryInfo> ScanAll()
        {
            var result = new Dictionary<string, CategoryInfo>();

            var affectedDefs = DefDatabase<ThingDef>.AllDefs
                .Where(def => def.thingClass != null
                    && def.thingClass != typeof(Building)
                    && def.comps.Any(c => c is CompProperties_AffectedByFacilities))
                .ToList();

            foreach (var def in affectedDefs)
            {
                var className = def.thingClass!.Name;
                var category = GetCategoryDisplayName(className);

                if (!result.TryGetValue(category, out var info))
                {
                    info = new CategoryInfo
                    {
                        displayName = category,
                        thingClassName = className,
                    };
                    result[category] = info;
                }

                if (!info.targets.Any(t => t.defName == def.defName))
                    info.targets.Add(new BuildingTarget(def.defName, def.label, GetModSourceName(def)));

                var affectedByFacilities = def.comps
                    .OfType<CompProperties_AffectedByFacilities>()
                    .FirstOrDefault();
                if (affectedByFacilities == null) continue;

                foreach (var facilityDef in affectedByFacilities.linkableFacilities)
                {
                    var source = GetModSourceName(facilityDef);
                    if (!info.facilities.ContainsKey(source))
                        info.facilities[source] = new List<string>();
                    if (!info.facilities[source].Contains(facilityDef.defName))
                        info.facilities[source].Add(facilityDef.defName);
                }
            }

            foreach (var info in result.Values)
                info.targets = info.targets.OrderBy(t => t.label).ToList();

            return result;
        }

        private static string GetCategoryDisplayName(string className)
            => KnownCategoryKeys.TryGetValue(className, out var key)
                ? key.Translate()
                : className;

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
