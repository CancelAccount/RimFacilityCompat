using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Side = FacilityCompat.InteractionState.Side;

namespace FacilityCompat
{
    /// <summary>
    /// 设施连接配置主窗口（双栏贴图矩阵）：
    /// 左栏 = 主设施（被附属的目标建筑，如研究台、床），右栏 = 附属设备（facility，如多元分析仪、床头柜），
    /// 均按 mod 来源分节、以物品贴图展示。
    /// 交互模型（窗口级事件路由 + 按下即触发）：
    ///   绘制阶段——IconGridSection 纯绘制，格子矩形注册进 GridHitMap（窗口局部坐标 + 视口裁剪）；
    ///   路由阶段——RouteMouse 以 Input 轮询 + Mouse.IsOver 命中，单击在按下帧立即触发
    ///   （对齐引擎 GUI.Button 行为，彻底消除跨帧 controlID 匹配类 bug），帧级去重保证
    ///   DoWindowContents 一帧多次调用下只触发一次。
    /// 选中/批量状态集中于 InteractionState（唯一状态类，变更带集中日志）。
    /// 点击本侧图标 = 选中/切换选中（点击选中项自身 = 取消）；点击对侧图标 = 切换该条连接并即时写入。
    /// </summary>
    public class Dialog_FacilityMatrix : Window
    {
        // 布局常量（集中定义，避免魔法数字散落；注意不可命名为 Margin，会隐藏 Window.Margin）
        private const float PanelMargin = 6f;
        private const float MidGap = 8f;
        private const float TabH = 28f;
        private const float TabGap = 4f;
        private const float TabPadW = 24f;
        private const float SearchRowH = 28f;
        private const float SearchLabelW = 44f;
        private const float PanelHdrH = 32f;
        private const float StatusH = 24f;
        private const float BtnRowH = 32f;
        private const float BtnW = 96f;
        private const float BtnSp = 6f;
        private const float ScrollBarW = 16f;

        // 配色常量（颜色跟随栏位：左栏蓝、右栏绿）
        private static readonly Color MainPanelColor = new(0.3f, 0.55f, 0.95f);
        private static readonly Color AccessoryPanelColor = new(0.55f, 0.7f, 0.35f);
        private static readonly Color TabSelectedBgColor = new(0.35f, 0.5f, 0.8f);

        private readonly FacilityCompatSettings settings;

        // ==================== 交互层（状态单类 + 当帧命中缓存 + 帧级去重） ====================

        /// <summary>交互状态（唯一状态类）：选中项 + 批量模式，变更集中走其方法并记日志</summary>
        private readonly InteractionState state = new();

        /// <summary>当帧格子命中缓存：绘制阶段注册，RouteMouse 查询（窗口局部坐标 + 视口裁剪）</summary>
        private readonly GridHitMap hitMap = new();

        /// <summary>
        /// 帧级去重标记：上次触发单击的帧号（-1 = 无）。
        /// DoWindowContents 按事件一帧多次调用（实测 Layout×2 → mouseDown → Layout → repaint），
        /// 而 Input.GetMouseButtonDown 按帧不按调用次数——不去重则单击触发 2~4 次 OnCellClick，
        /// toggle 语义下偶数次 = 视觉上「点了没反应」。
        /// </summary>
        private int lastClickFrame = -1;

        // ==================== 会话数据（非交互状态） ====================

        private string currentCategory = "";
        private Vector2 scrollMain = Vector2.zero;
        private Vector2 scrollAccessory = Vector2.zero;
        private string searchMain = "";
        private string searchAccessory = "";

        /// <summary>构建窗口；settings 为数据层引用（读写均通过其公开成员）</summary>
        public Dialog_FacilityMatrix(FacilityCompatSettings settings)
        {
            this.settings = settings;
            doCloseX = true;
            draggable = true;
            resizeable = true;
            closeOnAccept = false;
            currentCategory = FirstCategory();
        }

        /// <summary>初始尺寸：屏幕 85%，上限 1600×950</summary>
        public override Vector2 InitialSize => new(
            Mathf.Min(UI.screenWidth * 0.85f, 1600f),
            Mathf.Min(UI.screenHeight * 0.85f, 950f));

        /// <summary>窗口关闭时落盘一次，避免用户忘记保存而丢失界面内改动</summary>
        public override void PostClose()
        {
            settings.Write();
        }

        /// <summary>
        /// 应用并保存：声明式重建全部目标的设施链接 → 重连已放置建筑 → 落盘。
        /// 所有配置改动点（勾选/断开/重置/粘贴/导入）统一走此入口，当次会话即时生效。
        /// 重连必须在注入后顺序执行（直接调用而非事件，保证时序确定）；
        /// 设置界面期间游戏自动暂停，重连无卡顿风险。
        /// </summary>
        private void ApplyAndSave()
        {
            using (FCDebug.TimeScope("UI操作·声明式注入 ApplyInjection"))
            {
                FacilityPatcher.ApplyInjection(settings);
            }
            using (FCDebug.TimeScope("UI操作·重连已放置建筑 RelinkSpawnedThings"))
            {
                FacilityPatcher.RelinkSpawnedThings();
            }
            using (FCDebug.TimeScope("UI操作·设置落盘 Write"))
            {
                settings.Write();
            }
        }

        /// <summary>
        /// 根级 OnGUI 阶段（无窗口 group 变换）：批量模式激活期间在光标旁绘制游戏内勾/叉图标。
        /// 右键按住时显示反向状态图标（批量连接模式右键 = 批量断开，反之亦然）。
        /// 必须在此调用 GenUI.DrawMouseAttachment——它用 Event.current.mousePosition 定位
        /// ImmediateWindow，在 DoWindowContents（窗口局部坐标）里调用会定位到错误固定位置。
        /// </summary>
        public override void ExtraOnGUI()
        {
            if (state.BatchActive)
            {
                bool s = Input.GetMouseButton(1) ? !state.BatchState : state.BatchState;
                GenUI.DrawMouseAttachment(s ? Widgets.CheckboxOnTex : Widgets.CheckboxOffTex);
            }
        }

        /// <summary>主绘制：类别 Tab → 搜索行 → 双栏（注册命中矩形）→ 底部状态与操作栏 → 统一事件路由</summary>
        public override void DoWindowContents(Rect inRect)
        {
            FCDebug.FrameBegun(); // 调试：重置帧内日志样本标记
            hitMap.Clear();       // 当帧命中缓存重建（DoWindowContents 按事件多次调用，每次重建幂等）

            // 类别数据失效时回退到第一个类别
            if (string.IsNullOrEmpty(currentCategory) ||
                !settings.categories.ContainsKey(currentCategory))
            {
                currentCategory = FirstCategory();
                state.ClearSelection("category invalid");
            }

            // 批量模式失去选中上下文 → 自动退出（统一落盘并恢复窗口拖动）
            if (state.BatchActive && !state.HasSelection)
                ExitBatchMode();

            if (string.IsNullOrEmpty(currentCategory))
            {
                Widgets.Label(inRect, "FC.NoData".Translate());
                return;
            }

            float y = inRect.y;

            // 类别 Tab（可换行，返回实际占用高度）
            y += DrawCategoryTabs(inRect.x, y, inRect.width) + PanelMargin;

            // 双栏区域
            float bottomBarTop = inRect.yMax - (StatusH + BtnRowH + PanelMargin * 2f);
            float panelTop = y + SearchRowH;
            float panelH = bottomBarTop - panelTop;
            float panelW = (inRect.width - PanelMargin * 2f - MidGap) / 2f;

            // 搜索行
            DrawSearchRow(new Rect(inRect.x, y, panelW, SearchRowH), ref searchMain);
            DrawSearchRow(new Rect(inRect.x + panelW + MidGap, y, panelW, SearchRowH), ref searchAccessory);

            // 左栏（主设施）与右栏（附属设备）：绘制 + 注册命中矩形
            DrawMainPanel(new Rect(inRect.x, panelTop, panelW, panelH));
            DrawAccessoryPanel(new Rect(inRect.x + panelW + MidGap, panelTop, panelW, panelH));

            // 底部状态与操作栏
            DrawBottomBar(new Rect(inRect.x, bottomBarTop, inRect.width,
                inRect.yMax - bottomBarTop));

            RouteMouse();       // 统一事件路由（绘制之后：当帧命中矩形已注册完毕）
            DrawDebugOverlay(); // 调试：窗口级坐标对照与滚动量显示
        }

        // ==================== 统一事件路由（窗口级，按下即触发） ====================

        /// <summary>
        /// 统一事件路由：Input 轮询 + hitMap 命中（窗口局部坐标，Mouse.IsOver 含
        /// IsInputBlockedNow 遮挡/焦点检查——上层窗口如 FloatMenu 打开时点击自动失效）。
        /// 单击 = 按下即触发（mouseDown 帧，对齐引擎 GUI.Button）+ 帧级去重
        /// （Input 状态按帧不按 DoWindowContents 调用次数）。
        /// 批量涂抹 = 模式激活且按住期间每帧对悬停的对侧格子应用状态（幂等）。
        /// </summary>
        private void RouteMouse()
        {
            bool lmb = Input.GetMouseButton(0), rmb = Input.GetMouseButton(1);

            // 单击：先查主设施栏再查附属设备栏（两栏区域不重叠）
            if (Input.GetMouseButtonDown(0) && Time.frameCount != lastClickFrame)
            {
                lastClickFrame = Time.frameCount;
                if (hitMap.TryHitMain(out string mainDef))
                {
                    GUIUtility.keyboardControl = 0; // 对齐引擎按钮语义：点击格子让出键盘焦点（搜索框）
                    OnCellClick(Side.Main, mainDef);
                }
                else if (hitMap.TryHitAccessory(out string accDef))
                {
                    GUIUtility.keyboardControl = 0;
                    OnCellClick(Side.Accessory, accDef);
                }
            }

            // 批量涂抹：模式激活且按住期间，对悬停的对侧格子应用状态（左键 = 目标状态，右键 = 反向）。
            // 注意 (lmb || rmb) 必须显式括号：&& 优先于 ||，裸写会让右键在批量模式关闭时也进入涂抹分支；
            // 反向判定取 rmb && !lmb（左右同按时左键优先，与旧实现一致）。
            if (state.BatchActive && state.HasSelection && (lmb || rmb)
                && hitMap.TryHitOpposite(state.SelSide, out string hitDef))
            {
                if (state.SelSide == Side.Main)
                    ApplyBatch(hitDef, state.SelDef, rmb && !lmb); // 选中主设施 → 命中的是附属设备
                else
                    ApplyBatch(state.SelDef, hitDef, rmb && !lmb); // 选中附属设备 → 命中的是主设施
            }
        }

        /// <summary>格子单击统一入口（RouteMouse 转发）：
        /// 对侧已选中 → 切换该条连接（批量模式下应用批量状态）；
        /// 本侧同项 → 取消选中；否则 → 选中该项</summary>
        private void OnCellClick(Side side, string defName)
        {
            FCDebug.Log($"尝试点击 {(side == Side.Main ? "主设施" : "附属设备")}={defName} | " +
                $"当前激活={InteractionState.Describe(state.SelSide, state.SelDef)} 批量模式={state.BatchActive}");

            // 对侧已选中：点击 = 切换该条连接（ShouldLink/ToggleLink 参数序：(类别, 附属设备defName, 主设施defName)）
            if (state.SelSide == InteractionState.Opposite(side))
            {
                string accessory = side == Side.Accessory ? defName : state.SelDef;
                string main = side == Side.Main ? defName : state.SelDef;
                if (state.BatchActive)
                    ApplyBatch(accessory, main);
                else
                {
                    settings.ToggleLink(currentCategory, accessory, main);
                    ApplyAndSave();
                }
                return;
            }

            // 本侧已选中同一项：再次点击 = 取消选中（批量模式将由帧首检查自动退出并落盘）
            if (state.IsSelected(side, defName))
            {
                state.ClearSelection(side == Side.Main ? "取消选中主设施" : "取消选中附属设备");
                return;
            }

            state.Select(side, defName, side == Side.Main ? "选中主设施" : "选中附属设备");
        }

        // ==================== 调试工具（由全局操作菜单开关） ====================

        /// <summary>
        /// 窗口级调试可视化：品红十字 = Event.current.mousePosition（窗口局部坐标），
        /// 青色十字 = 全局 UI 鼠标（UI.MousePositionOnUIInverted 换算到窗口局部）。
        /// 两点分离即坐标变换异常的实锤；文本行附双栏滚动量。
        /// </summary>
        private void DrawDebugOverlay()
        {
            if (!FCDebug.EnableDraw) return;
            var evPos = Event.current.mousePosition;
            var uiPos = UI.MousePositionOnUIInverted - windowRect.position;
            DrawDebugCross(evPos, Color.magenta);
            DrawDebugCross(uiPos, Color.cyan);
            // 文本框靠右边缘时收回窗口内（防截断不可读）
            float labelX = Mathf.Min(evPos.x + 12f, windowRect.width - 430f);
            Widgets.Label(new Rect(labelX, evPos.y - 22f, 420f, 20f),
                $"ev:{evPos.x:F0},{evPos.y:F0} ui:{uiPos.x:F0},{uiPos.y:F0} " +
                $"scrollM:{scrollMain.y:F0} scrollA:{scrollAccessory.y:F0}");
        }

        /// <summary>画调试十字标记（当前 group 上下文坐标，±10px）</summary>
        private static void DrawDebugCross(Vector2 pos, Color color)
        {
            Widgets.DrawLine(new Vector2(pos.x - 10f, pos.y), new Vector2(pos.x + 10f, pos.y), color, 1f);
            Widgets.DrawLine(new Vector2(pos.x, pos.y - 10f), new Vector2(pos.x, pos.y + 10f), color, 1f);
        }

        /// <summary>取排序后的第一个类别 key（无类别返回空串）</summary>
        private string FirstCategory()
            => settings.categories.Keys.OrderBy(k => k).FirstOrDefault() ?? "";

        // ==================== 类别 Tab ====================

        /// <summary>绘制类别 Tab 行（按文字宽度流式换行）；返回占用高度</summary>
        private float DrawCategoryTabs(float x, float y, float maxWidth)
        {
            var keys = settings.categories.Keys.OrderBy(k => k).ToList();
            float curX = x;
            float curY = y;
            float rowMaxX = x + maxWidth;

            foreach (var key in keys)
            {
                string name = CategoryDisplayName(key);
                float w = Text.CalcSize(name).x + TabPadW;
                if (curX + w > rowMaxX && curX > x)
                {
                    curX = x;
                    curY += TabH + TabGap;
                }

                var rect = new Rect(curX, curY, w, TabH);
                bool isSel = key == currentCategory;
                var prevBg = GUI.backgroundColor;
                if (isSel) GUI.backgroundColor = TabSelectedBgColor;
                if (Widgets.ButtonText(rect, name))
                {
                    currentCategory = key;
                    state.ClearSelection("切换类别Tab"); // 批量模式（若有）由帧首检查自动退出并落盘
                    searchMain = "";
                    searchAccessory = "";
                    scrollMain = Vector2.zero;
                    scrollAccessory = Vector2.zero;
                }
                GUI.backgroundColor = prevBg;

                curX += w + TabGap;
            }

            return curY + TabH - y;
        }

        /// <summary>类别显示名：displayName 为空时回退到 key</summary>
        private string CategoryDisplayName(string key)
        {
            return settings.categories.TryGetValue(key, out var info)
                && !string.IsNullOrEmpty(info.displayName)
                ? info.displayName
                : key;
        }

        // ==================== 搜索行 ====================

        /// <summary>绘制单个搜索框（前缀标签 + 文本输入）</summary>
        private static void DrawSearchRow(Rect rect, ref string search)
        {
            var labelRect = new Rect(rect.x, rect.y, SearchLabelW, rect.height);
            var fieldRect = new Rect(rect.x + SearchLabelW, rect.y + 2f,
                rect.width - SearchLabelW, rect.height - 4f);
            Widgets.Label(labelRect, "FC.UiSearch".Translate());
            search = Widgets.TextField(fieldRect, search);
        }

        // ==================== 双栏 ====================

        /// <summary>绘制左栏（主设施：被附属的目标建筑）：栏标题 + 各 mod 分节图标网格（独立滚动）</summary>
        private void DrawMainPanel(Rect panel)
        {
            var info = settings.categories[currentCategory];

            DrawPanelHeader(new Rect(panel.x, panel.y, panel.width, PanelHdrH),
                string.Format("FC.UiMain".Translate(), info.targets.Count), MainPanelColor);

            // 主设施按 mod 来源分节（搜索过滤 + 按名称排序）
            var sections = info.targets
                .GroupBy(t => t.sourceMod)
                .OrderBy(g => g.Key)
                .Select(g => (
                    title: g.Key,
                    entries: g
                        .Select(t => CreateEntry(t.defName, t.sourceMod, t.label))
                        .Where(e => MatchesSearch(e, searchMain))
                        .OrderBy(e => e.label).ThenBy(e => e.defName)
                        .ToList()))
                .Where(s => s.entries.Count > 0)
                .ToList();

            var viewRect = new Rect(panel.x, panel.y + PanelHdrH, panel.width,
                panel.height - PanelHdrH);
            DrawScrolledGrid(Side.Main, viewRect, ref scrollMain, sections,
                // 附属设备已选中 → 主设施图标显示其与选中附属设备的连接状态
                e => state.SelSide == Side.Accessory
                    ? (settings.ShouldLink(currentCategory, state.SelDef, e.defName)
                        ? GridMark.Linked : GridMark.Unlinked)
                    : GridMark.None,
                state.SelSide == Side.Main ? state.SelDef : null,
                (e, mark) => BuildTip(e, mark, "FC.UiMainSide".Translate()));
        }

        /// <summary>绘制右栏（附属设备：facility）：栏标题 + 各 mod 分节图标网格（独立滚动）</summary>
        private void DrawAccessoryPanel(Rect panel)
        {
            var info = settings.categories[currentCategory];

            DrawPanelHeader(new Rect(panel.x, panel.y, panel.width, PanelHdrH),
                string.Format("FC.UiAccessory".Translate(),
                    info.facilities.Values.Sum(l => l.Count)), AccessoryPanelColor);

            // 附属设备按 mod 来源分节（info.facilities 已按来源分组）
            var sections = info.facilities
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => (
                    title: kvp.Key,
                    entries: kvp.Value
                        .Select(fn => CreateEntry(fn, kvp.Key))
                        .Where(e => MatchesSearch(e, searchAccessory))
                        .OrderBy(e => e.label).ThenBy(e => e.defName)
                        .ToList()))
                .Where(s => s.entries.Count > 0)
                .ToList();

            var viewRect = new Rect(panel.x, panel.y + PanelHdrH, panel.width,
                panel.height - PanelHdrH);
            DrawScrolledGrid(Side.Accessory, viewRect, ref scrollAccessory, sections,
                // 主设施已选中 → 附属设备图标显示其与选中主设施的连接状态
                e => state.SelSide == Side.Main
                    ? (settings.ShouldLink(currentCategory, e.defName, state.SelDef)
                        ? GridMark.Linked : GridMark.Unlinked)
                    : GridMark.None,
                state.SelSide == Side.Accessory ? state.SelDef : null,
                (e, mark) => BuildTip(e, mark, "FC.UiAccessorySide".Translate()));
        }

        /// <summary>布局振荡检测：各栏上次记录的 viewRect.y（帧间漂移会导致绘制与事件命中错位）</summary>
        private static readonly Dictionary<string, float> lastViewY = new();

        /// <summary>绘制带滚动条的分节网格（先量高再绘制，内容超出部分可滚动），
        /// 并向 hitMap 注册可见格子（内容坐标 → 窗口局部坐标换算 + 视口裁剪在 Register 内完成）</summary>
        private void DrawScrolledGrid(Side side, Rect viewRect, ref Vector2 scroll,
            List<(string title, List<IconGridEntry> entries)> sections,
            Func<IconGridEntry, GridMark> markOf, string? selectedDefName,
            Func<IconGridEntry, GridMark, string> tipOf)
        {
            float contentW = viewRect.width - ScrollBarW;
            float contentH = sections.Sum(s => IconGridSection.MeasureHeight(s.entries.Count, contentW));
            var contentRect = new Rect(0f, 0f, contentW, Mathf.Max(contentH, viewRect.height));

            string tagCn = side == Side.Main ? "主设施栏" : "附属设备栏";

            // 调试：点击时记录视口/内容几何（viewRect 为窗口局部坐标，格子 rect 为滚动内容坐标）
            if (FCDebug.EnableLog && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonUp(0)))
                FCDebug.Log($"{tagCn} 视口={viewRect} 滚动={scroll} 内容高={contentRect.height:F0}");

            // 调试：viewRect.y 帧间振荡检测——日志里 126/158 交替即为布局漂移实锤
            if (FCDebug.EnableLog)
            {
                string tag = side == Side.Main ? "Main" : "Accessory";
                if (lastViewY.TryGetValue(tag, out float prevY) && Mathf.Abs(prevY - viewRect.y) > 0.1f)
                    FCDebug.Log($"布局漂移 {tagCn}: 视口y {prevY:F0} -> {viewRect.y:F0}（内容高 {contentRect.height:F0}）");
                lastViewY[tag] = viewRect.y;
            }

            Widgets.BeginScrollView(viewRect, ref scroll, contentRect);
            float viewTop = scroll.y;
            float viewBottom = scroll.y + viewRect.height;
            // 滚动内容坐标 → 窗口局部坐标的换算原点：内容 (0,0) 对应 viewRect.position - scroll
            var origin = viewRect.position - scroll;
            float y = 0f;
            foreach (var (title, entries) in sections)
                y = IconGridSection.Draw(y, contentW, viewTop, viewBottom,
                    title, entries, markOf, selectedDefName,
                    (entry, iconRect) => hitMap.Register(side, entry.defName,
                        new Rect(origin + iconRect.position, iconRect.size), viewRect),
                    tipOf);
            Widgets.EndScrollView();
        }

        /// <summary>栏标题：彩色文本 + 底部细分隔线</summary>
        private static void DrawPanelHeader(Rect rect, string text, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            Text.Font = GameFont.Medium;
            Widgets.Label(rect, text);
            Text.Font = GameFont.Small;
            GUI.color = prev;
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 2f, rect.width, 1f),
                new Color(color.r, color.g, color.b, 0.4f));
        }

        // ==================== 数据构建 ====================

        /// <summary>由 defName 构建网格条目（def 失效时 label 回退；sourceMod 用于分节与 tooltip）</summary>
        private static IconGridEntry CreateEntry(string defName, string sourceMod,
            string? fallbackLabel = null)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            string label = !string.IsNullOrEmpty(def?.label) ? def!.label
                : (!string.IsNullOrEmpty(fallbackLabel) ? fallbackLabel! : defName);
            return new IconGridEntry(defName, label, sourceMod, def);
        }

        /// <summary>搜索匹配：label 或 defName 包含关键字（忽略大小写）</summary>
        private static bool MatchesSearch(IconGridEntry entry, string search)
        {
            if (string.IsNullOrEmpty(search)) return true;
            return entry.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>构建 tooltip 文本：名称 / defName / 来源 / 连接状态（对侧选中时）</summary>
        private string BuildTip(IconGridEntry entry, GridMark mark, string sideLabel)
        {
            var lines = new List<string>
            {
                entry.label,
                entry.defName,
                $"[{entry.sourceMod}]",
                sideLabel
            };
            if (mark == GridMark.Linked) lines.Add("FC.TipLinked".Translate());
            else if (mark == GridMark.Unlinked) lines.Add("FC.TipUnlinked".Translate());
            return string.Join("\n", lines);
        }

        // ==================== 批量模式（按钮激活的连续设置） ====================

        /// <summary>进入批量模式：锁定窗口拖动，模式内点击/拖过对侧格子即应用目标状态</summary>
        private void EnterBatchMode(bool linkState)
        {
            state.EnterBatch(linkState);
            draggable = false; // 按钮点击完成后才关闭拖动，彻底避开与 GUI.DragWindow 的时序竞争
        }

        /// <summary>退出批量模式：恢复窗口拖动，批量改动统一应用并落盘</summary>
        private void ExitBatchMode()
        {
            if (!state.BatchActive) return;
            state.ExitBatch();
            draggable = true;
            ApplyAndSave();
        }

        /// <summary>批量应用：把 (附属设备→主设施) 连接设为批量目标状态（inverted = 右键反向；
        /// 与目标一致时跳过；落盘延迟到退出批量模式）</summary>
        private void ApplyBatch(string accessory, string main, bool inverted = false)
        {
            bool target = inverted ? !state.BatchState : state.BatchState;
            if (settings.ShouldLink(currentCategory, accessory, main) != target)
                settings.ToggleLink(currentCategory, accessory, main);
        }

        /// <summary>绘制批量模式按钮（连接/断开）：激活中高亮，再点退出；方向互斥切换；tooltip 含右键反向说明</summary>
        private void DrawBatchButton(Rect rect, bool linkState)
        {
            bool isActive = state.BatchActive && state.BatchState == linkState;
            var prevBg = GUI.backgroundColor;
            if (isActive) GUI.backgroundColor = linkState ? Color.green : Color.red;
            if (Widgets.ButtonText(rect,
                    (linkState ? "FC.BtnBatchOn" : "FC.BtnBatchOff").Translate(),
                    active: state.HasSelection))
            {
                if (isActive)
                    ExitBatchMode();          // 再次点击退出批量模式
                else
                    EnterBatchMode(linkState); // 进入或切换方向
            }
            GUI.backgroundColor = prevBg;
            TooltipHandler.TipRegion(rect, "FC.TipBatch".Translate());
        }

        // ==================== 底部状态与操作栏 ====================

        /// <summary>底部栏：第一行选中状态统计，第二行批量操作 + 保存</summary>
        private void DrawBottomBar(Rect bar)
        {
            var statusRect = new Rect(bar.x, bar.y, bar.width, StatusH);
            Widgets.Label(statusRect, BuildStatusText());

            var btnRect = new Rect(bar.x, bar.y + StatusH, BtnW, BtnRowH);
            float step = BtnW + BtnSp;

            // 全部启用：主设施选中 → 全部附属设备连接它；附属设备选中 → 它连接全部主设施
            if (Widgets.ButtonText(btnRect, "FC.BtnAllOn".Translate(), active: state.HasSelection))
                ApplyToSelection(AllOn);
            btnRect.x += step;

            // 全部禁用（对称语义）
            if (Widgets.ButtonText(btnRect, "FC.BtnAllOff".Translate(), active: state.HasSelection))
                ApplyToSelection(AllOff);
            btnRect.x += step;

            // 重置原始：按选中侧分派（附属设备 → 恢复其原始链接；主设施 → 恢复所有设施与它的原始连接）
            if (Widgets.ButtonText(btnRect, "FC.BtnReset".Translate(), active: state.HasSelection))
            {
                if (state.SelSide == Side.Accessory)
                    settings.ResetFacilityToOriginal(currentCategory, state.SelDef);
                else
                    settings.ResetTargetToOriginal(currentCategory, state.SelDef);
                ApplyAndSave();
                Messages.Message(
                    string.Format("FC.MsgReset".Translate(), state.SelDef),
                    MessageTypeDefOf.NeutralEvent);
            }
            btnRect.x += step;

            // 复制：按选中侧分派（附属设备 → 连接配置；主设施 → 连接模式）
            if (Widgets.ButtonText(btnRect, "FC.BtnCopy".Translate(), active: state.HasSelection))
                CopySelected();
            btnRect.x += step;

            // 粘贴：选中侧与剪贴板维度匹配且有数据时可用（设施配置 ↔ 主设施连接模式互不通用）
            if (Widgets.ButtonText(btnRect, "FC.BtnPaste".Translate(),
                    active: state.HasSelection && FacilityClipboard.hasData
                        && FacilityClipboard.direction == (state.SelSide == Side.Accessory
                            ? FacilityClipboard.Direction.Facility
                            : FacilityClipboard.Direction.Target)))
                PasteToSelected();
            btnRect.x += step;

            // 批量连接/断开：按钮激活批量模式（锁定窗口拖动），点击/拖过对侧格子连续应用
            DrawBatchButton(btnRect, true);
            btnRect.x += step;
            DrawBatchButton(btnRect, false);

            // 全局操作（右下角）：预设导入/导出、全部重置、调试开关（独立对话框承载，替代原多层菜单）
            var globalRect = new Rect(bar.xMax - BtnW, bar.y + StatusH, BtnW, BtnRowH);
            if (Widgets.ButtonText(globalRect, "FC.UiGlobalMenu".Translate()))
                Find.WindowStack.Add(new Dialog_PresetManager(settings));
        }

        // ==================== 全局操作 ====================
        // 全部全局操作已迁移至 Dialog_PresetManager（预设导入/导出/全部重置/调试开关），
        // 由右下「全局操作」按钮打开，替代原 BuildGlobalMenu/BuildExportMenu/BuildImportMenu
        // 多层 FloatMenu 链（选项多时挤在右下角不美观）。

        /// <summary>构建状态行文本：选中项名称 + 已连接/总数；无选中时给出操作引导</summary>
        private string BuildStatusText()
        {
            var info = settings.categories[currentCategory];
            if (state.SelSide == Side.Main)
            {
                // 主设施选中：统计全部附属设备中与它连接的数量
                var facilityNames = AllFacilityDefNames(info).ToList();
                int linked = facilityNames.Count(fn =>
                    settings.ShouldLink(currentCategory, fn, state.SelDef));
                return string.Format("FC.UiSelectedFmt".Translate(),
                    EntryLabel(state.SelDef), linked, facilityNames.Count);
            }
            if (state.SelSide == Side.Accessory)
            {
                // 附属设备选中：统计全部主设施中与它连接的数量
                int linked = info.targets.Count(t =>
                    settings.ShouldLink(currentCategory, state.SelDef, t.defName));
                return string.Format("FC.UiSelectedFmt".Translate(),
                    EntryLabel(state.SelDef), linked, info.targets.Count);
            }
            return "FC.UiNoSelection".Translate();
        }

        /// <summary>取条目显示名（优先 def.label，失败回退 defName）</summary>
        private static string EntryLabel(string defName)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            return string.IsNullOrEmpty(def?.label) ? defName : def!.label;
        }

        /// <summary>对当前选中项执行批量操作（委托区分启用/禁用；有改动则即时应用并落盘）</summary>
        private void ApplyToSelection(Func<bool> operation)
        {
            if (operation()) ApplyAndSave();
        }

        /// <summary>全部启用：按选中侧批量建立连接</summary>
        private bool AllOn()
        {
            if (!state.HasSelection) return false;
            var info = settings.categories[currentCategory];

            if (state.SelSide == Side.Accessory)
            {
                // 附属设备选中：该设备连接全部主设施（数据层按设施提供）
                settings.EnableAllTargets(currentCategory, state.SelDef);
                return true;
            }

            // 主设施选中：全部附属设备连接它
            foreach (var fn in AllFacilityDefNames(info))
                if (!settings.ShouldLink(currentCategory, fn, state.SelDef))
                    settings.ToggleLink(currentCategory, fn, state.SelDef);
            return true;
        }

        /// <summary>全部禁用：按选中侧批量断开连接</summary>
        private bool AllOff()
        {
            if (!state.HasSelection) return false;
            var info = settings.categories[currentCategory];

            if (state.SelSide == Side.Accessory)
            {
                settings.DisableAllTargets(currentCategory, state.SelDef);
                return true;
            }

            foreach (var fn in AllFacilityDefNames(info))
                if (settings.ShouldLink(currentCategory, fn, state.SelDef))
                    settings.ToggleLink(currentCategory, fn, state.SelDef);
            return true;
        }

        /// <summary>类别下全部附属设备 defName（跨来源去重）</summary>
        private static IEnumerable<string> AllFacilityDefNames(CategoryInfo info)
            => info.facilities.Values.SelectMany(l => l).Distinct();

        /// <summary>复制当前选中项配置：附属设备 → 连接配置；主设施 → 连接模式（只记录连接列表）</summary>
        private void CopySelected()
        {
            if (state.SelSide == Side.Accessory)
            {
                var linked = settings.GetLinkedList(currentCategory, state.SelDef);
                FacilityClipboard.Copy(currentCategory, linked);
            }
            else
            {
                var linked = settings.GetTargetLinkedList(currentCategory, state.SelDef);
                FacilityClipboard.CopyTarget(currentCategory, linked);
            }
            Messages.Message(
                string.Format("FC.MsgCopied".Translate(), state.SelDef, currentCategory),
                MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>粘贴剪贴板配置到当前选中项（仅限同类别，跨类别拦截）</summary>
        private void PasteToSelected()
        {
            bool ok;
            List<string> pastedLinked;
            if (state.SelSide == Side.Accessory)
                ok = FacilityClipboard.Paste(currentCategory, settings, out pastedLinked);
            else
                ok = FacilityClipboard.PasteTarget(currentCategory, settings, out pastedLinked);

            // 跨类别粘贴一律拦截（含原本可跨类别的全选/全空）
            if (!ok)
            {
                Messages.Message(
                    string.Format("FC.MsgPasteCrossCategory".Translate(), state.SelDef),
                    MessageTypeDefOf.RejectInput);
                return;
            }

            if (state.SelSide == Side.Accessory)
                settings.SetLinkedList(currentCategory, state.SelDef, pastedLinked);
            else
                settings.SetTargetLinkedList(currentCategory, state.SelDef, pastedLinked);

            ApplyAndSave();
            Messages.Message(
                string.Format("FC.MsgPasted".Translate(), state.SelDef),
                MessageTypeDefOf.NeutralEvent);
        }
    }
}
