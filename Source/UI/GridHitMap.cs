using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 当帧格子命中缓存：绘制阶段由 IconGridSection 注册格子矩形，事件路由阶段（RouteMouse）查询。
    /// 坐标系 = 窗口局部（与 DoWindowContents 中 EndScrollView 之后的 Event.current.mousePosition 同系），
    /// 命中查询统一走 Mouse.IsOver（含 IsInputBlockedNow 的遮挡/焦点检查——上层窗口
    /// 如 FloatMenu 打开时点击自动失效，防穿透），禁止退化为裸 rect.Contains。
    /// 刻意用列表而非 defName → Rect 字典：同一 defName 可能出现在多个 mod 来源分节
    /// （数据层 AllFacilityDefNames 有 Distinct() 去重为证），须保留全部命中矩形。
    /// 必须为 Dialog 实例字段（禁止 static）：每帧 Clear 重建，跟随窗口拖动/缩放几何。
    /// </summary>
    public sealed class GridHitMap
    {
        private readonly List<(string defName, Rect rect)> mainCells = new();
        private readonly List<(string defName, Rect rect)> accessoryCells = new();

        /// <summary>清空当帧缓存（DoWindowContents 每次调用开头执行；按事件多次调用，重建幂等）</summary>
        public void Clear()
        {
            mainCells.Clear();
            accessoryCells.Clear();
        }

        /// <summary>
        /// 注册一个格子的命中矩形（窗口局部坐标），并裁剪到滚动视口内：
        /// 半露出格子的几何矩形会越过视口边缘（伸入底栏/搜索行区域），而引擎的
        /// mouseOverScrollViewStack 输入保护只在 BeginScrollView 内部生效——事件路由
        /// 发生在视图外部，须自行裁剪，否则点击底栏按钮会同时命中越界格子（双重触发）。
        /// </summary>
        public void Register(InteractionState.Side side, string defName, Rect windowRect, Rect viewport)
        {
            if (side != InteractionState.Side.Main && side != InteractionState.Side.Accessory) return;

            var clipped = ClipToViewport(windowRect, viewport);
            if (clipped.width <= 0f || clipped.height <= 0f) return; // 与视口无交集（保险，视口外格子本已被裁剪跳过）

            var cells = side == InteractionState.Side.Main ? mainCells : accessoryCells;
            cells.Add((defName, clipped));
        }

        /// <summary>命中查询：鼠标是否悬停在指定侧任一格子上（命中则返回该格 defName）</summary>
        public bool TryHit(InteractionState.Side side, out string defName)
        {
            defName = "";
            if (side == InteractionState.Side.None) return false;
            var cells = side == InteractionState.Side.Main ? mainCells : accessoryCells;
            foreach (var (dn, rect) in cells)
                if (Mouse.IsOver(rect))
                {
                    defName = dn;
                    return true;
                }
            return false;
        }

        /// <summary>主设施栏命中查询</summary>
        public bool TryHitMain(out string defName) => TryHit(InteractionState.Side.Main, out defName);

        /// <summary>附属设备栏命中查询</summary>
        public bool TryHitAccessory(out string defName) => TryHit(InteractionState.Side.Accessory, out defName);

        /// <summary>选中侧的对侧栏命中查询（批量涂抹用；无选中时恒 false）</summary>
        public bool TryHitOpposite(InteractionState.Side selectedSide, out string defName)
            => TryHit(InteractionState.Opposite(selectedSide), out defName);

        /// <summary>把矩形裁剪到视口内（无交集时返回零尺寸矩形）</summary>
        private static Rect ClipToViewport(Rect r, Rect vp)
        {
            float xMin = Mathf.Max(r.xMin, vp.xMin);
            float yMin = Mathf.Max(r.yMin, vp.yMin);
            float xMax = Mathf.Min(r.xMax, vp.xMax);
            float yMax = Mathf.Min(r.yMax, vp.yMax);
            return Rect.MinMaxRect(xMin, yMin, Mathf.Max(xMin, xMax), Mathf.Max(yMin, yMax));
        }
    }
}
