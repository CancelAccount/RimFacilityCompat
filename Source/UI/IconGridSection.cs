using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace FacilityCompat
{
    /// <summary>图标网格条目：承载一个 def 的展示数据（失效 defName 时 def 为 null）</summary>
    public class IconGridEntry
    {
        public readonly string defName;
        public readonly string label;
        public readonly string sourceMod;
        public readonly ThingDef? def;

        /// <summary>构建条目（defName 为唯一标识，label/sourceMod 用于展示，def 用于绘制贴图）</summary>
        public IconGridEntry(string defName, string label, string sourceMod, ThingDef? def)
        {
            this.defName = defName;
            this.label = label;
            this.sourceMod = sourceMod;
            this.def = def;
        }
    }

    /// <summary>图标角标状态：无标记（未选中对侧时）/ 已连接 / 已排除</summary>
    public enum GridMark { None, Linked, Excluded }

    /// <summary>
    /// 图标网格节控件（纯绘制，被动视图）：
    /// 绘制一个 mod 来源分节下的 def 图标网格，支持状态角标、选中描边、悬停高亮与 tooltip。
    /// 不持有任何业务与交互状态；命中矩形经 registerCell 回调交还调用方
    /// （换算到窗口局部坐标与视口裁剪由 GridHitMap.Register 完成）。
    /// 交互（单击/批量涂抹）由 Dialog_FacilityMatrix.RouteMouse 统一处理。
    /// </summary>
    public static class IconGridSection
    {
        // 尺寸常量（集中定义，避免魔法数字散落）
        public const float IconSize = 56f;
        public const float IconGap = 6f;
        public const float LabelH = 18f;
        /// <summary>分节标题行高（需容纳翻译后较高的字体，避免文字被裁切）</summary>
        public const float SectionHdrH = 30f;
        /// <summary>横向格间距（含图标）</summary>
        public static readonly float CellPitch = IconSize + IconGap;
        /// <summary>单个格子总高（图标 + 名称行 + 间距）</summary>
        public static readonly float CellH = IconSize + LabelH + IconGap;

        // 配色常量
        private static readonly Color SectionColor = new(0.95f, 0.75f, 0.4f);
        private static readonly Color SectionLineColor = new(0.95f, 0.75f, 0.4f, 0.35f);
        private static readonly Color SelectedBorderColor = new(1f, 0.82f, 0.25f);
        private static readonly Color LinkedMarkColor = new(0.3f, 0.9f, 0.4f);
        private static readonly Color ExcludedMarkColor = new(0.95f, 0.3f, 0.3f);
        private static readonly Color ExcludedIconColor = new(0.45f, 0.45f, 0.45f, 0.65f);
        private static readonly Color MissingDefColor = new(0.22f, 0.22f, 0.22f);
        private const float MarkSize = 9f;   // 角标方块边长
        private const float MarkMargin = 2f; // 角标与图标边缘间距

        /// <summary>
        /// 绘制一节图标网格（内容坐标系，x 从 0 开始）。
        /// viewTop/viewBottom 为当前滚动可视范围，范围外的行跳过绘制与命中注册（裁剪优化）。
        /// registerCell：命中注册回调（参数 = 条目 + 图标矩形[内容坐标]，由调用方换算注册）。
        /// 返回下一节的起始 y。
        /// </summary>
        public static float Draw(float y, float width, float viewTop, float viewBottom,
            string sectionTitle, List<IconGridEntry> entries,
            Func<IconGridEntry, GridMark> markOf, string? selectedDefName,
            Action<IconGridEntry, Rect> registerCell,
            Func<IconGridEntry, GridMark, string> tipOf)
        {
            // 分节标题（带底部分隔线）：超高截断，保证长 mod 名不裁切换行
            var hdrRect = new Rect(0f, y, width, SectionHdrH);
            if (IsVisible(hdrRect, viewTop, viewBottom))
            {
                var prev = GUI.color;
                GUI.color = SectionColor;
                Widgets.Label(new Rect(0f, y, width, SectionHdrH - 2f),
                    sectionTitle.Truncate(width));
                GUI.color = prev;
                Widgets.DrawBoxSolid(new Rect(0f, hdrRect.yMax - 2f, width, 1f), SectionLineColor);
            }
            y += SectionHdrH;

            // 图标网格：按列数流式排布
            int cols = Mathf.Max(1, Mathf.FloorToInt(width / CellPitch));
            for (int i = 0; i < entries.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                var cell = new Rect(col * CellPitch, y + row * CellH, IconSize, CellH);
                if (!IsVisible(cell, viewTop, viewBottom)) continue;

                var entry = entries[i];
                var mark = markOf(entry);
                DrawCell(cell, entry, mark,
                    selectedDefName == entry.defName,
                    registerCell, tipOf(entry, mark));
            }

            int rows = (entries.Count + cols - 1) / cols;
            float sectionEnd = y + rows * CellH;
            if (FCDebug.EnableDraw)
            {
                // 分节内容范围：左缘青色竖线（不同节的网格行起点错位/漂移可直接目视）
                Widgets.DrawLine(new Vector2(0f, y), new Vector2(0f, sectionEnd),
                    new Color(0f, 1f, 1f, 0.7f), 1f);
            }
            return sectionEnd + IconGap;
        }

        /// <summary>计算一节的内容高度（不绘制，用于滚动视口总量预计算）</summary>
        public static float MeasureHeight(int entryCount, float width)
        {
            int cols = Mathf.Max(1, Mathf.FloorToInt(width / CellPitch));
            int rows = (entryCount + cols - 1) / cols;
            return SectionHdrH + rows * CellH + IconGap;
        }

        // 矩形是否落在可视范围内（用于裁剪优化）
        private static bool IsVisible(Rect rect, float viewTop, float viewBottom)
            => rect.yMax > viewTop && rect.yMin < viewBottom;

        /// <summary>绘制单个图标格子：图标 + 名称 + 状态角标 + 选中描边 + 悬停高亮 + 命中注册 + tooltip</summary>
        private static void DrawCell(Rect cell, IconGridEntry entry, GridMark mark,
            bool selected, Action<IconGridEntry, Rect> registerCell, string tip)
        {
            var iconRect = new Rect(cell.x, cell.y, IconSize, IconSize);

            // 图标：排除态灰度渲染；失效 defName（def == null）画深灰占位块
            if (entry.def != null)
            {
                var prev = GUI.color;
                if (mark == GridMark.Excluded) GUI.color = ExcludedIconColor;
                Widgets.DefIcon(iconRect, entry.def);
                GUI.color = prev;
            }
            else
            {
                Widgets.DrawBoxSolid(iconRect, MissingDefColor);
            }

            // 名称：与图标水平居中对齐（包裹器：临时切换 Text.Anchor，绘制后恢复）
            var labelRect = new Rect(cell.x, cell.y + IconSize + 1f, IconSize, LabelH);
            var prevAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.UpperCenter;
            var prevFont = Text.Font;
            Text.Font = GameFont.Tiny;
            var prevColor = GUI.color;
            GUI.color = entry.def != null ? Color.white : Color.gray;
            Widgets.Label(labelRect, entry.label.Truncate(labelRect.width));
            GUI.color = prevColor;
            Text.Font = prevFont;
            Text.Anchor = prevAnchor;

            // 状态角标：右上角色块（绿 = 已连接，红 = 已排除）
            var markRect = new Rect(iconRect.xMax - MarkSize - MarkMargin,
                iconRect.y + MarkMargin, MarkSize, MarkSize);
            if (mark == GridMark.Linked)
                Widgets.DrawBoxSolid(markRect, LinkedMarkColor);
            else if (mark == GridMark.Excluded)
                Widgets.DrawBoxSolid(markRect, ExcludedMarkColor);

            // 悬停高亮与选中描边
            if (Mouse.IsOver(iconRect))
                Widgets.DrawHighlight(iconRect);
            if (selected)
            {
                var prev = GUI.color;
                GUI.color = SelectedBorderColor;
                Widgets.DrawBox(iconRect, 2);
                GUI.color = prev;
            }

            // ---------- 调试插桩：命中判定（与引擎 ButtonInvisibleDraggable 同款判定链） ----------
            bool evContains = iconRect.Contains(Event.current.mousePosition);
            bool isOver = Mouse.IsOver(iconRect); // = Contains && !IsInputBlockedNow（含上层遮挡检查）
            if (FCDebug.EnableLog && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonUp(0)))
            {
                string phase = Input.GetMouseButtonDown(0) ? "按下" : "释放";
                if (evContains)
                    FCDebug.Log($"[{phase}] 命中 {entry.defName} 事件类型={Event.current.type} " +
                        $"事件坐标={Event.current.mousePosition} 格子={iconRect} 被遮挡={!isOver}");
                else if (!FCDebug.SampleTakenThisFrame)
                {
                    FCDebug.SampleTakenThisFrame = true; // 未命中样本每帧一条，防刷屏
                    FCDebug.Log($"[{phase}] 未命中 {entry.defName} 事件类型={Event.current.type} " +
                        $"事件坐标={Event.current.mousePosition} 格子={iconRect}");
                }
            }
            if (FCDebug.EnableDraw)
            {
                // 绿 = 事件坐标在格内且未被遮挡（引擎判为可点）；黄 = 在格内但被遮挡（引擎判为不可点）；淡白 = 不在格内
                var dbgPrev = GUI.color;
                GUI.color = !evContains ? new Color(1f, 1f, 1f, 0.15f)
                    : (isOver ? Color.green : Color.yellow);
                Widgets.DrawBox(iconRect, 1);
                GUI.color = dbgPrev;
            }

            // 命中注册：本格交互统一交由窗口级 RouteMouse 处理，此处只上报矩形（内容坐标，
            // 由调用方换算为窗口局部坐标并注册进 GridHitMap，视口裁剪在 Register 内完成）
            registerCell(entry, iconRect);

            // 状态级日志：按下/释放瞬间悬停的格子（“尝试点击了谁”；与命中链路日志互补）
            if (FCDebug.EnableLog && Mouse.IsOver(iconRect))
            {
                if (Input.GetMouseButtonDown(0))
                    FCDebug.Log($"按下于 {entry.defName}");
                else if (Input.GetMouseButtonUp(0))
                    FCDebug.Log($"释放于 {entry.defName}");
            }

            TooltipHandler.TipRegion(iconRect, new TipSignal(tip, entry.defName.GetHashCode()));
        }
    }
}
