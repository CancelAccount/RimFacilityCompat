using System.Collections.Generic;
using System.Linq;

namespace FacilityCompat
{
    /// <summary>
    /// 设施配置剪贴板：支持两个维度各复制/粘贴（仅限同类别，跨类别一律拦截）：
    /// - Facility：复制某附属设备的连接配置，粘贴到其他附属设备；
    /// - Target：复制某主设施的连接模式（哪些设施连接它），粘贴到其他主设施。
    /// 复制只记录「连接列表」，All/None 由列表内容自然表达（全列表 = All、空列表 = None），
    /// 无需单独模式；粘贴时源类别与目标类别不同则拦截，避免全选/全空跨类别造成误连接或误断开。
    /// </summary>
    public static class FacilityClipboard
    {
        /// <summary>剪贴板维度：附属设备侧 / 主设施侧（两者均为连接列表语义）</summary>
        public enum Direction { Facility, Target }

        /// <summary>当前剪贴板数据的维度（hasData 为 false 时无意义）</summary>
        public static Direction direction = Direction.Facility;

        /// <summary>复制时的类别，用于跨类别粘贴拦截</summary>
        public static string sourceCategory = "";

        /// <summary>内容列表（Facility 维度 = 连接的目标；Target 维度 = 连接的设施）</summary>
        public static List<string> contentDefs = new();
        public static bool hasData;

        /// <summary>复制某附属设备的配置（连接列表语义）</summary>
        public static void Copy(string category, List<string> linked)
        {
            direction = Direction.Facility;
            sourceCategory = category;
            contentDefs = new List<string>(linked);
            hasData = true;
        }

        /// <summary>复制某主设施的连接模式（连接列表语义）</summary>
        public static void CopyTarget(string category, List<string> linked)
        {
            direction = Direction.Target;
            sourceCategory = category;
            contentDefs = new List<string>(linked);
            hasData = true;
        }

        /// <summary>
        /// 粘贴到目标附属设备。返回 false 表示拦截（跨类别 / 无数据 / 方向不匹配 / 类别无目标），
        /// 不修改任何配置；返回 true 时 newLinked 为同类别内匹配的连接目标列表。
        /// </summary>
        public static bool Paste(string targetCategory, FacilityCompatSettings settings,
            out List<string> newLinked)
        {
            newLinked = new List<string>();
            if (!hasData || direction != Direction.Facility) return false;
            if (sourceCategory != targetCategory) return false; // 跨类别一律拦截

            var targetDefNames = GetTargetDefNames(settings, targetCategory);
            if (targetDefNames.Count == 0) return false;

            foreach (var defName in contentDefs)
                if (targetDefNames.Contains(defName))
                    newLinked.Add(defName);

            return true;
        }

        /// <summary>
        /// 粘贴到目标主设施。返回 false 表示拦截（跨类别 / 无数据 / 方向不匹配 / 类别无设施），
        /// 不修改任何配置；返回 true 时 newLinked 为同类别内匹配的连接设施列表。
        /// </summary>
        public static bool PasteTarget(string targetCategory, FacilityCompatSettings settings,
            out List<string> newLinked)
        {
            newLinked = new List<string>();
            if (!hasData || direction != Direction.Target) return false;
            if (sourceCategory != targetCategory) return false; // 跨类别一律拦截

            var facilityNames = GetFacilityNames(settings, targetCategory);
            if (facilityNames.Count == 0) return false;

            foreach (var defName in contentDefs)
                if (facilityNames.Contains(defName))
                    newLinked.Add(defName);

            return true;
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
