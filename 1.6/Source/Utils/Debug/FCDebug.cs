using System;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 调试工具中枢（排查单击失灵等问题）：
    /// 全局版本常量 DebugBuild 定夺调试/发行版——true 时日志与可视化工具可通过
    /// “全局操作”菜单开关；false 时 EnableLog/EnableDraw 恒为 false，所有插桩短路为零开销。
    /// 判定链打点针对 ButtonInvisibleDraggable：其内部用 Input 轮询判定，
    /// 悬停高亮/批量涂抹/单击三条路径坐标系若不一致，日志与边框颜色会直接暴露。
    /// 另含 TimeScope 性能计时工具：using 包裹待测代码段，Dispose 输出毫秒耗时。
    /// </summary>
    public static class FCDebug
    {
        /// <summary>全局版本标志：由编译配置决定——Debug 编译为 true（含日志与可视化工具），
        /// Release 编译为 false（所有插桩短路为零开销）。无需手动修改。</summary>
#if DEBUG
        public static readonly bool DebugBuild = true;
#else
        public static readonly bool DebugBuild = false;
#endif
        /// <summary>日志关闭时复用的空作用域单例（避免每次分配）</summary>
        private static readonly IDisposable noopScope = new NoopScope();
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
        
        /// <summary>计时空作用域：日志关闭时 TimeScope 返回此单例，using 零开销</summary>
        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }

        /// <summary>计时作用域：构造启动 Stopwatch，Dispose 输出耗时（毫秒，1 位小数）。
        /// 输出直接走 Verse.Log 而非本类 Log()——后者受运行时菜单开关 EnableLog 门控，
        /// 而启动计时发生在主菜单出现前（用户无法打开开关），依赖它会丢失全部启动数据。</summary>
        private sealed class TimedScope : IDisposable
        {
            private readonly string label;
            private readonly System.Diagnostics.Stopwatch stopwatch;

            public TimedScope(string label)
            {
                this.label = label;
                stopwatch = System.Diagnostics.Stopwatch.StartNew();
            }

            public void Dispose()
            {
                stopwatch.Stop();
                Verse.Log.Message($"[FC-Dbg] [计时] {label}：{stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            }
        }

        /// <summary>
        /// 性能计时作用域：using (FCDebug.TimeScope("标签")) 包裹待测代码段，
        /// 作用域结束输出耗时。挂编译期 DebugBuild 门控（而非运行时 EnableLog）：
        /// 启动计时发生在主菜单出现前，用户无法打开菜单开关，须调试版即输出；
        /// 发行版（DebugBuild=false）返回空单例，零开销。启动计时与 UI 单击计时统一走本入口。
        /// </summary>
        public static IDisposable TimeScope(string label)
        {
            return DebugBuild ? new TimedScope(label) : noopScope;
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
