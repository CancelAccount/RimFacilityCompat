using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 预设管理对话框：替代原先挤在右下角的双层 FloatMenu，集中承载导入/导出与全局工具。
    /// 导出区：来源多选（全部来源 / 各 mod 来源勾选行）+ 文件名 + 连接数预览；
    /// 导入区：官方推荐 / 用户预设分列可滚动（用户预设可删除）+ 手动路径导入 + 打开预设文件夹；
    /// 全局行：全部重置；调试开关仅 DebugBuild 时显示。
    /// </summary>
    public class Dialog_PresetManager : Window
    {
        // 布局常量（集中定义，避免魔法数字散落）
        private const float Gap = 8f;
        private const float RowGap = 4f;
        private const float RowH = 30f;
        private const float SectionTitleH = 30f;
        private const float BtnH = 30f;
        private const float SourceBtnH = 28f;
        private const float SourceBtnGap = 6f;
        private const float SmallBtnW = 64f;
        private const float MidBtnW = 120f;
        private const float ScrollBarW = 16f;
        private const float LabelW = 84f;

        // 配色常量
        private static readonly Color TitleColor = new(0.95f, 0.85f, 0.5f);
        private static readonly Color SectionLineColor = new(1f, 1f, 1f, 0.25f);
        private static readonly Color DimColor = new(0.7f, 0.7f, 0.7f);

        private readonly FacilityCompatSettings settings;

        // 来源选择（导出）：allSelected = 全量；否则取 selected
        private readonly List<string> allSources = new();
        private readonly HashSet<string> selected = new();
        private bool allSelected = true;

        // 文件名（导出）
        private string fileNameText = "";
        private string lastAutoName = "";

        // 导入列表
        private List<FileInfo> recommended = new();
        private List<FileInfo> userPresets = new();
        private Vector2 importScroll = Vector2.zero;
        private string manualPathText = "";

        /// <summary>构建对话框；settings 为主窗口同一 ModSettings 单例引用</summary>
        public Dialog_PresetManager(FacilityCompatSettings settings)
        {
            this.settings = settings;
            doCloseX = true;
            draggable = true;
            resizeable = true;
            closeOnAccept = false;

            // 来源列表：当前扫描类目中全部目标来源，去重升序
            allSources.AddRange(settings.categories.Values
                .SelectMany(info => info.targets.Select(t => t.sourceMod))
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

            OnSourceChanged(); // 初始化默认文件名
            RefreshPresetLists();
        }

        /// <summary>初始尺寸：中号可缩放窗口，随屏幕上限收敛</summary>
        public override Vector2 InitialSize => new(
            Mathf.Min(UI.screenWidth * 0.7f, 860f),
            Mathf.Min(UI.screenHeight * 0.8f, 760f));

        // ==================== 数据与动作 ====================

        /// <summary>当前勾选来源集合（null = 全量），供导出/预览调用</summary>
        private ICollection<string>? ChosenSources() => allSelected ? null : selected;

        /// <summary>刷新官方推荐 / 用户预设列表（构造、导出、删除后调用）</summary>
        private void RefreshPresetLists()
        {
            recommended = FacilityPreset.ListRecommended(settings.modDir);
            userPresets = FacilityPreset.ListUserPresets(settings.modDir);
        }

        /// <summary>依据来源选择生成默认文件名。调试版一律用作者预设前缀，发行版一律用用户备份前缀</summary>
        private string BuildAutoName()
        {
            if (FCDebug.DebugBuild)
            {
                var suffix = (!allSelected && selected.Count == 1)
                    ? selected.First()
                    : DateTime.Now.ToString("yyyyMMdd_HHmmss");
                return $"{FacilityPreset.RecommendedPrefix}{suffix}";
            }
            return $"{FacilityPreset.UserPrefix}{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        /// <summary>来源选择变化：除非用户已手动改名，否则刷新默认文件名</summary>
        private void OnSourceChanged()
        {
            var auto = BuildAutoName();
            if (fileNameText.Length == 0 || fileNameText == lastAutoName)
                fileNameText = auto;
            lastAutoName = auto;
        }

        /// <summary>点击单个来源：从「全部」切换为单选，或在多选间增删</summary>
        private void ToggleSource(string source)
        {
            if (allSelected)
            {
                allSelected = false;
                selected.Clear();
                selected.Add(source);
            }
            else if (!selected.Remove(source))
            {
                selected.Add(source);
            }
            OnSourceChanged();
        }

        /// <summary>点击「全部来源」：恢复全量语义并清空子项勾选</summary>
        private void SelectAllSources()
        {
            allSelected = true;
            selected.Clear();
            OnSourceChanged();
        }

        /// <summary>应用并保存：声明式重建 → 重连已放置建筑 → 落盘（与主窗口入口同构，供导入后即时生效）</summary>
        private void ApplyAndSave()
        {
            FacilityPatcher.ApplyInjection(settings);
            FacilityPatcher.RelinkSpawnedThings();
            settings.Write();
        }

        /// <summary>导出当前勾选来源的启用连接到用户预设文件</summary>
        private void DoExport()
        {
            var path = FacilityPreset.Export(settings, ChosenSources(), fileNameText);
            if (string.IsNullOrEmpty(path))
            {
                Messages.Message("FC.MsgExportEmpty".Translate(), MessageTypeDefOf.RejectInput);
                FCLogger.Warn("FC.MsgExportEmpty");
                return;
            }
            RefreshPresetLists(); // 新文件出现在用户列表
            Messages.Message(string.Format("FC.MsgExported".Translate(), path),
                MessageTypeDefOf.NeutralEvent);
            FCLogger.Msg("FC.MsgExported", path);
        }

        /// <summary>导入指定预设文件（缺 def 的组合自动跳过）</summary>
        private void DoImport(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Messages.Message("FC.MsgFileMissing".Translate(filePath),
                    MessageTypeDefOf.RejectInput);
                FCLogger.Warn("FC.MsgFileMissing", filePath);
                return;
            }
            var (imported, skipped) = FacilityPreset.Import(filePath, settings);
            ApplyAndSave();
            Messages.Message(string.Format("FC.MsgImported".Translate(), imported, skipped),
                MessageTypeDefOf.NeutralEvent);
            FCLogger.Msg("FC.MsgImported", imported, skipped);
        }

        /// <summary>删除用户预设（先弹确认）</summary>
        private void DoDelete(FileInfo file)
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                string.Format("FC.MsgDeleteConfirm".Translate(), file.Name),
                delegate
                {
                    File.Delete(file.FullName);
                    RefreshPresetLists();
                    Messages.Message(string.Format("FC.MsgDeleted".Translate(), file.Name),
                        MessageTypeDefOf.NeutralEvent);
                    FCLogger.Msg("FC.MsgDeleted", file.Name);
                }));
        }

        /// <summary>全局重置：全部回到原始链接</summary>
        private void DoResetAll()
        {
            settings.ResetToOriginal();
            ApplyAndSave();
            Messages.Message("FC.MsgResetAllDone".Translate(), MessageTypeDefOf.NeutralEvent);
            FCLogger.Msg("FC.MsgResetAllDone");
        }

        /// <summary>打开预设文件夹</summary>
        private void OpenPresetFolder()
        {
            var dir = Path.Combine(settings.modDir, "Presets");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }

        // ==================== 绘制 ====================

        /// <summary>分区标题（金标 + 底部分隔线）</summary>
        private static void DrawSectionTitle(Rect rect, string text)
        {
            var prev = GUI.color;
            GUI.color = TitleColor;
            Text.Font = GameFont.Medium;
            Widgets.Label(rect, text);
            Text.Font = GameFont.Small;
            GUI.color = prev;
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), SectionLineColor);
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 每帧刷新预览（用户可能回到主矩阵改连接后直接在此导出）
            int previewLinks = FacilityPreset.CountLinks(settings, ChosenSources());

            float w = inRect.width;
            float x = inRect.x;
            float y = inRect.y;

            // ---- 导出区 ----
            DrawSectionTitle(new Rect(x, y, w, SectionTitleH), "FC.PresetExportTitle".Translate());
            y += SectionTitleH + RowGap;

            // 来源选择（wrap 按钮行：首钮「全部来源」开关，其后为各 mod 来源；按钮宽度按文本自适应）
            float srcX = x;
            float srcRowBaseY = y;
            float srcRowH = SourceBtnH;

            // 「全部来源」开关
            float allBtnW = Text.CalcSize("FC.ExportAllSources".Translate()).x + 24f;
            var allRect = new Rect(srcX, srcRowBaseY, allBtnW, SourceBtnH);
            if (Widgets.ButtonText(allRect, "FC.ExportAllSources".Translate(),
                    active: !allSelected))
            {
                SelectAllSources();
                return;
            }
            if (allSelected)
                Widgets.DrawBox(allRect, 2);
            TooltipHandler.TipRegion(allRect, "FC.ExportAllSourcesTip".Translate());
            srcX += allBtnW + SourceBtnGap;

            foreach (var source in allSources)
            {
                // 按钮宽度随 mod 名长度自适应，避免长名被截断
                float btnW = Text.CalcSize(source).x + 24f;
                if (srcX + btnW > x + w - ScrollBarW && srcX > x)
                {
                    srcX = x;
                    srcRowBaseY += SourceBtnH + RowGap;
                    srcRowH += SourceBtnH + RowGap;
                }
                // 来源按钮始终可点：点击即从「全部」切换为该来源（或多选增删）
                if (Widgets.ButtonText(new Rect(srcX, srcRowBaseY, btnW, SourceBtnH), source))
                {
                    ToggleSource(source);
                    return; // 触发后立即结束本帧绘制，避免状态变更后继续用旧值布局
                }
                if (!allSelected && selected.Contains(source))
                    Widgets.DrawBox(new Rect(srcX, srcRowBaseY, btnW, SourceBtnH), 2);
                srcX += btnW + SourceBtnGap;
            }
            y = srcRowBaseY + srcRowH + RowGap;

            // 文件名行：标签 + 输入 + 导出按钮
            var nameLabelRect = new Rect(x, y, LabelW, BtnH);
            Widgets.Label(nameLabelRect, "FC.PresetFileNameLabel".Translate());
            var nameFieldRect = new Rect(x + LabelW, y, w - LabelW - MidBtnW - Gap, BtnH);
            var newName = Widgets.TextField(nameFieldRect, fileNameText);
            if (newName != fileNameText)
            {
                fileNameText = newName;
                lastAutoName = ""; // 用户手动编辑后不再自动覆盖
            }
            var exportBtnRect = new Rect(x + w - MidBtnW, y, MidBtnW, BtnH);
            if (Widgets.ButtonText(exportBtnRect, "FC.BtnExport".Translate()))
            {
                DoExport();
                return;
            }
            y += BtnH + RowGap;

            // 预览行（高度用 Text.LineHeight，避免文字下半被裁剪）
            var prev = GUI.color;
            GUI.color = DimColor;
            Widgets.Label(new Rect(x, y, w, Text.LineHeight),
                string.Format("FC.PresetPreviewFmt".Translate(),
                    previewLinks,
                    (allSelected ? settings.categories.Values.Sum(i => i.targets.Count)
                        : settings.categories.Values.Sum(i => i.targets.Count(t => selected.Contains(t.sourceMod))))));
            GUI.color = prev;
            y += Text.LineHeight + RowGap;

            // 分隔
            y += RowGap;

            // ---- 导入区 ----
            DrawSectionTitle(new Rect(x, y, w, SectionTitleH), "FC.PresetImportTitle".Translate());
            y += SectionTitleH;

            // 两分区滚动列表高度：至底部全局行上方（预留标题 + 按钮行 + 三段行距，避免全局工具被挤出窗口）
            float listBottom = inRect.yMax - (SectionTitleH + BtnH + RowGap * 3f);
            float listH = listBottom - y;
            var listRect = new Rect(x, y, w, listH);
            DrawImportList(listRect);
            y += listH + RowGap;

            // ---- 全局工具行 ----
            DrawSectionTitle(new Rect(x, y, w, SectionTitleH), "FC.PresetGlobalTitle".Translate());
            y += SectionTitleH + RowGap;
            var resetBtnRect = new Rect(x, y, MidBtnW, BtnH);
            if (Widgets.ButtonText(resetBtnRect, "FC.BtnResetAll".Translate()))
            {
                DoResetAll();
                return;
            }
            // 调试开关（仅调试版；悬停说明开发者用途，常规发行界面不显示）
            if (FCDebug.DebugBuild)
            {
                var dbgX = x + MidBtnW + Gap * 2f;
                var dbgW = (w - (x - inRect.x) - (MidBtnW + Gap * 2f) - MidBtnW) / 2f;
                if (Widgets.ButtonText(new Rect(dbgX, y, dbgW, BtnH),
                        (FCDebug.EnableLog ? "✓ " : "× ") + "FC.DbgLog".Translate()))
                    FCDebug.EnableLog = !FCDebug.EnableLog;
                if (Widgets.ButtonText(new Rect(dbgX + dbgW + Gap, y, dbgW, BtnH),
                        (FCDebug.EnableDraw ? "✓ " : "× ") + "FC.DbgDraw".Translate()))
                    FCDebug.EnableDraw = !FCDebug.EnableDraw;
            }
            y += BtnH + RowGap;
        }

        /// <summary>预设文件展示名：省略 FC_Recommended_ / FC_Preset_ 前缀（完整路径仍由 tooltip 提供）</summary>
        private static string DisplayName(string fileName)
        {
            if (fileName.StartsWith(FacilityPreset.RecommendedPrefix, StringComparison.OrdinalIgnoreCase))
                return fileName.Substring(FacilityPreset.RecommendedPrefix.Length);
            if (fileName.StartsWith(FacilityPreset.UserPrefix, StringComparison.OrdinalIgnoreCase))
                return fileName.Substring(FacilityPreset.UserPrefix.Length);
            return fileName;
        }

        /// <summary>绘制导入列表（官方推荐 / 用户预设分区，行内 [导入]，用户行追加 [删除]）</summary>
        private void DrawImportList(Rect listRect)
        {
            // 有序渲染项：分区标题行 / 预设文件行（header=分区标题，file=文件）
            var items = new List<(string? header, (FileInfo file, bool user)? row)>();
            if (recommended.Count > 0)
            {
                items.Add(("FC.ImportOfficialHeader".Translate(), null));
                foreach (var f in recommended)
                    items.Add((null, (f, false)));
            }
            if (userPresets.Count > 0)
            {
                items.Add(("FC.ImportUserHeader".Translate(), null));
                foreach (var f in userPresets)
                    items.Add((null, (f, true)));
            }
            if (items.Count == 0)
                items.Add(("FC.PresetEmptyAll".Translate(), null));

            float deleteColX = listRect.xMax - SmallBtnW - ScrollBarW; // 删除列（仅用户行）
            float importColX = deleteColX - MidBtnW - Gap;              // 导入列
            float fileColX = listRect.xMax - ScrollBarW - SmallBtnW * 2f - MidBtnW - Gap * 2f;

            const float headerH = 22f;
            float contentH = items.Count * RowH + Gap + BtnH + RowGap + Gap;
            var contentRect = new Rect(0f, 0f, listRect.width - ScrollBarW,
                Mathf.Max(contentH, listRect.height));

            // 点击动作延迟到 EndScrollView 之后执行：若在 BeginScrollView 内提前 return，
            // 会跳过 EndScrollView，导致 Begin/End 不配对（"Mouse position stack is not empty" 报错）。
            Action? pendingAction = null;

            Widgets.BeginScrollView(listRect, ref importScroll, contentRect);
            float y = 0f;
            foreach (var (header, row) in items)
            {
                if (header != null)
                {
                    var prev = GUI.color;
                    GUI.color = DimColor;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(0f, y, contentRect.width, headerH), header);
                    Text.Font = GameFont.Small;
                    GUI.color = prev;
                    y += headerH;
                    continue;
                }

                var lineRect = new Rect(0f, y, contentRect.width, RowH);
                if (Mouse.IsOver(lineRect))
                    Widgets.DrawHighlight(lineRect);

                var presetFile = row!.Value.file; // header==null 分支保证文件行存在
                var nameRect = new Rect(lineRect.x, lineRect.y, fileColX - lineRect.x, RowH);
                Widgets.Label(nameRect, DisplayName(presetFile.Name));
                TooltipHandler.TipRegion(nameRect, presetFile.FullName);

                if (Widgets.ButtonText(new Rect(importColX, lineRect.y, MidBtnW, RowH - 4f),
                        "FC.BtnImport".Translate()))
                {
                    var f = presetFile;
                    pendingAction = () => DoImport(f.FullName);
                    break;
                }
                if (row.Value.user)
                {
                    if (Widgets.ButtonText(new Rect(deleteColX, lineRect.y, SmallBtnW, RowH - 4f),
                            "FC.BtnDelete".Translate()))
                    {
                        var f = presetFile;
                        pendingAction = () => DoDelete(f);
                        break;
                    }
                }
                y += RowH;
            }

            // 底部工具行：手动路径导入 + 打开文件夹（仅无行内点击时绘制）
            if (pendingAction == null)
            {
                y += Gap;
                // 按钮宽度随文字自适应，避免「手动输入路径…」被截断
                float manualBtnW = Text.CalcSize("FC.ImportManual".Translate()).x + 24f;
                var manualBtnRect = new Rect(0f, y, manualBtnW, BtnH);
                if (Widgets.ButtonText(manualBtnRect, "FC.ImportManual".Translate())
                    && !string.IsNullOrEmpty(manualPathText))
                {
                    pendingAction = () => DoImport(manualPathText);
                }
                var manualFieldRect = new Rect(manualBtnRect.xMax + Gap, y,
                    contentRect.width - manualBtnRect.xMax - Gap - MidBtnW, BtnH);
                manualPathText = Widgets.TextField(manualFieldRect, manualPathText);
                var folderBtnRect = new Rect(contentRect.width - MidBtnW, y, MidBtnW, BtnH);
                if (Widgets.ButtonText(folderBtnRect, "FC.ImportOpenFolder".Translate()))
                    OpenPresetFolder();
            }

            Widgets.EndScrollView();
            pendingAction?.Invoke();
        }
    }
}
