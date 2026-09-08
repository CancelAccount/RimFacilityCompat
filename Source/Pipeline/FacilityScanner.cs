using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施扫描器：
    /// 1. 独立收集所有 CompProperties_Facility；
    /// 2. 收集所有 CompProperties_AffectedByFacilities 及其已有链接；
    /// 3. 已知建筑族按继承关系归类，普通 Building 按已有关系拆分，未引用设施单独列出。
    /// </summary>
    public static class FacilityScanner
    {
        private const string UnassignedCategory = "FacilityCompat.Unassigned";

        private sealed class KnownCategory
        {
            public readonly string typeName;
            public readonly string translationKey;

            public KnownCategory(string typeName, string translationKey)
            {
                this.typeName = typeName;
                this.translationKey = translationKey;
            }
        }

        /// <summary>
        /// 顺序从具体类型到宽泛类型，避免 ResearchBench/CryptoBed 等先被父类类别截获。
        /// </summary>
        private static readonly List<KnownCategory> KnownCategories = new()
        {
            // 低温休眠舱
            new KnownCategory("Building_CryptoBed", "FC.CatCryptoBed"),
            // 研究台
            new KnownCategory("Building_ResearchBench", "FC.CatResearchBench"),
            // 基因组装器
            new KnownCategory("Building_GeneAssembler", "FC.CatGeneAssembler"),
            // 逆重飞船
            new KnownCategory("Building_GravEngine", "FC.CatGravEngine"),
            // 收容平台
            new KnownCategory("Building_HoldingPlatform", "FC.CatHoldingPlatform"),
            // 床
            new KnownCategory("Building_Bed", "FC.CatBed"),
            // 工作台
            new KnownCategory("Building_WorkTable", "FC.CatWorkTable"),
        };

        /// <summary>DLC / Core 的 modContentPack.Name → 翻译 key 映射</summary>
        private static readonly Dictionary<string, string> OfficialModKeys = new()
        {
            ["Core"] = "FC.SourceCore",
            ["Royalty"] = "FC.SourceRoyalty",
            ["Ideology"] = "FC.SourceIdeology",
            ["Biotech"] = "FC.SourceBiotech",
            ["Anomaly"] = "FC.SourceAnomaly",
            ["Odyssey"] = "FC.SourceOdyssey",
        };

        /// <summary>
        /// 会话级类型缓存：Type.Name → Type（同名字以 DefDatabase 顺序首个为准，与未缓存实现语义一致）。
        /// 每次 ScanAll 开头置 null 重建；同一次扫描内被 ResolveType/ResolveBaseType 反复调用时避免全表遍历。
        /// </summary>
        private static Dictionary<string, Type>? typeCache;

        /// <summary>清空类型缓存（ScanAll 开头调用，保证下次扫描基于当前 DefDatabase 重建）</summary>
        private static void ResetTypeCache()
        {
            typeCache = null;
        }

        /// <summary>
        /// 扫描所有类别，返回 类别标识 → CategoryInfo。
        /// 已知类别继续使用原显示名作为标识，以兼容现有设置；动态普通建筑组使用稳定前缀。
        /// </summary>
        public static Dictionary<string, CategoryInfo> ScanAll()
        {
            ResetTypeCache(); // 类型缓存随本次扫描重建，保证基于当前 DefDatabase
            var result = new Dictionary<string, CategoryInfo>();
            var allDefs = DefDatabase<ThingDef>.AllDefs
                .Where(def => def?.thingClass != null)
                .ToList();

            var facilityDefs = allDefs
                .Where(HasFacilityComp)
                .ToList();

            var affectedDefs = allDefs
                .Where(HasAffectedByFacilitiesComp)
                .ToList();

            var targetLinks = affectedDefs.ToDictionary(
                def => def,
                GetValidLinkedFacilities);

            // 已收录目标登记表（defName）：保证每个目标只归入一个类别，
            // 防止同族扩展把更具体类别的目标重复收入宽泛类别（如 CryptoBed 再进 Bed）
            var claimedTargets = new HashSet<string>();

            // 已知建筑族和自定义非 Building 类型。
            foreach (var def in affectedDefs.Where(def => def.thingClass != typeof(Building)))
            {
                var category = ResolveTypedCategory(def.thingClass!);
                var info = GetOrCreateCategory(result, category.key, category.displayName,
                    category.baseType.Name);

                AddTarget(info, def);
                claimedTargets.Add(def.defName);
                foreach (var facilityDef in targetLinks[def])
                    AddFacility(info, facilityDef);
            }

            // 已知建筑族需要包含尚未声明设施 comp 的同族目标，才能实现跨 Mod 兼容。
            // 已被更具体类别收录的目标跳过（claimedTargets.Add 返回 false = 已存在）。
            foreach (var info in result.Values.Where(info => info.thingClassName != typeof(Building).Name))
            {
                var baseType = ResolveBaseType(info.thingClassName);
                if (baseType == null) continue;

                foreach (var def in allDefs.Where(def => baseType.IsAssignableFrom(def.thingClass!)))
                    if (claimedTargets.Add(def.defName))
                        AddTarget(info, def);
            }

            // 普通 Building 不能整体扩展；只按已有“目标—设施”关系的连通分量建组。
            AddGenericBuildingGroups(result,
                affectedDefs.Where(def => def.thingClass == typeof(Building)).ToList(),
                targetLinks);

            // 独立扫描得到但没有被任何目标引用的设施进入待分配区。
            var referencedFacilities = new HashSet<string>(
                targetLinks.Values
                    .SelectMany(list => list)
                    .Select(def => def.defName));

            var unassigned = facilityDefs
                .Where(def => !referencedFacilities.Contains(def.defName))
                .ToList();

            if (unassigned.Count > 0)
            {
                var info = GetOrCreateCategory(result, UnassignedCategory,
                    "FC.CatUnassigned".Translate(), "");
                foreach (var facilityDef in unassigned)
                    AddFacility(info, facilityDef);
            }

            foreach (var info in result.Values)
            {
                info.targets = info.targets
                    .OrderBy(target => target.label)
                    .ThenBy(target => target.defName)
                    .ToList();

                foreach (var source in info.facilities.Keys.ToList())
                    info.facilities[source] = info.facilities[source]
                        .Distinct()
                        .OrderBy(defName => defName)
                        .ToList();
            }

            return result;
        }

        private static bool HasFacilityComp(ThingDef def)
            => def.comps?.Any(comp => comp is CompProperties_Facility) == true;

        private static bool HasAffectedByFacilitiesComp(ThingDef def)
            => def.comps?.Any(comp => comp is CompProperties_AffectedByFacilities) == true;

        private static List<ThingDef> GetValidLinkedFacilities(ThingDef def)
        {
            return def.comps?
                .OfType<CompProperties_AffectedByFacilities>()
                .Where(comp => comp.linkableFacilities != null)
                .SelectMany(comp => comp.linkableFacilities)
                .Where(facilityDef => facilityDef != null)
                .GroupBy(facilityDef => facilityDef.defName)
                .Select(group => group.First())
                .ToList()
                ?? new List<ThingDef>();
        }

        /// <summary>按类型解析已知类别（如 Building、CryptoBed、Bed 等，目前在游戏中共 7 种）</summary>
        private static (string key, string displayName, Type baseType) ResolveTypedCategory(Type type)
        {
            // 从KnownCategories中查找匹配的类别(7种)
            var known = KnownCategories.FirstOrDefault(category =>
            {
                var knownType = ResolveType(category.typeName);
                return knownType != null && knownType.IsAssignableFrom(type);
            });
            // 如果找到匹配类别，返回该类别
            if (known != null)
            {
                var displayName = known.translationKey.Translate().ToString();
                // key 用稳定的类名标识（不随语言变化），displayName 仅用于 UI 展示
                return (known.typeName, displayName, ResolveType(known.typeName)!);
            }

            // 未知自定义类型维持原有按类名分组行为。
            return (type.Name, type.Name, type);
        }

        private static Type? ResolveBaseType(string className)
        {
            var known = KnownCategories.FirstOrDefault(category =>
                category.typeName == className);
            if (known != null)
                return ResolveType(known.typeName);

            return ResolveType(className);
        }

        /// <summary>按类名解析 Type（首次调用构建 Type.Name → Type 字典，之后 O(1) 查询）</summary>
        private static Type? ResolveType(string className)
        {
            if (typeCache == null)
            {
                typeCache = new Dictionary<string, Type>();
                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                {
                    var type = def.thingClass;
                    if (type != null && !typeCache.ContainsKey(type.Name))
                        typeCache[type.Name] = type;
                }
            }

            return typeCache.TryGetValue(className, out var result) ? result : null;
        }

        /// <summary>
        /// 普通 Building 目标按“共享设施”关系做连通分量分组。
        ///
        /// 问题模型：目标与设施构成二分图（目标 → 它引用的设施）。若两个目标引用了同一个设施，
        /// 它们就属于同一“圈子”（连通分量）。目标是求出所有圈子，每个圈子生成一个类别。
        ///
        /// 旧实现：每出队一个目标，就线性扫描一遍全部剩余目标（remaining.Where(...)）验证
        /// “谁的设施与当前圈子重叠”，最坏 O(B²·d)（B=目标数，d=每目标平均设施数）。
        ///
        /// 新实现：先把边反向建索引“设施 defName → 引用它的目标列表”（邻接表/倒排索引），
        /// BFS 时经索引 O(1) 直接拿到“和本目标同用某个设施的人”，复杂度降为 O(B·d + B)。
        /// 两者求的是同一传递闭包，分量划分结果一致。
        /// </summary>
        private static void AddGenericBuildingGroups(
            Dictionary<string, CategoryInfo> result,
            List<ThingDef> genericTargets,
            Dictionary<ThingDef, List<ThingDef>> targetLinks)
        {
            // ---- 第 1 步：建倒排索引（邻接表）----
            // facilityToTargets：设施 defName → 所有引用该设施的目标。
            // 效果：给定一个设施，O(1) 就能找出“全都有资格连它的目标们”。
            // （这正是旧实现靠全量扫描剩余目标才能得到的信息，这里一次性预先算好。）
            var facilityToTargets = new Dictionary<string, List<ThingDef>>();
            foreach (var target in genericTargets)
                foreach (var facilityDef in targetLinks[target])
                {
                    // 设施第一次出现 → 新建一个空列表挂到索引上（list 变量复用，避免二次查找）
                    if (!facilityToTargets.TryGetValue(facilityDef.defName, out var list))
                        facilityToTargets[facilityDef.defName] = list = new List<ThingDef>();
                    list.Add(target); // 把“引用该设施的目标”追加进邻接表
                }

            // ---- 第 2 步：待分组目标池 ----
            // remaining = 还没被划入任何圈子的目标。只有“挂了至少一个设施”的目标才参与连通
            // 分量：零设施目标会形成空圈子，导致下方取“字典序最小设施”时序列为空而崩溃。
            var remaining = new HashSet<ThingDef>(
                genericTargets.Where(def => targetLinks[def].Count > 0));

            // ---- 第 3 步：反复从池中捞种子，BFS 扩散出一个完整圈子 ----
            // 每次外层循环从 remaining 取任意一个目标作为新圈子的种子，
            // BFS 把与它直接/间接共享设施的所有目标全收进来，直到池空（全部划完）。
            while (remaining.Count > 0)
            {
                // 任取一个尚未分组的目标作为新圈子起点
                var first = remaining.First();
                remaining.Remove(first); // 立刻从池中移除 = 标记“已分组”，防重复入圈

                // 当前圈子（正在扩散的连通分量）的累计结果：
                //   componentTargets    —— 圈子包含的所有目标
                //   componentFacilities —— 圈子累计出现过的所有设施（按 defName 去重）
                var componentTargets = new List<ThingDef> { first };
                var componentFacilities = new Dictionary<string, ThingDef>();
                var queue = new Queue<ThingDef>();
                queue.Enqueue(first);

                // BFS：出队一个目标，把“与它共享某个设施”的其他目标也拉进圈子，再继续扩散
                while (queue.Count > 0)
                {
                    var target = queue.Dequeue();
                    // 遍历目标引用的每个设施（沿用 targetLinks 正向边）
                    foreach (var facilityDef in targetLinks[target])
                    {
                        // ① 登记：该设施属于本圈子（去重），供最后生成类别 + 收尾收集设施用
                        componentFacilities[facilityDef.defName] = facilityDef;

                        // ② 扩展：查倒排索引，找出“所有也引用这个设施的目标”＝本圈子的潜在邻居
                        if (!facilityToTargets.TryGetValue(facilityDef.defName, out var candidates))
                            continue; // 防御：理论上索引必然存在，跳过即可

                        foreach (var candidate in candidates)
                        {
                            // remaining.Remove 成功 = 它还没被任何圈子收走 →
                            // 并入当前圈子并加入待扩散队列；返回 false = 已入其他圈子，忽略
                            if (remaining.Remove(candidate))
                            {
                                componentTargets.Add(candidate);
                                queue.Enqueue(candidate);
                            }
                        }
                    }
                }

                // ---- 第 4 步：把扩散出的圈子落成一个类别 ----
                // anchor = 圈子内设施中字典序最小者，作为该普通建筑组的稳定标识
                var anchor = componentFacilities.Keys.OrderBy(defName => defName).First();
                var categoryKey = $"Building:{anchor}"; // 类别键形如 Building:NutrientPasteDispenser
                var displayName = ResolveBuildingGroupDisplayName(anchor);
                var info = GetOrCreateCategory(result, categoryKey, displayName, typeof(Building).Name);

                // 圈子的全部目标与全部设施登记进该类别
                foreach (var target in componentTargets)
                    AddTarget(info, target);
                foreach (var facilityDef in componentFacilities.Values)
                    AddFacility(info, facilityDef);
            }
        }

        /// <summary>
        /// 普通建筑组的显示名：优先查专用翻译键 FC.CatLinked.{defName}（如 Blackboard→教室、
        /// ShardBeacon→心灵仪式），无专用键时回退到通用「普通建筑设施组：{defName}」。
        /// Translate() 对不存在的 key 原样返回 key 本身，以此判断是否有专用翻译。
        /// </summary>
        private static string ResolveBuildingGroupDisplayName(string anchor)
        {
            var specificKey = $"FC.CatLinked.{anchor}";
            var translated = specificKey.Translate();
            return translated.ToString() != specificKey
                ? translated
                : string.Format("FC.CatLinkedBuilding".Translate(), anchor);
        }

        private static CategoryInfo GetOrCreateCategory(
            Dictionary<string, CategoryInfo> result,
            string categoryKey,
            string displayName,
            string thingClassName)
        {
            if (result.TryGetValue(categoryKey, out var info))
                return info;

            info = new CategoryInfo
            {
                displayName = displayName,
                thingClassName = thingClassName,
            };
            result[categoryKey] = info;
            return info;
        }

        private static void AddTarget(CategoryInfo info, ThingDef def)
        {
            if (!info.targets.Any(target => target.defName == def.defName))
                info.targets.Add(new BuildingTarget(def.defName, def.label, GetModSourceName(def)));
        }

        private static void AddFacility(CategoryInfo info, ThingDef facilityDef)
        {
            var source = GetModSourceName(facilityDef);
            if (!info.facilities.TryGetValue(source, out var facilities))
            {
                facilities = new List<string>();
                info.facilities[source] = facilities;
            }

            if (!facilities.Contains(facilityDef.defName))
                facilities.Add(facilityDef.defName);
        }

        private static string GetModSourceName(ThingDef def)
        {
            if (def.modContentPack == null)
                return "FC.SourceUnknown".Translate();

            if (def.modContentPack.IsOfficialMod
                && OfficialModKeys.TryGetValue(def.modContentPack.Name, out var key))
                return key.Translate();

            return def.modContentPack.Name;
        }
    }
}
