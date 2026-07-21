using System.Collections.Generic;
using System.Linq;

namespace FacilityCompat
{
    /// <summary>
    /// 设施配置剪贴板：支持复制某设施的排除列表，粘贴到同类别或跨类别设施（自动过滤不匹配目标）
    /// </summary>
    public static class FacilityClipboard
    {
        public static string? sourceCategory;
        public static string? sourceFacilityDefName;
        public static List<string> excludedDefs = new();
        public static FacilityMode copyMode;
        public static bool hasData;

        /// 复制当前设施的配置
        public static void Copy(string category, string facilityDefName,
            FacilityMode mode, List<string> excluded)
        {
            sourceCategory = category;
            sourceFacilityDefName = facilityDefName;
            copyMode = mode;
            excludedDefs = new List<string>(excluded);
            hasData = true;
        }

        /// <summary>
        /// 粘贴到目标设施。
        /// 返回被过滤掉的数量（源排除列表中的目标在目标类别中不存在）。
        /// 当 Manual 模式下所有排除项都被过滤（跨类别无匹配）时，保持目标设施原有设置。
        /// </summary>
        public static int Paste(string targetCategory, string targetFacilityDefName,
            FacilityCompatSettings settings, out FacilityMode newMode,
            out List<string> newExcluded)
        {
            newMode = copyMode;
            newExcluded = new List<string>();
            int filtered = 0;

            if (!hasData) return filtered;

            var targetDefNames = GetTargetDefNames(settings, targetCategory);
            if (targetDefNames.Count == 0) return filtered;

            foreach (var defName in excludedDefs)
            {
                if (targetDefNames.Contains(defName))
                    newExcluded.Add(defName);
                else
                    filtered++;
            }

            // 手动模式下所有排除项在目标类别中都不存在 → 回退到目标设施原有设置
            if (copyMode == FacilityMode.Manual && excludedDefs.Count > 0 && newExcluded.Count == 0)
            {
                newMode = settings.GetMode(targetCategory, targetFacilityDefName);
                newExcluded = settings.GetExcludedList(targetCategory, targetFacilityDefName);
            }

            return filtered;
        }

        // 粘贴到同类别下所有设施（批量）。返回每个设施的过滤情况。
        public static Dictionary<string, int> PasteToAllInCategory(
            string targetCategory, FacilityCompatSettings settings,
            out FacilityMode newMode, out Dictionary<string, List<string>> newExcludedMap)
        {
            newMode = copyMode;
            newExcludedMap = new Dictionary<string, List<string>>();
            var filterMap = new Dictionary<string, int>();

            if (!hasData) return filterMap;

            var facilityNames = GetFacilityNames(settings, targetCategory);
            foreach (var fn in facilityNames)
            {
                var filtered = Paste(targetCategory, fn, settings, out _, out var excluded);
                newExcludedMap[fn] = excluded;
                filterMap[fn] = filtered;
            }

            return filterMap;
        }

        /// 清除剪贴板
        public static void Clear()
        {
            hasData = false;
            sourceCategory = null;
            sourceFacilityDefName = null;
            excludedDefs.Clear();
            copyMode = FacilityMode.All;
        }

        private static List<string> GetTargetDefNames(FacilityCompatSettings settings, string category)
        {
            if (settings.categories.TryGetValue(category, out var info))
                return info.targets.Select(t => t.defName).ToList();
            return new List<string>();
        }

        private static List<string> GetFacilityNames(FacilityCompatSettings settings, string category)
        {
            var names = new List<string>();
            if (settings.categories.TryGetValue(category, out var info))
                foreach (var list in info.facilities.Values)
                    names.AddRange(list);
            return names.Distinct().ToList();
        }
    }
}
