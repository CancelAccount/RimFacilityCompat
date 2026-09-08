using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施注入应用器：单通道运行时声明式注入。
    /// 每个目标的最终 linkableFacilities ≡ 配置声明状态（类别下所有设施 ∩ ShouldLink 判定的集合，
    /// 未配置的设施回退到原始链接），语义为"整体重建"而非"增量追加"：增删皆可（原始链接也可断开）、
    /// 幂等（重复调用结果一致）、无重复条目、不落盘（每次启动 defs 自动回到 XML 原始状态，
    /// 扫描基准天然无污染）。设置界面任意操作后可直接调用 ApplyInjection，当次会话即时生效，无需重启。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class FacilityPatcher
    {
        /// <summary>
        /// 启动期快照：原版 XML 自带设施 comp 的目标 defName 集合。
        /// 用于区分"原版 comp"与"本 mod 运行时新建的 comp"——声明状态为空时，
        /// 原版 comp 仅清空列表（保留结构），自建 comp 整体移除（避免下次启动语义漂移）。
        /// </summary>
        private static HashSet<string> targetsWithOriginalComp = new();

        static FacilityPatcher()
        {
            LongEventHandler.ExecuteWhenFinished(Apply);
        }

        /// <summary>
        /// 启动流程（主菜单出现前执行）：
        /// 阶段一 扫描（干净 defs）→ 阶段二 首次运行初始化 → 阶段三 声明式注入 → 落盘。
        /// </summary>
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

                // 阶段一：扫描。运行时注入不落盘，defs 每次启动自动干净，
                // originalLinks 与连通分量分组天然基于真原始状态
                using (FCDebug.TimeScope("启动·扫描 ScanAll"))
                {
                    settings.categories = FacilityScanner.ScanAll();
                }
                using (FCDebug.TimeScope("启动·原始链接扫描 ScanOriginalLinks"))
                {
                    settings.originalLinks = ScanOriginalLinks(settings.categories);
                }
                using (FCDebug.TimeScope("启动·原版comp快照 SnapshotOriginalComps"))
                {
                    SnapshotOriginalComps(settings.categories);
                }

                // 阶段二：首次运行初始化（清空覆盖表，原始链接由 ShouldLink 兜底保留）
                if (!settings.initialized)
                {
                    settings.ResetToOriginal();
                    settings.initialized = true;
                }

                // 阶段三：声明式注入 + 落盘（确保 initialized 持久化）
                int applied;
                using (FCDebug.TimeScope("启动·声明式注入 ApplyInjection"))
                {
                    applied = ApplyInjection(settings);
                }
                using (FCDebug.TimeScope("启动·设置落盘 Write"))
                {
                    settings.Write();
                }
                FCLogger.Msg("FC.LogPatched", applied, settings.categories.Count);
            }
            catch (System.Exception ex)
            {
                FCLogger.Exception("FC.LogPatchFailed".Translate(), ex);
            }
        }

        /// <summary>
        /// 对所有目标重建设施链接：目标最终状态 ≡ 配置声明状态。
        /// 幂等，可反复调用（设置界面每次操作后调用，当次会话即时生效）。
        /// 返回实际重建（含新建 comp）的目标数。
        /// 同步维护设施侧反向索引：引擎的 CompProperties_Facility.linkableBuildings（设施 def 上
        /// "可连接的目标"列表）仅在 def 加载时由 ResolveReferences 反向填充一次，运行时修改目标的
        /// linkableFacilities 不会自动重算。设施侧全部入口（蓝图预览连线、设施放置/重装的CompFacility.LinkToNearbyBuildings）都读该反向索引，若不同步重建，补丁连接对设施侧不可见
        /// （表现为：放设施蓝图不画线、建成不连接，必须重放主设施才生效）。
        /// </summary>
        public static int ApplyInjection(FacilityCompatSettings settings)
        {
            int totalApplied = 0;
            // linkableFacilities 发生变化的目标所涉及的设施 def（旧∪新列表），注入后需重建其反向索引
            var affectedFacilities = new HashSet<ThingDef>();
            foreach (var kvp in settings.categories)
            {
                var category = kvp.Key;
                var info = kvp.Value;

                // 类别下全部设施 def（GetNamedSilentFail 过滤 mod 卸载后的失效 defName）
                var allFacilityDefs = info.facilities.Values
                    .SelectMany(x => x)
                    .Distinct()
                    .Select(fn => DefDatabase<ThingDef>.GetNamedSilentFail(fn))
                    .Where(def => def != null)
                    .Cast<ThingDef>()
                    .ToList();

                foreach (var targetDef in GetTargetDefs(info))
                {
                    // 该目标的最终链接 = 类别全部设施 ∩ ShouldLink 判定的
                    var finalList = allFacilityDefs
                        .Where(f => settings.ShouldLink(category, f.defName, targetDef.defName))
                        .ToList();

                    var comps = targetDef.comps
                        .OfType<CompProperties_AffectedByFacilities>()
                        .ToList();
                    var comp = comps.FirstOrDefault();
                    var oldList = comp?.linkableFacilities;

                    if (finalList.Count == 0)
                    {
                        // 声明状态为空：原版 comp 仅清空列表（保留结构），自建 comp 整体移除
                        if (comp != null)
                        {
                            bool hadLinks = oldList != null && oldList.Count > 0;

                            if (targetsWithOriginalComp.Contains(targetDef.defName))
                            {
                                // 原版 comp：仅当原本有链接时才清空（原本无链接则无变化）
                                if (hadLinks)
                                {
                                    foreach (var f in oldList!) affectedFacilities.Add(f);
                                    comp.linkableFacilities = new List<ThingDef>();
                                    totalApplied++;
                                }
                            }
                            else
                            {
                                // 自建 comp：整体移除（移除本身即变更）
                                if (hadLinks)
                                    foreach (var f in oldList!) affectedFacilities.Add(f);
                                foreach (var c in comps)
                                    targetDef.comps.Remove(c);
                                totalApplied++;
                            }
                        }
                        continue;
                    }

                    // 变更检测：内容未变化且非新建 comp 时跳过写入与计数
                    bool changed = !FacilityListEquals(oldList, finalList);
                    if (comp == null)
                    {
                        comp = new CompProperties_AffectedByFacilities();
                        targetDef.comps.Add(comp);
                        changed = true;
                    }

                    if (!changed) continue;

                    if (oldList != null)
                        foreach (var f in oldList) affectedFacilities.Add(f);
                    foreach (var f in finalList) affectedFacilities.Add(f);

                    comp.linkableFacilities = finalList; // 整体重建，非追加
                    totalApplied++;
                }
            }

            // 重建受影响设施的反向索引：复用引擎 ResolveReferences（全量扫描 defs，
            // 按"当前所有目标"的 linkableFacilities 反向重建 linkableBuildings，与其他 mod 的声明天然兼容）
            foreach (var facilityDef in affectedFacilities)
            {
                facilityDef.GetCompProperties<CompProperties_Facility>()?.ResolveReferences(facilityDef);
            }
            return totalApplied;
        }

        /// <summary>
        /// 设施链接列表相等性比较（集合语义，忽略顺序）。
        /// oldList 为 null 视为空集（此调用点 finalList 保证非空，故 null 必不等）。
        /// </summary>
        private static bool FacilityListEquals(List<ThingDef>? oldList, List<ThingDef> newList)
        {
            if (oldList == null || oldList.Count != newList.Count) return false;
            foreach (var f in oldList)
                if (!newList.Contains(f)) return false;
            return true;
        }

        /// <summary>
        /// 重连当前会话所有地图上受设施影响的建筑（Notify_ThingChanged → RelinkAll）。
        /// 须在 ApplyInjection 之后调用，使已放置建筑立即应用重建后的 defs；
        /// 设置界面期间游戏自动暂停，遍历重连无卡顿风险。
        /// 读档/新游戏/建筑生成时引擎经 PostSpawnSetup 自动重连
        /// （CompAffectedByFacilities 不持久化 linkedFacilities），本方法仅补"会话中修改配置"入口。
        /// </summary>
        public static void RelinkSpawnedThings()
        {
            if (Current.Game == null) return; // 主菜单阶段无游戏会话（启动期 Apply 不触发重连）
            int relinked = 0;
            foreach (var map in Find.Maps)
            {
                foreach (var thing in map.listerThings.AllThings)
                {
                    var comp = thing.TryGetComp<CompAffectedByFacilities>();
                    if (comp == null) continue;

                    // def 层 comp 已被还原（ApplyInjection 移除了本 mod 自建的 comp）→ 实例 comp 成为孤儿。
                    // 直接走引擎 RelinkAll 会崩：PotentialThingsToLinkTo 对 def.GetCompProperties
                    // 的结果不判空即访问 linkableFacilities，def 已无该 comp → NullReferenceException。
                    // 这里手动解除残留双向登记并从实例移除，使实例与 def 声明一致（此后引擎按普通建筑处理）。
                    if (thing.def.GetCompProperties<CompProperties_AffectedByFacilities>() == null)
                    {
                        foreach (var f in comp.LinkedFacilitiesListForReading)
                            f.TryGetComp<CompFacility>()?.Notify_LinkRemoved(thing);
                        comp.LinkedFacilitiesListForReading.Clear();
                        if (thing is ThingWithComps twc) // 有 comp 实例必为 ThingWithComps
                            twc.AllComps.Remove(comp);
                        continue;
                    }

                    comp.Notify_ThingChanged();
                    relinked++;
                }
            }
            if (relinked > 0)
                FCLogger.Msg("FC.LogRelinked", relinked);
        }

        /// <summary>
        /// 扫描器已经完成语义分类；后续阶段只解析它明确给出的目标，
        /// 避免再次按 thingClass 扩张，尤其不能把普通 Building 扩展为所有建筑。
        /// </summary>
        private static List<ThingDef> GetTargetDefs(CategoryInfo info)
        {
            return info.targets
                .Select(target => DefDatabase<ThingDef>.GetNamedSilentFail(target.defName))
                .Where(def => def != null)
                .Cast<ThingDef>()
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// 启动期快照：记录原版 XML 自带设施 comp 的目标（注入前调用）,全局重置时就用的它。
        /// </summary>
        private static void SnapshotOriginalComps(Dictionary<string, CategoryInfo> categories)
        {
            targetsWithOriginalComp = new HashSet<string>();
            foreach (var info in categories.Values)
            {
                foreach (var targetDef in GetTargetDefs(info))
                {
                    if (targetDef.comps.Any(c => c is CompProperties_AffectedByFacilities))
                        targetsWithOriginalComp.Add(targetDef.defName);
                }
            }
        }

        /// <summary>
        /// 扫描原始链接：记录每个设施在各类别中原本就链接了哪些目标
        /// </summary>
        public static Dictionary<string, List<string>> ScanOriginalLinks(Dictionary<string, CategoryInfo> categories)
        {
            var result = new Dictionary<string, List<string>>();
            // 遍历所有类别
            foreach (var ckv in categories)
            {
                var category = ckv.Key;
                var info = ckv.Value;
                // 遍历所有目标类别下的设施
                foreach (var targetDef in GetTargetDefs(info))
                {
                    var affectedByFacilities = targetDef.comps
                        .OfType<CompProperties_AffectedByFacilities>()
                        .FirstOrDefault();

                    if (affectedByFacilities?.linkableFacilities == null) continue;
                    // 遍历所有链接的设施
                    foreach (var facilityDef in affectedByFacilities.linkableFacilities)
                    {
                        if (facilityDef == null) continue;
                        var key = $"{category}|{facilityDef.defName}";
                        if (!result.ContainsKey(key))
                            result[key] = new List<string>();
                        if (!result[key].Contains(targetDef.defName))
                            result[key].Add(targetDef.defName);
                    }
                }
            }

            return result;
        }
    }
}
