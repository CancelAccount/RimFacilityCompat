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
    /// 同类设施打包注入，每个目标只打一次补丁。
    /// </summary>
    public static class XpathGenerator
    {
        /// <summary>
        /// 根据已保存的设置生成 xpath 补丁文件。
        /// 对每个目标生成带 fallback 的补丁：深层 xpath 匹配失败时回退到 comps 层。
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

                int categoryOps = 0;

                // 为目标注入自己应该链接的设施
                foreach (var defName in targetDefNames)
                {
                    var targetDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                    if (targetDef == null) continue;

                    // 收集该目标应该链接的设施
                    var facilitiesForTarget = facilityNames
                        .Where(fn => settings.ShouldLink(category, fn, defName))
                        .ToList();

                    if (facilitiesForTarget.Count == 0) continue;

                    // 检查目标是否已有 CompProperties_AffectedByFacilities
                    bool hasComp = targetDef.comps.Any(c => c is CompProperties_AffectedByFacilities);

                    // 生成针对该目标的补丁
                    AddTargetPatch(doc, defName, facilitiesForTarget, hasComp);
                    totalOps++;
                    categoryOps++;
                }

                if (categoryOps > 0)
                    totalCategories++;

                FCLogger.Raw($"XpathGen: [{category}] targets={targetDefNames.Count}, ops={categoryOps}, facilities={facilityNames.Count}");
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

        /// <summary>
        /// 为目标生成补丁
        /// </summary>
        private static void AddTargetPatch(XDocument doc, string defName, List<string> facilities, bool hasComp)
        {
            var deepXpath = $"/Defs/ThingDef[defName=\"{defName}\"]/comps/li[@Class=\"CompProperties_AffectedByFacilities\"]/linkableFacilities";
            var compsXpath = $"/Defs/ThingDef[defName=\"{defName}\"]/comps";
            var defXpath = $"/Defs/ThingDef[defName=\"{defName}\"]";

            if (hasComp)
            {
                // 目标已有 CompProperties_AffectedByFacilities → 尝试追加到 linkableFacilities
                // 三级 fallback：deep → comps → def
                // nomatch 自身即 PatchOperationConditional，不额外包裹 <Operation>
                doc.Root!.Add(BuildConditionalOp(deepXpath,
                    BuildDeepMatch(deepXpath, facilities),
                    BuildConditionalOp(compsXpath,
                        BuildCompsMatch(compsXpath, facilities),
                        BuildConditionalOp(defXpath,
                            BuildDefMatch(defXpath, facilities),
                            null, "nomatch"),
                        "nomatch")));
            }
            else
            {
                // 目标没有 CompProperties_AffectedByFacilities → 在 comps 下创建新 comp
                // 二级 fallback：comps → def
                doc.Root!.Add(BuildConditionalOp(compsXpath,
                    BuildCompsMatch(compsXpath, facilities),
                    BuildConditionalOp(defXpath,
                        BuildDefMatch(defXpath, facilities),
                        null, "nomatch")));
            }
        }

        // 构件设施 <li> 列表
        private static List<XElement> BuildFacilityLiElements(List<string> facilities)
        {
            return facilities.Select(fn => new XElement("li", fn)).ToList();
        }

        // 构件 PatchOperationConditional（elementName 控制根标签：Operation 或 nomatch）
        private static XElement BuildConditionalOp(string xpath, XElement match, XElement? nomatch, string elementName = "Operation")
        {
            var op = new XElement(elementName,
                new XAttribute("Class", "PatchOperationConditional"));
            op.Add(new XElement("xpath", xpath));
            op.Add(match);
            if (nomatch != null)
                op.Add(nomatch);
            return op;
        }

        // buildmatch：深层匹配 → 追加 <li> 到 linkableFacilities
        private static XElement BuildDeepMatch(string xpath, List<string> facilities)
        {
            var match = new XElement("match",
                new XAttribute("Class", "PatchOperationAdd"));
            match.Add(new XElement("xpath", xpath));
            var value = new XElement("value");
            foreach (var li in BuildFacilityLiElements(facilities))
                value.Add(li);
            match.Add(value);
            return match;
        }

        // buildmatch：comps 层 → 追加新 CompProperties_AffectedByFacilities
        private static XElement BuildCompsMatch(string xpath, List<string> facilities)
        {
            var match = new XElement("match",
                new XAttribute("Class", "PatchOperationAdd"));
            match.Add(new XElement("xpath", xpath));
            match.Add(BuildCompValue(facilities));
            return match;
        }

        // buildmatch：def 层（兜底）→ 创建 <comps> + 新 CompProperties_AffectedByFacilities
        private static XElement BuildDefMatch(string xpath, List<string> facilities)
        {
            var match = new XElement("match",
                new XAttribute("Class", "PatchOperationAdd"));
            match.Add(new XElement("xpath", xpath));
            var value = new XElement("value",
                new XElement("comps",
                    BuildCompLiElement(facilities)));
            match.Add(value);
            return match;
        }

        // 构件完整的 CompProperties_AffectedByFacilities <li> 元素
        private static XElement BuildCompLiElement(List<string> facilities)
        {
            var linkable = new XElement("linkableFacilities");
            foreach (var li in BuildFacilityLiElements(facilities))
                linkable.Add(li);
            return new XElement("li",
                new XAttribute("Class", "CompProperties_AffectedByFacilities"),
                linkable);
        }

        // 构件 <value> 包含 CompProperties_AffectedByFacilities<li>
        private static XElement BuildCompValue(List<string> facilities)
        {
            return new XElement("value", BuildCompLiElement(facilities));
        }
    }
}
