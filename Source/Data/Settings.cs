using System.Collections.Generic;
using System.Linq;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施兼容补丁的持久化设置。
    /// 三态覆盖表：linkedTargets 无 key = 未配置（回退保留原始链接 originalLinks）；
    /// 有 key 且空列表 = 显式全部断开；有 key 且非空 = 显式启用集合。
    /// 这样新增 mod 自带的原始连接不会被清空，同时用户仍可显式断开或启用。
    /// </summary>
    public class FacilityCompatSettings : ModSettings
    {
        /// <summary>Mod 目录路径，由 Mod 构造函数设置</summary>
        public string modDir = "";
        /// <summary>所有扫描到的类别信息，由 Scanner/Patcher 填充</summary>
        public Dictionary<string, CategoryInfo> categories = new();

        /// <summary>
        /// 覆盖表：key = "类别|设施defName"，value = 显式启用的目标 defName 集合。
        /// 无 key = 未配置（回退原始链接）；空列表 = 显式全部断开；非空 = 显式启用集合。
        /// </summary>
        public Dictionary<string, List<string>> linkedTargets = new();

        /// <summary>原始链接（修改前状态），每次启动扫描</summary>
        public Dictionary<string, List<string>> originalLinks = new();

        /// <summary>是否已完成首次初始化（持久化）：首次为 false 时清空覆盖表，让三态语义从干净状态生效</summary>
        public bool initialized;

        /// <summary>
        /// 检查某设施是否应注入某目标。三态语义：
        /// linkedTargets 有 key → 按列表（空列表 = 显式全部断开）；
        /// 无 key（未配置）→ 回退到原始链接（originalLinks），保留 mod 自带的连接声明。
        /// </summary>
        public bool ShouldLink(string category, string facilityDefName, string targetDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (linkedTargets.TryGetValue(key, out var linked))
                return linked.Contains(targetDefName);
            return originalLinks.TryGetValue(key, out var orig) && orig.Contains(targetDefName);
        }

        /// <summary>
        /// 切换某设施对某目标的注入状态。
        /// 首次操作某设施时先「物化」其当前生效状态（未配置 = 原始链接）为显式列表，
        /// 再应用切换；切换后列表为空时保留空 key，表示「显式全部断开」（区别于未配置）。
        /// </summary>
        public void ToggleLink(string category, string facilityDefName, string targetDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!linkedTargets.TryGetValue(key, out var list))
            {
                list = originalLinks.TryGetValue(key, out var orig)
                    ? new List<string>(orig)
                    : new List<string>();
                linkedTargets[key] = list;
            }

            if (list.Contains(targetDefName))
                list.Remove(targetDefName);
            else
                list.Add(targetDefName);
        }

        /// <summary>设置某设施对某目标的注入状态（幂等，已是目标状态时不改动）。
        /// 当前状态经 ShouldLink 判断（含原始链接兜底），避免把「未配置但原始已连」误当作断开。</summary>
        public void SetLink(string category, string facilityDefName, string targetDefName, bool linked)
        {
            if (linked == ShouldLink(category, facilityDefName, targetDefName)) return;
            ToggleLink(category, facilityDefName, targetDefName);
        }

        /// <summary>该设施对当前类别全部启用</summary>
        public void EnableAllTargets(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (!categories.TryGetValue(category, out var info))
            {
                linkedTargets[key] = new List<string>(); // 类别失效：显式断开，避免兜底原始链接
                return;
            }
            linkedTargets[key] = info.targets.Select(t => t.defName).ToList();
        }

        /// <summary>该设施对当前类别全部禁用（显式空列表，区别于「未配置」）</summary>
        public void DisableAllTargets(string category, string facilityDefName)
        {
            linkedTargets[MakeKey(category, facilityDefName)] = new List<string>();
        }

        /// <summary>获取当前生效连接列表的副本（用于复制）：未配置时回退原始链接</summary>
        public List<string> GetLinkedList(string category, string facilityDefName)
        {
            var key = MakeKey(category, facilityDefName);
            if (linkedTargets.TryGetValue(key, out var linked))
                return new List<string>(linked);
            if (originalLinks.TryGetValue(key, out var orig))
                return new List<string>(orig);
            return new List<string>();
        }

        /// <summary>设置某设施的启用目标列表（覆盖表，用于粘贴）</summary>
        public void SetLinkedList(string category, string facilityDefName, List<string> linked)
        {
            linkedTargets[MakeKey(category, facilityDefName)] =
                linked == null ? new List<string>() : new List<string>(linked);
        }

        /// <summary>回到原始状态：清空所有显式覆盖（linkedTargets），由 ShouldLink 兜底返回原始链接</summary>
        public void ResetToOriginal()
        {
            linkedTargets.Clear();
        }

        /// <summary>将单个设施重置到原始状态：移除其显式覆盖，由 ShouldLink 兜底返回原始链接</summary>
        public void ResetFacilityToOriginal(string category, string facilityDefName)
        {
            linkedTargets.Remove(MakeKey(category, facilityDefName));
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

        /// <summary>设置连接某主设施的设施列表（覆盖表，用于粘贴；列表外的设施全部断开）</summary>
        public void SetTargetLinkedList(string category, string targetDefName, List<string> linked)
        {
            if (!categories.TryGetValue(category, out var info)) return;
            linked ??= new List<string>();
            var facilityNames = info.facilities.Values.SelectMany(l => l).Distinct();
            foreach (var fn in facilityNames)
                SetLink(category, fn, targetDefName, linked.Contains(fn));
        }

        // 导出数据
        public override void ExposeData()
        {
            // 字段名 initialized 与旧版 initializedV2/V3 不同：旧存档读不到 → false，
            // 触发一次 ResetToOriginal（清空覆盖表），让三态语义从干净状态生效。
            Scribe_Collections.Look(ref linkedTargets, "linkedTargets",
                LookMode.Value, LookMode.Value, ref _keys, ref _values);
            Scribe_Values.Look(ref initialized, "initialized");

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
}
