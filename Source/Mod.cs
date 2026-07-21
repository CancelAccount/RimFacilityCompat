using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施兼容补丁 Mod 主入口。设置界面：类别→设施→目标，含保存/重置。
    /// </summary>
    public class FacilityCompatMod : Mod
    {
        public static FacilityCompatSettings Settings = null!;

        private List<string> expandedCategories = new();
        private Dictionary<string, List<string>> expandedGroups = new();
        private List<string> manualExpanded = new();
        private Vector2 scrollPos = Vector2.zero;

        private const float BtnW = 60f;
        private const float BtnIconW = 22f;
        private const float BtnSp = 3f;
        private const float RowH = 26f;
        /// <summary>文本按钮数量（全部启用/禁用/手动/重置）</summary>
        private const int TextBtnCount = 4;
        /// <summary>图标按钮数量（复制/粘贴）</summary>
        private const int IconBtnCount = 2;
        /// <summary>按钮总数量</summary>
        private const int BtnCnt = TextBtnCount + IconBtnCount;
        /// <summary>所有按钮占用的总宽度</summary>
        private static float TotalBtnWidth => BtnW * TextBtnCount + BtnIconW * IconBtnCount + BtnSp * (BtnCnt - 1);

        private static readonly Color CategoryColor = new(0.3f, 0.55f, 0.95f);
        private static readonly Color SourceGroupColor = new(0.95f, 0.55f, 0.2f);
        private static readonly Color TargetGroupColor = new(0.55f, 0.7f, 0.35f);

        public FacilityCompatMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<FacilityCompatSettings>();
            Settings.modDir = content.RootDir;
        }

        public override string SettingsCategory() => "FC.SettingsCategory".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("FC.Title".Translate());
            listing.GapLine();

            if (!Settings.scanCompleted || Settings.categories.Count == 0)
            {
                listing.Label("FC.NoData".Translate());
                listing.End();
                return;
            }

            // 顶部按钮行
            var topRect = listing.GetRect(30f);
            var btnAll = new Rect(topRect.x, topRect.y + 3f, 150f, 24f);
            var btnReset = new Rect(topRect.x + 160f, topRect.y + 3f, 90f, 24f);
            var btnExport = new Rect(topRect.x + 258f, topRect.y + 3f, 60f, 24f);
            var btnImport = new Rect(topRect.x + 322f, topRect.y + 3f, 60f, 24f);
            var btnSave = new Rect(topRect.width - 150f, topRect.y + 3f, 140f, 24f);

            if (Widgets.ButtonText(btnAll, "FC.BtnEnableAll".Translate()))
                Settings.EnableAll();
            if (Widgets.ButtonText(btnReset, "FC.BtnResetAll".Translate()))
            {
                Settings.ResetToOriginal();
                manualExpanded.Clear();
            }
            // 导出按钮
            if (Widgets.ButtonText(btnExport, "FC.BtnExport".Translate()))
            {
                var defaultName = $"FC_Preset_{System.DateTime.Now:yyyyMMdd_HHmmss}";
                Find.WindowStack.Add(new Dialog_TextInput(defaultName,
                    "FC.DlgExportMsg".Translate(),
                    (name) =>
                    {
                        var path = FacilityPreset.Export(Settings, name);
                        Messages.Message(string.Format("FC.MsgExported".Translate(), path),
                            MessageTypeDefOf.NeutralEvent);
                    }, 60));
            }
            // 导入按钮
            if (Widgets.ButtonText(btnImport, "FC.BtnImport".Translate()))
            {
                var options = new List<FloatMenuOption>();

                // 手动输入路径
                options.Add(new FloatMenuOption("FC.ImportManual".Translate(), delegate
                {
                    Find.WindowStack.Add(new Dialog_TextInput("",
                        "FC.DlgImportMsg".Translate(),
                        (path) =>
                        {
                            var (imported, skipped) = FacilityPreset.Import(path, Settings);
                            Settings.Write();
                            Messages.Message(
                                string.Format("FC.MsgImported".Translate(), imported, skipped),
                                MessageTypeDefOf.NeutralEvent);
                        }, 200));
                }));

                // 预设文件列表
                var presets = FacilityPreset.ListPresets(Settings.modDir);
                foreach (var file in presets)
                {
                    var f = file;
                    options.Add(new FloatMenuOption(f.Name, delegate
                    {
                        var (imported, skipped) = FacilityPreset.Import(f.FullName, Settings);
                        Settings.Write();
                        Messages.Message(
                            string.Format("FC.MsgImported".Translate(), imported, skipped),
                            MessageTypeDefOf.NeutralEvent);
                    }));
                }

                // 打开预设文件夹
                options.Add(new FloatMenuOption("FC.ImportOpenFolder".Translate(), delegate
                {
                    var dir = System.IO.Path.Combine(Settings.modDir, "Presets");
                    System.IO.Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }));

                Find.WindowStack.Add(new FloatMenu(options));
            }
            if (Widgets.ButtonText(btnSave, "FC.BtnSave".Translate()))
            {
                Settings.SaveForXpath();
                Settings.Write();
                XpathGenerator.Generate(Settings); // 保存时生成 xpath 补丁
                Messages.Message("FC.MsgSaved".Translate(), MessageTypeDefOf.NeutralEvent);
            }

            // C# 运行时二次注入开关
            var chkRect = listing.GetRect(24f);
            bool rtPatch = Settings.enableRuntimePatch;
            Widgets.CheckboxLabeled(chkRect, "FC.OptRuntimePatch".Translate(), ref rtPatch);
            if (rtPatch != Settings.enableRuntimePatch)
            {
                Settings.enableRuntimePatch = rtPatch;
                Settings.Write();
            }
            listing.Gap(6f);

            // 滚动区域
            float contentH = CalcHeight();
            float availH = inRect.height - listing.CurHeight - 10f;
            Widgets.BeginScrollView(listing.GetRect(availH), ref scrollPos,
                new Rect(0f, 0f, inRect.width - 20f, Mathf.Max(contentH, availH)));
            var sl = new Listing_Standard();
            sl.Begin(new Rect(0f, 0f, inRect.width - 20f, Mathf.Max(contentH, availH)));

            foreach (var catKvp in Settings.categories.OrderBy(x => x.Key))
                DrawCategory(sl, catKvp.Key, catKvp.Value);

            sl.End();
            Widgets.EndScrollView();

            listing.End();
        }

        private void DrawCategory(Listing_Standard listing, string category, CategoryInfo info)
        {
            bool catExp = expandedCategories.Contains(category);
            string targetsStr = string.Format("FC.CatTargetCount".Translate(), info.targets.Count);
            string facilitiesStr = string.Format("FC.CatFacilityCount".Translate(), TotalFacilityCount(info));
            string header = $"[{(catExp ? "v" : ">")}] {category} ({targetsStr}, {facilitiesStr})";

            var prevColor = GUI.color;
            GUI.color = CategoryColor;
            if (listing.ButtonText(header))
            {
                if (catExp) expandedCategories.Remove(category);
                else expandedCategories.Add(category);
            }
            GUI.color = prevColor;
            if (!catExp) return;

            foreach (var srcKvp in info.facilities.OrderBy(x => x.Key))
                DrawSourceGroup(listing, category, srcKvp.Key, srcKvp.Value);
        }

        private void DrawSourceGroup(Listing_Standard listing, string category, string source, List<string> facilities)
        {
            if (!expandedGroups.ContainsKey(category))
                expandedGroups[category] = new List<string>();
            bool exp = expandedGroups[category].Contains(source);
            string hdr = $"  [{(exp ? "v" : ">")}] {source} ({facilities.Count})";

            var prevColor = GUI.color;
            GUI.color = SourceGroupColor;
            if (listing.ButtonText(hdr))
            {
                if (exp) expandedGroups[category].Remove(source);
                else expandedGroups[category].Add(source);
            }
            GUI.color = prevColor;
            if (!exp) return;

            foreach (var fn in facilities.OrderBy(x => x))
                DrawFacilityRow(listing, category, fn);
        }

        private void DrawFacilityRow(Listing_Standard listing, string category, string fn)
        {
            var mode = Settings.GetMode(category, fn);
            var rowRect = listing.GetRect(RowH);

            // 查找设施的中文名
            string facilityLabel = fn;
            var facilityDef = DefDatabase<ThingDef>.GetNamedSilentFail(fn);
            if (facilityDef != null && fn != facilityDef.label)
                facilityLabel = $"{fn} ({facilityDef.label})";

            float labelWidth = rowRect.width - TotalBtnWidth - 10f;
            var labelRect = new Rect(rowRect.x, rowRect.y, labelWidth, rowRect.height);
            Widgets.Label(labelRect, $"    {facilityLabel}");

            float bx = rowRect.x + labelWidth + 10f;
            var br1 = new Rect(bx, rowRect.y, BtnW, 22f);
            var br2 = new Rect(bx + BtnW + BtnSp, rowRect.y, BtnW, 22f);
            var br3 = new Rect(bx + (BtnW + BtnSp) * 2, rowRect.y, BtnW, 22f);
            var br4 = new Rect(bx + (BtnW + BtnSp) * 3, rowRect.y, BtnW, 22f);
            float iconX = bx + (BtnW + BtnSp) * TextBtnCount;
            var br5 = new Rect(iconX, rowRect.y + 2f, BtnIconW, 18f);
            var br6 = new Rect(iconX + BtnIconW + BtnSp, rowRect.y + 2f, BtnIconW, 18f);

            if (Widgets.ButtonText(br1, "FC.BtnAllOn".Translate(), active: mode != FacilityMode.All))
                Settings.EnableAllTargets(category, fn);
            if (Widgets.ButtonText(br2, "FC.BtnAllOff".Translate(), active: mode != FacilityMode.None))
                Settings.DisableAllTargets(category, fn);

            string mk = $"{category}|{fn}";
            bool openFlag = manualExpanded.Contains(mk);
            string manualLabel = openFlag ? "FC.BtnCollapse".Translate() : "FC.BtnManual".Translate();
            if (Widgets.ButtonText(br3, manualLabel, active: true))
            {
                if (openFlag) manualExpanded.Remove(mk);
                else manualExpanded.Add(mk);
            }
            if (Widgets.ButtonText(br4, "FC.BtnReset".Translate()))
            {
                Settings.ResetFacilityToOriginal(category, fn);
            }

            // 复制按钮（内置图标）
            TooltipHandler.TipRegion(br5, "FC.BtnCopy".Translate());
            if (Widgets.ButtonImage(br5, TexButton.Copy))
            {
                var excluded = Settings.GetExcludedList(category, fn);
                FacilityClipboard.Copy(category, fn, mode, excluded);
                Messages.Message(string.Format("FC.MsgCopied".Translate(), fn, category),
                    MessageTypeDefOf.NeutralEvent);
            }
            // 粘贴按钮（内置图标，无数据时灰色）
            TooltipHandler.TipRegion(br6, "FC.BtnPaste".Translate());
            if (FacilityClipboard.hasData)
            {
                if (Widgets.ButtonImage(br6, TexButton.Paste))
                {
                    var oldMode = Settings.GetMode(category, fn);
                    int filtered = FacilityClipboard.Paste(category, fn, Settings,
                        out var pastedMode, out var pastedExcluded);

                    // Manual 模式下所有排除项在目标类别中无匹配 → 不做更改
                    if (pastedMode == oldMode && filtered > 0)
                    {
                        Messages.Message(string.Format("FC.MsgPasteNoMatch".Translate(), fn),
                            MessageTypeDefOf.RejectInput);
                    }
                    else
                    {
                        Settings.SetModeAndExcluded(category, fn, pastedMode, pastedExcluded);
                        if (filtered > 0)
                            Messages.Message(string.Format("FC.MsgPastedFiltered".Translate(), fn, filtered),
                                MessageTypeDefOf.NeutralEvent);
                        else
                            Messages.Message(string.Format("FC.MsgPasted".Translate(), fn),
                                MessageTypeDefOf.NeutralEvent);
                    }
                }
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.DrawTextureRotated(br6, TexButton.Paste, 0f);
                GUI.color = Color.white;
            }

            if (manualExpanded.Contains(mk))
            {
                Settings.categories.TryGetValue(category, out var catInfo);
                var targets = catInfo?.targets ?? new List<BuildingTarget>();

                var grouped = targets.GroupBy(t => t.sourceMod).OrderBy(g => g.Key);

                foreach (var group in grouped)
                {
                    string modName = string.IsNullOrEmpty(group.Key)
                        ? "FC.SourceUnknown".Translate()
                        : group.Key;

                    // 彩色文本标题（非按钮）
                    var hdrRect = listing.GetRect(RowH);
                    var prevC = GUI.color;
                    GUI.color = TargetGroupColor;
                    Widgets.Label(new Rect(hdrRect.x + 24f, hdrRect.y, hdrRect.width - 24f, hdrRect.height),
                        modName);
                    GUI.color = prevC;

                    foreach (var t in group)
                    {
                        bool linked = Settings.ShouldLink(category, fn, t.defName);
                        bool nl = linked;
                        string display = $"         {t.defName} ({t.label})";
                        listing.CheckboxLabeled(display, ref nl);
                        if (nl != linked)
                            Settings.ToggleLink(category, fn, t.defName);
                    }
                }
            }
        }

        private static int TotalFacilityCount(CategoryInfo info)
        {
            int n = 0;
            foreach (var list in info.facilities.Values) n += list.Count;
            return n;
        }

        private float CalcHeight()
        {
            float h = 0f;
            foreach (var ckv in Settings.categories)
            {
                h += RowH;
                if (!expandedCategories.Contains(ckv.Key)) continue;

                var info = ckv.Value;
                if (!expandedGroups.ContainsKey(ckv.Key))
                    expandedGroups[ckv.Key] = new List<string>();

                foreach (var skv in info.facilities)
                {
                    h += RowH;
                    if (!expandedGroups[ckv.Key].Contains(skv.Key)) continue;

                    foreach (var fn in skv.Value)
                    {
                        h += RowH;
                        string mk = $"{ckv.Key}|{fn}";
                        if (manualExpanded.Contains(mk))
                        {
                            var targets = info.targets.GroupBy(t => t.sourceMod);
                            h += targets.Count() * RowH;      // 分组文本标题
                            h += info.targets.Count * 24f;    // 所有目标行（始终展开）
                        }
                    }
                }
            }
            return h;
        }
    }
}
