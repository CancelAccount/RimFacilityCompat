using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 调试工具中枢（排查单击失灵等问题）：
    /// 全局版本常量 DebugBuild 定夺调试/发行版——true 时日志与可视化工具可通过
    /// “全局操作”菜单开关；false 时 EnableLog/EnableDraw 恒为 false，所有插桩短路为零开销。
    /// 判定链打点针对 ButtonInvisibleDraggable：其内部用 Input 轮询 + Mouse.IsOver
    /// （= rect.Contains(Event 坐标) &amp;&amp; !IsInputBlockedNow）判定，
    /// 悬停高亮/批量涂抹/单击三条路径坐标系若不一致，日志与边框颜色会直接暴露。
    /// </summary>
    public static class FCDebug
    {
        /// <summary>全局版本标志：true = 调试版（含日志与可视化工具）；false = 发行版（强制禁用）。发版时改为 false。</summary>
        public const bool DebugBuild = true;

        private static bool enableLog;  // 菜单开关的实际存储（发行版下 getter 短路，此字段不生效）
        private static bool enableDraw;

        /// <summary>日志开关：读取受 DebugBuild 保护，发行版恒 false</summary>
        public static bool EnableLog
        {
            get => DebugBuild && enableLog;
            set => enableLog = value;
        }

        /// <summary>可视化开关：读取受 DebugBuild 保护，发行版恒 false</summary>
        public static bool EnableDraw
        {
            get => DebugBuild && enableDraw;
            set => enableDraw = value;
        }

        /// <summary>帧内标记：未命中样本每帧只记一条，防止几十个格子刷屏</summary>
        internal static bool SampleTakenThisFrame;

        /// <summary>每帧开头调用：重置帧内样本标记（发行版零开销）</summary>
        public static void FrameBegun() => SampleTakenThisFrame = false;

        /// <summary>输出调试日志（前缀 [FC-Dbg] 便于过滤；关闭时零开销）。
        /// 注意用全限定 Verse.Log——类内存在同名方法 Log，裸写会解析到自身。</summary>
        public static void Log(string message)
        {
            if (EnableLog) Verse.Log.Message("[FC-Dbg] " + message);
        }
    }
}
