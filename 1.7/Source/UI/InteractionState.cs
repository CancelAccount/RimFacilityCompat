namespace FacilityCompat
{
    /// <summary>
    /// 交互状态（唯一状态类）：集中持有选中项与批量模式。
    /// 所有交互状态的变更必须走本类方法——方法内记录「旧 -> 新 + 原因」调试日志（选中状态集中日志约定），
    /// 杜绝旧版散落赋值导致的日志遗漏（曾需要窗口级兜底检测补漏）。
    /// 不持有数据层引用：落盘（settings.Write）与窗口属性联动（draggable）由窗口层负责。
    /// </summary>
    public sealed class InteractionState
    {
        /// <summary>选中侧：无 / 左栏主设施 / 右栏附属设备（同时用作双栏面板标识）</summary>
        public enum Side { None, Main, Accessory }

        /// <summary>当前选中侧</summary>
        public Side SelSide { get; private set; } = Side.None;

        /// <summary>当前选中项 defName（SelSide 为 None 时无意义）</summary>
        public string SelDef { get; private set; } = "";

        /// <summary>批量模式是否激活（激活期间按住鼠标拖过对侧格子连续应用连接状态）</summary>
        public bool BatchActive { get; private set; }

        /// <summary>批量目标状态：true = 连接（右键按住 = 应用反向）</summary>
        public bool BatchState { get; private set; }

        /// <summary>是否有选中项</summary>
        public bool HasSelection => SelSide != Side.None;

        /// <summary>是否选中了指定侧的指定项</summary>
        public bool IsSelected(Side side, string defName) => SelSide == side && SelDef == defName;

        /// <summary>取指定侧的对侧（None 的对侧仍为 None）</summary>
        public static Side Opposite(Side side)
            => side == Side.Main ? Side.Accessory : side == Side.Accessory ? Side.Main : Side.None;

        /// <summary>选中状态的日志描述（None 显示「无」，否则「主设施:defName / 附属设备:defName」）</summary>
        public static string Describe(Side side, string def) => side switch
        {
            Side.Main => $"主设施:{def}",
            Side.Accessory => $"附属设备:{def}",
            _ => "无"
        };

        /// <summary>选中指定项（无变化时不记日志；所有选中变更的唯一入口）</summary>
        public void Select(Side side, string defName, string reason)
        {
            if (SelSide == side && SelDef == defName) return;
            FCDebug.Log($"选中变更: {Describe(SelSide, SelDef)} -> {Describe(side, defName)}（{reason}）");
            SelSide = side;
            SelDef = defName;
        }

        /// <summary>取消选中（无变化时不记日志）</summary>
        public void ClearSelection(string reason) => Select(Side.None, "", reason);

        /// <summary>进入（或切换方向进入）批量模式</summary>
        public void EnterBatch(bool linkState)
        {
            if (BatchActive && BatchState == linkState) return;
            FCDebug.Log($"批量模式: {(BatchActive ? (BatchState ? "连接" : "断开") : "关")} -> {(linkState ? "连接" : "断开")}");
            BatchActive = true;
            BatchState = linkState;
        }

        /// <summary>退出批量模式（批量改动落盘由窗口层负责）</summary>
        public void ExitBatch()
        {
            if (!BatchActive) return;
            FCDebug.Log("批量模式: -> 关");
            BatchActive = false;
        }
    }
}
