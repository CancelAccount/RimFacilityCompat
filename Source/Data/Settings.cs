using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施兼容补丁的持久化设置
    /// 白名单模式：默认不注入任何跨 mod 连接，仅当用户/预设显式启用时才建立连接。
    /// originalLinks 记录修改前的原始链接（每次启动扫描），"全部重置"即回到该状态。
    /// </summary>
    public class FacilityCompatSettings : ModSettings
    {
        /// <summary>Mod 目录路径，由 Mod 构造函数设置</summary>
        public string modDir = "";
        /// <summary>所有扫描到的类别信息，由 Scanner/Patcher 填充</summary>
        public Dictionary<string, CategoryInfo> categories = new();

        /// <summary>
        /// 白名单：key = "类别|设施defName"，value = 启用的目标 defName 集合。
        /// 无 key 或空 = 该设施不连接任何目标（含原始链接也可断开，见 ResetToOriginal）。
        /// </summary>
        public Dictionary<string, List<string>> linkedTargets = new();

        /// <summary>原始链接（修改前状态），每次启动扫描</summary>
        public Dictionary<string, List<string>> originalLinks = new();

        /// <summary>是否已完成首次初始化（持久化）：首次为 false 时执行 ResetToOriginal 保留原始链接</summary>
        public bool initialized;

        /// <summary>检查某设施是否应注入某目标（白名单：默认 false）</summary>
        public bool ShouldLink(string category, string facilityDefName, string targetDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!linkedTargets.TryGetValue(key, out var linked))
                return false;
            return linked.Contains(targetDefName);
        }

        /// <summary>切换某设施对某目标的注入状态</summary>
        public void ToggleLink(string category, string facilityDefName, string targetDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!linkedTargets.ContainsKey(key))
                linkedTargets[key] = new List<string>();

            if (linkedTargets[key].Contains(targetDefName))
                linkedTargets[key].Remove(targetDefName);
            else
                linkedTargets[key].Add(targetDefName);

            if (linkedTargets[key].Count == 0)
                linkedTargets.Remove(key);
        }

        /// <summary>设置某设施对某目标的注入状态（幂等，已是目标状态时不改动）</summary>
        public void SetLink(string category, string facilityDefName, string targetDefName, bool linked)
        {
            bool currentlyLinked = linkedTargets.TryGetValue(
                MakeKey(category, facilityDefName), out var list)
                && list.Contains(targetDefName);
            if (linked == currentlyLinked) return; // 已是目标状态
            ToggleLink(category, facilityDefName, targetDefName);
        }

        /// <summary>该设施对当前类别全部启用</summary>
        public void EnableAllTargets(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!categories.TryGetValue(category, out var info))
            {
                linkedTargets.Remove(key);
                return;
            }
            linkedTargets[key] = info.targets.Select(t => t.defName).ToList();
        }

        /// <summary>该设施对当前类别全部禁用</summary>
        public void DisableAllTargets(string category, string facilityDefName)
        {
            linkedTargets.Remove(MakeKey(category, facilityDefName));
        }

        /// <summary>
        /// 获取模式。None 判定按"无白名单记录或记录为空"；
        /// All = 全部有效目标（过滤 mod 卸载后失效的 defName）均启用；Manual = 部分启用。
        /// </summary>
        public FacilityMode GetMode(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!linkedTargets.TryGetValue(key, out var linked) || linked.Count == 0)
                return FacilityMode.None;
            if (categories.TryGetValue(category, out var info))
            {
                var validTargetNames = info.targets
                    .Select(t => t.defName)
                    .Where(fn => DefDatabase<ThingDef>.GetNamedSilentFail(fn) != null)
                    .ToList();
                if (validTargetNames.Count == 0) return FacilityMode.None;
                return validTargetNames.All(linked.Contains)
                    ? FacilityMode.All : FacilityMode.Manual;
            }
            return FacilityMode.Manual;
        }

        /// <summary>获取启用列表的副本（用于复制）</summary>
        public List<string> GetLinkedList(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (linkedTargets.TryGetValue(key, out var linked))
                return new List<string>(linked);
            return new List<string>();
        }

        /// <summary>设置模式和启用列表（用于粘贴）</summary>
        public void SetModeAndLinked(string category, string facilityDefName,
            FacilityMode mode, List<string> linked)
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
                    if (linked == null || linked.Count == 0)
                        linkedTargets.Remove(key);
                    else
                        linkedTargets[key] = new List<string>(linked);
                    break;
            }
        }

        /// <summary>
        /// 回到原始状态：白名单 = 原始链接（originalLinks 每次启动扫描的真原始状态）。
        /// 新设施（originalLinks 无记录）自然无白名单记录 → 不连接任何目标。
        /// </summary>
        public void ResetToOriginal()
        {
            linkedTargets.Clear();
            foreach (var kvp in originalLinks)
                linkedTargets[kvp.Key] = new List<string>(kvp.Value);
        }

        /// <summary>
        /// 将单个设施重置到原始状态：白名单 = 该设施的原始链接；
        /// 无原始链接记录 → 移除白名单（不连接任何目标）。
        /// </summary>
        public void ResetFacilityToOriginal(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (originalLinks.TryGetValue(key, out var originalTargets))
                linkedTargets[key] = new List<string>(originalTargets);
            else
                linkedTargets.Remove(key);
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
        /// Manual 时 linked 之外的设施全部断开（与 SetModeAndLinked 的设施维度语义对称）。
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

        // 导出数据
        public override void ExposeData()
        {
            // 字段名 "linkedTargets" 与旧版黑名单 "excludedTargets" 不同：旧存档读不到则保持空，
            // 配合下方 initializedV2（旧存档无此键 → false）触发一次 ResetToOriginal 重建白名单。
            Scribe_Collections.Look(ref linkedTargets, "linkedTargets",
                LookMode.Value, LookMode.Value, ref _keys, ref _values);
            Scribe_Values.Look(ref initialized, "initializedV2");

            linkedTargets ??= new Dictionary<string, List<string>>();
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
