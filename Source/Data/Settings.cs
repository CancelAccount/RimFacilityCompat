using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施兼容补丁的持久化设置
    /// 排除列表模式：默认所有设施注入所有同类目标，用户可排除特定组合。
    /// originalLinks 记录修改前的原始链接，"全部重置"即回到该状态。
    /// </summary>
    public class FacilityCompatSettings : ModSettings
    {
        /// <summary>Mod 目录路径，由 Mod 构造函数设置</summary>
        public string modDir = "";
        /// <summary>所有扫描到的类别信息，由 Scanner/Patcher 填充</summary>
        public Dictionary<string, CategoryInfo> categories = new();

        /// <summary>
        /// 排除列表：key = "类别|设施defName"，value = 不注入的目标 defName 集合
        /// </summary>
        public Dictionary<string, List<string>> excludedTargets = new();

        /// <summary>原始链接（修改前状态），不序列化</summary>
        public Dictionary<string, List<string>> originalLinks = new();

        /// <summary>是否已完成首次初始化（持久化）：首次为 false 时执行保守语义 ResetToOriginal</summary>
        public bool initialized;

        public bool scanCompleted;

        /// <summary>检查某设施是否应注入某目标</summary>
        public bool ShouldLink(string category, string facilityDefName, string targetDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!excludedTargets.TryGetValue(key, out var excluded))
                return true;
            return !excluded.Contains(targetDefName);
        }

        /// <summary>切换某设施对某目标的注入状态</summary>
        public void ToggleLink(string category, string facilityDefName, string targetDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!excludedTargets.ContainsKey(key))
                excludedTargets[key] = new List<string>();

            if (excludedTargets[key].Contains(targetDefName))
                excludedTargets[key].Remove(targetDefName);
            else
                excludedTargets[key].Add(targetDefName);

            if (excludedTargets[key].Count == 0)
                excludedTargets.Remove(key);
        }

        /// <summary>设置某设施对某目标的注入状态（幂等，已是目标状态时不改动）</summary>
        public void SetLink(string category, string facilityDefName, string targetDefName, bool linked)
        {
            bool currentlyExcluded = excludedTargets.TryGetValue(
                MakeKey(category, facilityDefName), out var excluded)
                && excluded.Contains(targetDefName);
            if (linked == !currentlyExcluded) return; // 已是目标状态
            ToggleLink(category, facilityDefName, targetDefName);
        }

        /// <summary>该设施对当前类别全部启用</summary>
        public void EnableAllTargets(string category, string facilityDefName)
        {
            excludedTargets.Remove(MakeKey(category, facilityDefName));
        }

        /// <summary>该设施对当前类别全部禁用</summary>
        public void DisableAllTargets(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!excludedTargets.ContainsKey(key))
                excludedTargets[key] = new List<string>();
            else
                excludedTargets[key].Clear();

            if (categories.TryGetValue(category, out var info))
                foreach (var t in info.targets)
                    excludedTargets[key].Add(t.defName);
        }

        /// <summary>
        /// 获取模式。None 判定按"当前有效 targets（过滤 mod 卸载后失效的 defName）是否全部被排除"
        /// ——避免残留条目使 count 虚高导致误判。
        /// </summary>
        public FacilityMode GetMode(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!excludedTargets.TryGetValue(key, out var excluded) || excluded.Count == 0)
                return FacilityMode.All;
            if (categories.TryGetValue(category, out var info))
            {
                var validTargetNames = info.targets
                    .Select(t => t.defName)
                    .Where(fn => DefDatabase<ThingDef>.GetNamedSilentFail(fn) != null);
                if (validTargetNames.Any(excluded.Contains))
                    return validTargetNames.All(excluded.Contains)
                        ? FacilityMode.None : FacilityMode.Manual;
            }
            return FacilityMode.Manual;
        }

        /// <summary>获取排除列表的副本（用于复制）</summary>
        public List<string> GetExcludedList(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (excludedTargets.TryGetValue(key, out var excluded))
                return new List<string>(excluded);
            return new List<string>();
        }

        /// <summary>设置模式和排除列表（用于粘贴）</summary>
        public void SetModeAndExcluded(string category, string facilityDefName,
            FacilityMode mode, List<string> excluded)
        {
            switch (mode)
            {
                case FacilityMode.All:
                    EnableAllTargets(category, facilityDefName);
                    break;
                case FacilityMode.None:
                    DisableAllTargets(category, facilityDefName);
                    break;
                case FacilityMode.Manual:
                    var key = MakeKey(category, facilityDefName);
                    if (!excludedTargets.ContainsKey(key))
                        excludedTargets[key] = new List<string>();
                    else
                        excludedTargets[key].Clear();
                    excludedTargets[key].AddRange(excluded);
                    if (excludedTargets[key].Count == 0)
                        excludedTargets.Remove(key);
                    break;
            }
        }

        /// <summary>全局全部启用 (跨mod全兼容)</summary>
        public void EnableAll()
        {
            excludedTargets.Clear();
        }

        /// <summary>
        /// 回到原始状态：只保留每个设施原本就链接的目标，取消一切跨mod修改。
        /// 对于新增设施（originalLinks 中无记录），视为原本无链接，全部禁用。
        /// </summary>
        public void ResetToOriginal()
        {
            excludedTargets.Clear();

            // 处理所有已扫描到的设施：已有记录的保留原始链接，新设施全部禁用
            foreach (var catKvp in categories)
            {
                var category = catKvp.Key;
                foreach (var srcList in catKvp.Value.facilities.Values)
                {
                    foreach (var facilityDefName in srcList)
                    {
                        var key = MakeKey(category, facilityDefName);
                        if (originalLinks.TryGetValue(key, out var originalTargets))
                            ExcludeNonOriginal(key, originalTargets);
                        else
                            // 该设施原本没有任何链接（新 mod 引入）→ 全部禁用
                            DisableAllTargets(category, facilityDefName);
                    }
                }
            }
        }

        /// <summary>
        /// 将单个设施重置到原始状态：只保留原本就链接的目标。
        /// 若该设施原本没有任何链接 → 全部禁用。
        /// </summary>
        public void ResetFacilityToOriginal(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            excludedTargets.Remove(key);

            if (originalLinks.TryGetValue(key, out var originalTargets))
                ExcludeNonOriginal(key, originalTargets);
            else
                // 该设施原本没有任何链接 → 全部禁用
                DisableAllTargets(category, facilityDefName);
        }

        /// <summary>
        /// 将单个主设施（目标）重置到原始状态：类别下所有设施与它的连接恢复原始。
        /// 与 ResetFacilityToOriginal 对称（目标维度）：无设施记录时视为原始无连接 → 全部断开。
        /// </summary>
        public void ResetTargetToOriginal(string category, string targetDefName)
        {
            if (!categories.TryGetValue(category, out var info)) return;
            foreach (var fn in info.facilities.Values.SelectMany(l => l).Distinct())
            {
                bool original = originalLinks.TryGetValue(MakeKey(category, fn), out var orig)
                    && orig.Contains(targetDefName);
                SetLink(category, fn, targetDefName, original);
            }
        }

        /// <summary>
        /// 获取主设施（目标）维度的模式：
        /// All = 全部设施连接它；None = 无设施连接它；Manual = 部分连接。
        /// 与 GetMode（设施维度）对称。
        /// </summary>
        public FacilityMode GetTargetMode(string category, string targetDefName)
        {
            if (!categories.TryGetValue(category, out var info)) return FacilityMode.Manual;
            int total = 0, linked = 0;
            foreach (var fn in info.facilities.Values.SelectMany(l => l).Distinct())
            {
                total++;
                if (ShouldLink(category, fn, targetDefName)) linked++;
            }
            if (total == 0 || linked == 0) return FacilityMode.None;
            return linked == total ? FacilityMode.All : FacilityMode.Manual;
        }

        /// <summary>获取当前连接某主设施（目标）的设施 defName 列表（用于复制）</summary>
        public List<string> GetTargetLinkedList(string category, string targetDefName)
        {
            var result = new List<string>();
            if (!categories.TryGetValue(category, out var info)) return result;
            foreach (var fn in info.facilities.Values.SelectMany(l => l).Distinct())
                if (ShouldLink(category, fn, targetDefName))
                    result.Add(fn);
            return result;
        }

        /// <summary>
        /// 设置主设施（目标）维度的模式与连接列表（用于粘贴）。
        /// Manual 时 linked 之外的设施全部断开（与 SetModeAndExcluded 的设施维度语义对称）。
        /// </summary>
        public void SetTargetModeAndLinked(string category, string targetDefName,
            FacilityMode mode, List<string> linked)
        {
            if (!categories.TryGetValue(category, out var info)) return;
            var facilityNames = info.facilities.Values.SelectMany(l => l).Distinct();
            foreach (var fn in facilityNames)
            {
                bool target = mode switch
                {
                    FacilityMode.All => true,
                    FacilityMode.None => false,
                    _ => linked.Contains(fn),
                };
                SetLink(category, fn, targetDefName, target);
            }
        }

        /// <summary>排除非原始目标</summary>
        private void ExcludeNonOriginal(string key, List<string> originalTargets)
        {
            var parts = key.Split('|');
            if (parts.Length != 2) return;
            var category = parts[0];

            if (!categories.TryGetValue(category, out var info)) return;

            foreach (var t in info.targets)
            {
                if (!originalTargets.Contains(t.defName))
                {
                    if (!excludedTargets.ContainsKey(key))
                        excludedTargets[key] = new List<string>();
                    excludedTargets[key].Add(t.defName);
                }
            }
        }

        // 导出数据
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref excludedTargets, "excludedTargets",
                LookMode.Value, LookMode.Value, ref _keys, ref _values);
            Scribe_Values.Look(ref initialized, "initialized");

            excludedTargets ??= new Dictionary<string, List<string>>();
        }

        private static string MakeKey(string category, string facility) => $"{category}|{facility}";

        private List<string> _keys = new();
        private List<List<string>> _values = new();
    }

    public class CategoryInfo
    {
        public string displayName = "";
        public string thingClassName = "";
        public List<BuildingTarget> targets = new();
        public Dictionary<string, List<string>> facilities = new();
    }

    public class BuildingTarget
    {
        public string defName = "";
        public string label = "";
        public string sourceMod = "";

        public BuildingTarget() { }
        public BuildingTarget(string defName, string label, string sourceMod = "")
        {
            this.defName = defName;
            this.label = label;
            this.sourceMod = sourceMod;
        }
    }

    public enum FacilityMode { All, None, Manual }
}
