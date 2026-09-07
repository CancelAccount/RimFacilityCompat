using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RimWorld;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 配置预设的导入导出（v2 语义表，统一格式）。
    /// 预设只含 <Link Target="目标defName" Facility="设施defName"/> 的声明式白名单：
    /// 一条 Link = "该目标应当连接该设施"。导出按来源过滤当前启用连接；导入对每条 Link
    /// 增量启用（SetLink true），缺 def / 无设施 comp / 无类别归属的组合自动跳过，
    /// 避免在缺少对应 mod 时尝试连接不存在的 def。
    /// </summary>
    public static class FacilityPreset
    {
        /// <summary>作者预设预设文件名前缀（随发行只读）</summary>
        public const string RecommendedPrefix = "FC_Recommended_";
        /// <summary>用户预设文件名前缀</summary>
        public const string UserPrefix = "FC_Preset_";

        /// <summary>
        /// 导出当前启用连接为 v2 语义表。
        /// sources 为 null/空 = 全量；否则仅导出目标来源 ∈ sources 的目标当前启用连接。
        /// 无任何可导出连接时返回空串，由调用方提示。
        /// </summary>
        public static string Export(FacilityCompatSettings settings,
            ICollection<string>? sources = null, string? customName = null)
        {
            var dir = Path.Combine(settings.modDir, "Presets");
            Directory.CreateDirectory(dir);

            var name = string.IsNullOrEmpty(customName)
                ? BuildDefaultName(sources)
                : customName!;
            name = SanitizeFileName(name);
            if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) name += ".xml";
            var filePath = Path.Combine(dir, name);

            var links = CollectLinks(settings, sources);
            if (links.Count == 0) return "";

            var doc = new XDocument(new XElement("FacilityCompatPreset",
                new XAttribute("Version", "2"),
                new XAttribute("Name", NameAttribute(sources))));
            foreach (var (target, facility) in links)
                doc.Root!.Add(new XElement("Link",
                    new XAttribute("Target", target),
                    new XAttribute("Facility", facility)));
            doc.Save(filePath);
            return filePath;
        }

        /// <summary>统计按来源过滤的当前启用连接数（导出预览用）</summary>
        public static int CountLinks(FacilityCompatSettings settings, ICollection<string>? sources)
            => CollectLinks(settings, sources).Count;

        /// <summary>收集按来源过滤的当前启用连接（目标 ∈ sources 时的 (目标, 设施) 对）</summary>
        private static List<(string target, string facility)> CollectLinks(
            FacilityCompatSettings settings, ICollection<string>? sources)
        {
            var result = new List<(string target, string facility)>();
            foreach (var kvp in settings.categories)
            {
                var category = kvp.Key;
                var info = kvp.Value;

                // 目标按来源过滤（null/空集合 = 全量）
                var targets = (sources == null || sources.Count == 0)
                    ? info.targets
                    : info.targets.Where(t => sources.Contains(t.sourceMod)).ToList();

                var facilityNames = info.facilities.Values
                    .SelectMany(x => x)
                    .Distinct()
                    .ToList();

                foreach (var target in targets)
                {
                    foreach (var facilityName in facilityNames)
                    {
                        // 仅导出当前已启用的连接
                        if (!settings.ShouldLink(category, facilityName, target.defName))
                            continue;
                        result.Add((target.defName, facilityName));
                    }
                }
            }
            return result;
        }

        /// <summary>预设根元素 Name 属性（单一来源记其名，其余记 All，仅人类可读元数据）</summary>
        private static string NameAttribute(ICollection<string>? sources)
        {
            if (sources != null && sources.Count == 1)
                return sources.First();
            return "All";
        }

        /// <summary>
        /// 根据来源生成默认文件名。调试版一律使用作者预设前缀（FC_Recommended_），
        /// 单一来源用来源名、其余用时间戳作后缀；发行版一律落为用户备份前缀（FC_Preset_）。
        /// </summary>
        private static string BuildDefaultName(ICollection<string>? sources)
        {
            if (FCDebug.DebugBuild)
            {
                var suffix = (sources != null && sources.Count == 1)
                    ? SanitizeFileName(sources.First())
                    : DateTime.Now.ToString("yyyyMMdd_HHmmss");
                return $"{RecommendedPrefix}{suffix}";
            }
            return $"{UserPrefix}{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        /// <summary>
        /// 从文件导入 v2 语义表：对每条 Link 增量启用（SetLink true，幂等）。
        /// 校验：目标 def 存在、设施 def 存在且带 CompProperties_Facility、目标与设施同属某类别
        /// （否则注入引擎无法处理该连接）。任一不满足则跳过并计数。返回 (导入数, 跳过数)。
        /// </summary>
        public static (int imported, int skipped) Import(string filePath, FacilityCompatSettings settings)
        {
            if (!File.Exists(filePath)) return (0, 0);

            XDocument doc;
            try { doc = XDocument.Load(filePath); }
            catch { return (0, 0); }

            int imported = 0;
            int skipped = 0;

            foreach (var elem in doc.Root?.Elements("Link") ?? Enumerable.Empty<XElement>())
            {
                var target = (string?)elem.Attribute("Target");
                var facility = (string?)elem.Attribute("Facility");
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(facility))
                {
                    skipped++;
                    continue;
                }

                // 目标 def 缺失（未装对应种族 mod）→ 跳过
                var targetDef = DefDatabase<ThingDef>.GetNamedSilentFail(target!);
                if (targetDef == null) { skipped++; continue; }

                // 设施 def 缺失或非设施（未装对应设施 mod）→ 跳过
                var facilityDef = DefDatabase<ThingDef>.GetNamedSilentFail(facility!);
                if (facilityDef == null || facilityDef.GetCompProperties<CompProperties_Facility>() == null)
                { skipped++; continue; }

                // 目标与设施必须同属某类别的设施池（否则注入引擎无法处理）→ 否则跳过
                if (!TryResolveCategory(settings, target!, facility!, out var category))
                { skipped++; continue; }

                settings.SetLink(category, facility!, target!, true);
                imported++;
            }

            return (imported, skipped);
        }

        /// <summary>反查「目标所属类别」且「设施在该类别设施池」中，返回匹配的类别标识</summary>
        private static bool TryResolveCategory(FacilityCompatSettings settings,
            string target, string facility, out string category)
        {
            category = "";
            foreach (var kvp in settings.categories)
            {
                var info = kvp.Value;
                if (!info.targets.Any(t => t.defName == target)) continue;
                if (info.facilities.Values.SelectMany(x => x).Contains(facility))
                {
                    category = kvp.Key;
                    return true;
                }
            }
            return false;
        }

        /// <summary>列出 Presets 目录下所有预设文件（作者预设 + 用户）</summary>
        public static List<FileInfo> ListPresets(string modDir)
        {
            var dir = Path.Combine(modDir, "Presets");
            if (!Directory.Exists(dir)) return new List<FileInfo>();
            return new DirectoryInfo(dir).GetFiles("*.xml")
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>列出作者预设（FC_Recommended_*.xml）</summary>
        public static List<FileInfo> ListRecommended(string modDir)
        {
            return ListPresets(modDir)
                .Where(f => f.Name.StartsWith(RecommendedPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>列出用户预设（FC_Preset_*.xml）</summary>
        public static List<FileInfo> ListUserPresets(string modDir)
        {
            return ListPresets(modDir)
                .Where(f => f.Name.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>文件名消毒：替换路径分隔符与非法字符，防止路径穿越与非法文件名</summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
