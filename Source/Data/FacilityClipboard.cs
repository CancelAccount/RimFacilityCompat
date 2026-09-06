using System.Collections.Generic;
using System.Linq;

namespace FacilityCompat
{
    /// <summary>
    /// 设施配置剪贴板：支持两个维度各复制/粘贴（同类别或跨类别，自动过滤不匹配项）：
    /// - Facility：复制某附属设备的排除配置，粘贴到其他附属设备；
    /// - Target：复制某主设施的连接模式（哪些设施连接它），粘贴到其他主设施。
    /// 两维度数据互不通用：粘贴按钮按当前选中侧匹配剪贴板方向。
    /// </summary>
    public static class FacilityClipboard
    {
        /// <summary>剪贴板维度：附属设备侧（排除列表语义）/ 主设施侧（连接列表语义）</summary>
        public enum Direction { Facility, Target }

        /// <summary>当前剪贴板数据的维度（hasData 为 false 时无意义）</summary>
        public static Direction direction = Direction.Facility;

        public static string? sourceCategory;
        /// <summary>源 defName（Facility 维度 = 设施；Target 维度 = 主设施）</summary>
        public static string? sourceDefName;
        /// <summary>内容列表（Facility 维度 = 排除的目标；Target 维度 = 连接的设施）</summary>
        public static List<string> contentDefs = new();
        public static FacilityMode copyMode;
        public static bool hasData;

        /// <summary>复制某附属设备的配置（排除列表语义）</summary>
        public static void Copy(string category, string facilityDefName,
            FacilityMode mode, List<string> excluded)
        {
            direction = Direction.Facility;
            sourceCategory = category;
            sourceDefName = facilityDefName;
            copyMode = mode;
            contentDefs = new List<string>(excluded);
            hasData = true;
        }

        /// <summary>复制某主设施的连接模式（连接列表语义）</summary>
        public static void CopyTarget(string category, string targetDefName,
            FacilityMode mode, List<string> linked)
        {
            direction = Direction.Target;
            sourceCategory = category;
            sourceDefName = targetDefName;
            copyMode = mode;
            contentDefs = new List<string>(linked);
            hasData = true;
        }

        /// <summary>
        /// 粘贴到目标附属设备。
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

            if (!hasData || direction != Direction.Facility) return filtered;

            var targetDefNames = GetTargetDefNames(settings, targetCategory);
            if (targetDefNames.Count == 0) return filtered;

            foreach (var defName in contentDefs)
            {
                if (targetDefNames.Contains(defName))
                    newExcluded.Add(defName);
                else
                    filtered++;
            }

            // 手动模式下所有排除项在目标类别中都不存在 → 回退到目标设施原有设置
            if (copyMode == FacilityMode.Manual && contentDefs.Count > 0 && newExcluded.Count == 0)
            {
                newMode = settings.GetMode(targetCategory, targetFacilityDefName);
                newExcluded = settings.GetExcludedList(targetCategory, targetFacilityDefName);
            }

            return filtered;
        }

        /// <summary>
        /// 粘贴到目标主设施。
        /// 返回被过滤掉的数量（源连接列表中的设施在目标类别中不存在）。
        /// 当 Manual 模式下所有连接项都被过滤（跨类别无匹配）时，保持目标主设施原有设置。
        /// </summary>
        public static int PasteTarget(string targetCategory, string targetTargetDefName,
            FacilityCompatSettings settings, out FacilityMode newMode,
            out List<string> newLinked)
        {
            newMode = copyMode;
            newLinked = new List<string>();
            int filtered = 0;

            if (!hasData || direction != Direction.Target) return filtered;

            var facilityNames = GetFacilityNames(settings, targetCategory);
            if (facilityNames.Count == 0) return filtered;

            foreach (var defName in contentDefs)
            {
                if (facilityNames.Contains(defName))
                    newLinked.Add(defName);
                else
                    filtered++;
            }

            // 手动模式下所有连接项在目标类别中都不存在 → 回退到目标主设施原有设置
            if (copyMode == FacilityMode.Manual && contentDefs.Count > 0 && newLinked.Count == 0)
            {
                newMode = settings.GetTargetMode(targetCategory, targetTargetDefName);
                newLinked = settings.GetTargetLinkedList(targetCategory, targetTargetDefName);
            }

            return filtered;
        }

        /// 清除剪贴板
        public static void Clear()
        {
            hasData = false;
            sourceCategory = null;
            sourceDefName = null;
            contentDefs.Clear();
            copyMode = FacilityMode.All;
        }

        // 获取目标类别下的所有目标设施名称
        private static List<string> GetTargetDefNames(FacilityCompatSettings settings, string category)
        {
            if (settings.categories.TryGetValue(category, out var info))
                return info.targets.Select(t => t.defName).ToList();
            return new List<string>();
        }

        // 获取目标类别下的所有设施名称（包含所有子类别）
        private static List<string> GetFacilityNames(FacilityCompatSettings settings, string category)
        {
            var names = new List<string>();
            if (settings.categories.TryGetValue(category, out var info))
                foreach (var list in info.facilities.Values)        // 遍历所有子类别下的设施
                    names.AddRange(list);
            return names.Distinct().ToList();
        }
    }
}
