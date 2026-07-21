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

        /// <summary>用于 xpath 生成：类别→thingClassName（序列化）</summary>
        public Dictionary<string, string> savedCategories = new();

        /// <summary>用于 xpath 生成：类别→设施 defName 列表（序列化）</summary>
        public Dictionary<string, List<string>> savedFacilities = new();

        /// <summary>用于 xpath 生成：类别→目标 defName 列表（序列化）</summary>
        public Dictionary<string, List<string>> savedTargets = new();

        /// <summary>C# 运行时二次注入开关：默认关闭（仅靠 xpath），开启后首次运行时也生效，但是需要注意二次注入冲突</summary>
        public bool enableRuntimePatch;

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

        /// <summary>获取模式</summary>
        public FacilityMode GetMode(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!excludedTargets.TryGetValue(key, out var excluded) || excluded.Count == 0)
                return FacilityMode.All;
            if (categories.TryGetValue(category, out var info) && excluded.Count >= info.targets.Count)
                return FacilityMode.None;
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
        /// 保存 xpath 生成所需数据：类别→thingClassName、类别→设施列表、类别→目标defName列表
        /// </summary>
        public void SaveForXpath()
        {
            savedCategories = new Dictionary<string, string>();
            savedFacilities = new Dictionary<string, List<string>>();
            savedTargets = new Dictionary<string, List<string>>();
            foreach (var kvp in categories)
            {
                var category = kvp.Key;
                savedCategories[category] = kvp.Value.thingClassName;
                savedFacilities[category] = new List<string>();
                foreach (var list in kvp.Value.facilities.Values)
                    foreach (var fn in list)
                        if (!savedFacilities[category].Contains(fn))
                            savedFacilities[category].Add(fn);

                savedTargets[category] = kvp.Value.targets
                    .Select(t => t.defName)
                    .Distinct()
                    .ToList();
            }
        }

        /// <summary>
        /// 回到原始状态：只保留每个设施原本就链接的目标，取消一切跨mod修改
        /// </summary>
        public void ResetToOriginal()
        {
            excludedTargets.Clear();

            foreach (var kvp in originalLinks)
            {
                ExcludeNonOriginal(kvp.Key, kvp.Value);
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

        /// <summary>
        /// 设置是否已被修改（与原始状态比较）
        /// </summary>
        public bool IsModified()
        {
            // 如果排除列表为空 → 完全启用状态，即"一键全部启用"
            // 检查是否与原始状态不同
            return true; // 简化：总是允许保存
        }

        // 导出数据
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref excludedTargets, "excludedTargets",
                LookMode.Value, LookMode.Value, ref _keys, ref _values);
            Scribe_Collections.Look(ref savedCategories, "savedCategories",
                LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref savedFacilities, "savedFacilities",
                LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref savedTargets, "savedTargets",
                LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref enableRuntimePatch, "enableRuntimePatch");

            excludedTargets ??= new Dictionary<string, List<string>>();
            savedCategories ??= new Dictionary<string, string>();
            savedFacilities ??= new Dictionary<string, List<string>>();
            savedTargets ??= new Dictionary<string, List<string>>();
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
