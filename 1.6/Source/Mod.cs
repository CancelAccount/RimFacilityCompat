using RimWorld;
using UnityEngine;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 设施兼容补丁 Mod 主入口。
    /// 点击 Mod 设置直接进入 Dialog_FacilityMatrix 双栏配置界面（无二级菜单）；
    /// 全局操作（全局启用/重置、预设导入导出、运行时开关）收纳于配置窗口底部的「全局操作」菜单。
    /// </summary>
    public class FacilityCompatMod : Mod
    {
        public static FacilityCompatSettings Settings = null!;

        public FacilityCompatMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<FacilityCompatSettings>();
            Settings.modDir = content.RootDir;
        }

        public override string SettingsCategory() => "FC.SettingsCategory".Translate();

        /// <summary>
        /// 设置入口：直接弹出配置窗口并关闭标准设置页（设置页面大小固定为700×900，在配置页面不太能装下）。
        /// WindowStack 以临时副本遍历窗口，在绘制回调内增删窗口安全；
        /// Dialog_ModSettings 关闭时（PreClose）会自动落盘设置。
        /// </summary>
        public override void DoSettingsWindowContents(Rect inRect)
        {
            // 配置窗口已在则不重复弹出
            if (Find.WindowStack.WindowOfType<Dialog_FacilityMatrix>() == null)
                Find.WindowStack.Add(new Dialog_FacilityMatrix(Settings));

            // 关闭标准设置页（不播关窗音，配置窗口随即接管）
            Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), false);
        }
    }
}
