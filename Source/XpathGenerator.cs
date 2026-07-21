using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RimWorld;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// xpath 补丁生成器：在 Mod 启动时将用户配置的设施链接写入 Patches 目录。
    /// 用 defName 精确匹配目标 ThingDef，而非 thingClass，解决继承属性在 xpath 阶段不可见的问题。
    /// 同类设施打包注入，每个目标只打一次补丁。
    /// </summary>
    public static class XpathGenerator
    {
        /// <summary>
        /// 根据已保存的设置生成 xpath 补丁文件。
        /// 对每个目标 defName 按是否有 CompProperties_AffectedByFacilities 分为两类：
        ///   1) 已有 → 在 linkableFacilities 下批量追加设施
        ///   2) 没有 → 创建新的 CompProperties_AffectedByFacilities 并填入所有设施
        /// </summary>
        public static void Generate(FacilityCompatSettings settings)
        {
            FCLogger.Raw($"XpathGen: start, categories={settings.savedCategories.Count}, facilities={settings.savedFacilities.Count}");

            if (settings.savedCategories.Count == 0 || settings.savedFacilities.Count == 0)
            {
                FCLogger.Raw("XpathGen: no saved data, abort");
                return;
            }

            var doc = new XDocument(new XElement("Patch"));
            int totalOps = 0;
            int totalCategories = 0;

            foreach (var catKvp in settings.savedFacilities)
            {
                var category = catKvp.Key;
                var facilityNames = catKvp.Value;

                if (!settings.savedCategories.TryGetValue(category, out var thingClassName))
                {
                    FCLogger.Raw($"XpathGen: skip [{category}], no thingClassName");
                    continue;
                }

                // 获取目标列表：优先用已保存的 savedTargets，没有则从 DefDatabase 降级构建
                if (!settings.savedTargets.TryGetValue(category, out var targetDefNames) || targetDefNames.Count == 0)
                {
                    targetDefNames = DefDatabase<ThingDef>.AllDefs
                        .Where(def => def.thingClass != null && def.thingClass.Name == thingClassName)
                        .Select(def => def.defName)
                        .Distinct()
                        .ToList();
                    FCLogger.Raw($"XpathGen: [{category}] fallback={targetDefNames.Count} targets");
                }

                // 筛选未被完全禁用的设施
                var activeFacilities = facilityNames
                    .Where(fn => !IsFullyDisabled(settings, category, fn))
                    .ToList();
                if (activeFacilities.Count == 0) continue;

                // 按目标是否有 CompProperties_AffectedByFacilities 分组
                var defsWithComp = new List<string>();
                var defsWithoutComp = new List<string>();

                foreach (var targetDefName in targetDefNames)
                {
                    var targetDef = DefDatabase<ThingDef>.GetNamedSilentFail(targetDefName);
                    if (targetDef == null) continue;

                    if (targetDef.comps.Any(c => c is CompProperties_AffectedByFacilities))
                        defsWithComp.Add(targetDefName);
                    else
                        defsWithoutComp.Add(targetDefName);
                }

                FCLogger.Raw($"XpathGen: [{category}] fac={activeFacilities.Count}, withComp={defsWithComp.Count}, withoutComp={defsWithoutComp.Count}");

                // 操作1：目标已有 CompProperties_AffectedByFacilities → 追加到 linkableFacilities
                foreach (var defName in defsWithComp)
                {
                    var xpath = $"/Defs/ThingDef[defName=\"{defName}\"]/comps/li[@Class=\"CompProperties_AffectedByFacilities\"]/linkableFacilities";
                    var cond = new XElement("Operation",
                        new XAttribute("Class", "PatchOperationConditional"));
                    cond.Add(new XElement("xpath", xpath));

                    var match = new XElement("match",
                        new XAttribute("Class", "PatchOperationAdd"));
                    match.Add(new XElement("xpath", xpath));

                    var value = new XElement("value");
                    foreach (var fn in activeFacilities)
                        value.Add(new XElement("li", fn));
                    match.Add(value);
                    cond.Add(match);
                    doc.Root!.Add(cond);
                    totalOps++;
                }

                // 操作2：目标没有 CompProperties_AffectedByFacilities → 创建新 comp
                foreach (var defName in defsWithoutComp)
                {
                    var compsXpath = $"/Defs/ThingDef[defName=\"{defName}\"]/comps";
                    var cond = new XElement("Operation",
                        new XAttribute("Class", "PatchOperationConditional"));
                    cond.Add(new XElement("xpath", compsXpath));

                    var match = new XElement("match",
                        new XAttribute("Class", "PatchOperationAdd"));
                    match.Add(new XElement("xpath", compsXpath));

                    var linkableFacilities = new XElement("linkableFacilities");
                    foreach (var fn in activeFacilities)
                        linkableFacilities.Add(new XElement("li", fn));

                    var value = new XElement("value",
                        new XElement("li",
                            new XAttribute("Class", "CompProperties_AffectedByFacilities"),
                            linkableFacilities));
                    match.Add(value);
                    cond.Add(match);
                    doc.Root!.Add(cond);
                    totalOps++;
                }

                totalCategories++;
            }

            FCLogger.Raw($"XpathGen: done, {totalCategories} categories, {totalOps} ops");

            // 写入文件
            try
            {
                var dir = Path.Combine(settings.modDir, "1.6", "Patches");
                Directory.CreateDirectory(dir);
                var filePath = Path.Combine(dir, "FC_Generated.xml");
                doc.Save(filePath);
                FCLogger.Msg("FC.LogXpathGenerated", filePath);
            }
            catch (System.Exception ex)
            {
                FCLogger.Exception("FC.LogXpathFailed".Translate(), ex);
            }
        }

        /// <summary>检查设施是否对该类别完全禁用</summary>
        private static bool IsFullyDisabled(FacilityCompatSettings settings, string category, string facility)
        {
            var key = $"{category}|{facility}";
            if (!settings.excludedTargets.TryGetValue(key, out var excluded))
                return false; // 不在排除列表中 → 全部启用

            // 检查是否所有目标都被排除了
            if (settings.categories.TryGetValue(category, out var info))
                return excluded.Count >= info.targets.Count;

            return excluded.Count > 0;
        }
    }
}
