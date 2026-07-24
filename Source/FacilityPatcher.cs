using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施补丁应用器：按类别和排除列表，将所有发现的设施注入对应目标
    /// </summary>
    [StaticConstructorOnStartup]
    public static class FacilityPatcher
    {
        static FacilityPatcher()
        {
            LongEventHandler.ExecuteWhenFinished(Apply);
        }

        public static void Apply()
        {
            try
            {
                var settings = FacilityCompatMod.Settings;
                if (settings == null)
                {
                    FCLogger.Warn("FC.LogNoSettings");
                    return;
                }

                settings.categories = FacilityScanner.ScanAll();
                settings.scanCompleted = true;
                settings.originalLinks = ScanOriginalLinks(settings.categories);

                // 始终填充 targets（设置界面需要）
                foreach (var kvp in settings.categories)
                {
                    var info = kvp.Value;
                    var allTargetDefs = DefDatabase<ThingDef>.AllDefs
                        .Where(def => def.thingClass != null
                            && def.thingClass.Name == info.thingClassName)
                        .ToList();
                    settings.categories[kvp.Key].targets = allTargetDefs
                        .Select(def => new BuildingTarget(def.defName, def.label,
                            def.modContentPack != null && !def.modContentPack.IsCoreMod
                                ? def.modContentPack.Name
                                : "FC.SourceCore".Translate()))
                        .OrderBy(t => t.label)
                        .ToList();
                }

                // 合并重复 CompProperties_AffectedByFacilities + 清理失效条目
                int totalSanitized = 0;
                foreach (var kvp in settings.categories)
                {
                    var info = kvp.Value;
                    var allTargetDefs = DefDatabase<ThingDef>.AllDefs
                        .Where(def => def.thingClass != null && def.thingClass.Name == info.thingClassName)
                        .ToList();
                    foreach (var targetDef in allTargetDefs)
                    {
                        if (CleanupComps(targetDef))
                            totalSanitized++;
                    }
                }
                if (totalSanitized > 0)
                    FCLogger.Msg("FC.LogSanitized", totalSanitized, settings.categories.Count);

                // C# 运行时注入（仅在开关开启时执行）
                if (settings.enableRuntimePatch)
                {
                    int totalPatched = 0;
                    foreach (var kvp in settings.categories)
                    {
                        var category = kvp.Key;
                        var info = kvp.Value;

                        var facilityDefNames = new List<string>();
                        foreach (var list in info.facilities.Values)
                            foreach (var fn in list)
                                facilityDefNames.Add(fn);

                        if (facilityDefNames.Count == 0) continue;

                        var allTargetDefs = DefDatabase<ThingDef>.AllDefs
                            .Where(def => def.thingClass != null
                                && def.thingClass.Name == info.thingClassName)
                            .ToList();

                        foreach (var targetDef in allTargetDefs)
                        {
                            if (PatchTarget(targetDef, facilityDefNames, category, settings))
                                totalPatched++;
                        }
                    }
                    FCLogger.Msg("FC.LogPatched", totalPatched, settings.categories.Count);
                }

                // 确保 xpath 补丁文件存在（首次运行或文件被删时自动生成）
                var xpathFile = System.IO.Path.Combine(settings.modDir, "1.6", "Patches", "FC_Generated.xml");
                if (!System.IO.File.Exists(xpathFile))
                {
                    if (settings.savedCategories.Count == 0)
                    {
                        // 首次运行：扫描并沿用原版链接
                        settings.ResetToOriginal();
                        settings.SaveForXpath();
                    }
                    settings.Write();
                    XpathGenerator.Generate(settings);
                    FCLogger.Warn("FC.LogNeedRestart");
                }
            }
            catch (System.Exception ex)
            {
                FCLogger.Exception("FC.LogPatchFailed".Translate(), ex);
            }
        }

        /// <summary>
        /// 合并重复的 CompProperties_AffectedByFacilities + 移除失效的 null 条目
        /// </summary>
        private static bool CleanupComps(ThingDef targetDef)
        {
            var allFacilityComps = targetDef.comps
                .OfType<CompProperties_AffectedByFacilities>().ToList();
            if (allFacilityComps.Count == 0) return false;

            bool changed = false;

            // 合并多个 CompProperties_AffectedByFacilities（xpath 可能追加重复 comp）
            if (allFacilityComps.Count > 1)
            {
                var merged = allFacilityComps[0];
                if (merged.linkableFacilities == null)
                    merged.linkableFacilities = new List<ThingDef>();
                for (int i = 1; i < allFacilityComps.Count; i++)
                {
                    if (allFacilityComps[i].linkableFacilities != null)
                        foreach (var f in allFacilityComps[i].linkableFacilities)
                            if (f != null && !merged.linkableFacilities.Any(x => x?.defName == f.defName))
                                merged.linkableFacilities.Add(f);
                    targetDef.comps.Remove(allFacilityComps[i]);
                }
                changed = true;
                FCLogger.Raw($"[CleanupComps] {targetDef.defName}: merged {allFacilityComps.Count} comps → {merged.linkableFacilities.Count} facilities: [{string.Join(", ", merged.linkableFacilities.Select(f => f?.defName ?? "null"))}]");
            }

            // 清理失效条目（mod 移除后残留的 null / 无效 defName）
            var affectedByFacilities = allFacilityComps[0];
            if (affectedByFacilities.linkableFacilities != null)
            {
                for (int i = affectedByFacilities.linkableFacilities.Count - 1; i >= 0; i--)
                {
                    if (affectedByFacilities.linkableFacilities[i] == null)
                    {
                        affectedByFacilities.linkableFacilities.RemoveAt(i);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static bool PatchTarget(ThingDef targetDef, List<string> facilityDefNames,
            string category, FacilityCompatSettings settings)
        {
            var affectedByFacilities = targetDef.comps
                .OfType<CompProperties_AffectedByFacilities>()
                .FirstOrDefault();

            if (affectedByFacilities == null)
            {
                affectedByFacilities = new CompProperties_AffectedByFacilities
                {
                    linkableFacilities = new List<ThingDef>()
                };
                targetDef.comps.Add(affectedByFacilities);
            }
            else if (affectedByFacilities.linkableFacilities == null)
            {
                affectedByFacilities.linkableFacilities = new List<ThingDef>();
            }

            bool changed = false;

            // 移除被用户排除的设施
            for (int i = affectedByFacilities.linkableFacilities.Count - 1; i >= 0; i--)
            {
                var f = affectedByFacilities.linkableFacilities[i];
                if (f == null) continue;
                if (!settings.ShouldLink(category, f.defName, targetDef.defName))
                {
                    affectedByFacilities.linkableFacilities.RemoveAt(i);
                    changed = true;
                }
            }

            // 添加被用户启用但尚未在列表中的设施
            foreach (var fn in facilityDefNames)
            {
                if (!settings.ShouldLink(category, fn, targetDef.defName))
                    continue;
                if (affectedByFacilities.linkableFacilities.Any(f => f.defName == fn))
                    continue;

                var facilityDef = DefDatabase<ThingDef>.GetNamedSilentFail(fn);
                if (facilityDef != null)
                {
                    affectedByFacilities.linkableFacilities.Add(facilityDef);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// 扫描原始链接：记录每个设施在各类别中原本就链接了哪些目标
        /// </summary>
        public static Dictionary<string, List<string>> ScanOriginalLinks(Dictionary<string, CategoryInfo> categories)
        {
            var result = new Dictionary<string, List<string>>();

            foreach (var ckv in categories)
            {
                var category = ckv.Key;
                var info = ckv.Value;

                var targetDefs = DefDatabase<ThingDef>.AllDefs
                    .Where(def => def.thingClass != null && def.thingClass.Name == info.thingClassName)
                    .ToList();

                foreach (var targetDef in targetDefs)
                {
                    var affectedByFacilities = targetDef.comps
                        .OfType<CompProperties_AffectedByFacilities>()
                        .FirstOrDefault();

                    if (affectedByFacilities?.linkableFacilities == null) continue;

                    foreach (var facilityDef in affectedByFacilities.linkableFacilities)
                    {
                        var key = $"{category}|{facilityDef.defName}";
                        if (!result.ContainsKey(key))
                            result[key] = new List<string>();
                        result[key].Add(targetDef.defName);
                    }
                }
            }

            return result;
        }
    }
}
