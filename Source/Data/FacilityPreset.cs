using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 配置预设的导入导出：将排除列表导出为 XML，导入时自动过滤不合法定义
    /// </summary>
    public static class FacilityPreset
    {
        /// <summary>导出当前排除配置到 Presets 目录</summary>
        public static string Export(FacilityCompatSettings settings, string? customName = null)
        {
            var dir = Path.Combine(settings.modDir, "Presets");
            Directory.CreateDirectory(dir);
            var name = string.IsNullOrEmpty(customName)
                ? $"FC_Preset_{DateTime.Now:yyyyMMdd_HHmmss}"
                : customName!;
            if (!name.EndsWith(".xml")) name += ".xml";
            var filePath = Path.Combine(dir, name);

            var doc = new XDocument(new XElement("FacilityCompatPreset"));
            foreach (var kvp in settings.excludedTargets)
            {
                foreach (var target in kvp.Value)
                {
                    doc.Root!.Add(new XElement("Exclude",
                        new XAttribute("Key", kvp.Key),
                        new XAttribute("Target", target)));
                }
            }
            doc.Save(filePath);
            return filePath;
        }

        /// <summary>从文件导入，配对验证合法定义，返回 (导入数, 跳过数)</summary>
        public static (int imported, int skipped) Import(string filePath, FacilityCompatSettings settings)
        {
            if (!File.Exists(filePath)) return (0, 0);

            XDocument doc;
            try { doc = XDocument.Load(filePath); }
            catch { return (0, 0); }

            int imported = 0;
            int skipped = 0;

            foreach (var elem in doc.Root?.Elements("Exclude") ?? Enumerable.Empty<XElement>())
            {
                var key = (string?)elem.Attribute("Key");
                var target = (string?)elem.Attribute("Target");
                if (key is null or "" || target is null or "")
                {
                    skipped++;
                    continue;
                }

                // 解析 key: "类别|设施defName"
                var parts = key.Split('|');
                if (parts.Length != 2)
                {
                    skipped++;
                    continue;
                }
                var category = parts[0];
                var facility = parts[1];

                // 验证设施 def 存在
                var facilityDef = DefDatabase<ThingDef>.GetNamedSilentFail(facility);
                if (facilityDef == null) { skipped++; continue; }

                // 验证目标 def 存在
                var targetDef = DefDatabase<ThingDef>.GetNamedSilentFail(target);
                if (targetDef == null) { skipped++; continue; }

                // 验证类别中存在该目标
                if (!settings.categories.TryGetValue(category, out var info)) { skipped++; continue; }
                if (!info.targets.Any(t => t.defName == target)) { skipped++; continue; }

                // 添加到排除列表
                var mk = $"{category}|{facility}";
                if (!settings.excludedTargets.ContainsKey(mk))
                    settings.excludedTargets[mk] = new List<string>();
                if (!settings.excludedTargets[mk].Contains(target))
                {
                    settings.excludedTargets[mk].Add(target);
                    imported++;
                }
            }

            return (imported, skipped);
        }

        /// <summary>列出 Presets 目录下所有预设文件</summary>
        public static List<FileInfo> ListPresets(string modDir)
        {
            var dir = Path.Combine(modDir, "Presets");
            if (!Directory.Exists(dir)) return new List<FileInfo>();
            return new DirectoryInfo(dir).GetFiles("FC_Preset_*.xml")
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }
    }
}
