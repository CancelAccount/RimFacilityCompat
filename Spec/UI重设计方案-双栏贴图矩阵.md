# RimFacilityCompat UI 重设计方案：双栏贴图矩阵

> 文档版本：v1.0（2026-09-06）
> 状态：待评审 / 待实施
> 依赖文档：[修复技术方案-运行时声明式注入.md](修复技术方案-运行时声明式注入.md)（本方案的交互依赖其"即时生效"注入机制）

---

## 1. 现状 UI 的问题

当前设置界面（[Mod.cs#L46-L375](file:///e:/steam/steamapps/common/RimWorld/Mods/RimFacilityCompat/Source/Mod.cs#L46-L375)）为**四层嵌套折叠树**：

```
类别（按钮，可折叠）
  └─ mod 来源分组（按钮，可折叠）
      └─ 设施行（6 个小按钮横排：全开/全关/手动/重置/复制/粘贴）
          └─ 手动模式展开的目标列表（按 mod 再分组）
```

具体痛点：

| # | 问题 | 后果 |
|---|---|---|
| U1 | 找到一个配置项需要展开 2~3 层折叠 | 定位成本高 |
| U2 | 每行 6 个按钮（总宽约 380px），文字按钮 60px 宽 | 列表可视区被严重压缩，1366×768 下几乎不可用 |
| U3 | 手动模式下再嵌套一层目标列表 → 四层结构 | 认知负担大，滚动位置易错乱 |
| U4 | 纯文本 defName + label，无贴图 | 辨识度低，玩家需逐个阅读文字 |
| U5 | `CalcHeight` 手工累加行高，与实际控件行高不一致（BUG-8） | 长列表底部被裁切 |
| U6 | 折叠状态、滚动位置由多个临时集合（`expandedCategories`/`expandedGroups`/`manualExpanded`）维护 | 状态管理分散，易出不一致 |

## 2. 设计目标

1. **一眼看全**：设施与目标建筑（"附属设备"）双侧平铺，无深层折叠；
2. **贴图直观**：全部使用物品贴图（`ThingDef.uiIcon`）展示，悬停显示文字详情；
3. **低操作成本**：任意连接关系 ≤ 2 次点击（选中一侧 → 点另一侧图标）；
4. **即时生效**：切换即刻应用（依赖修复方案的 `ApplyInjection`），无保存/重启心智负担；
5. **屏幕自适配**：全屏窗口、可缩放，两侧独立滚动，支持搜索过滤。

## 3. 布局线框图

```
┌──────────────────────────────────────────────────────────────────┐
│ 设施兼容配置                                          [—] [✕]    │
├──────────────────────────────────────────────────────────────────┤
│ 类别: [床] [工作台] [研究台] [基因组装台] [其他建筑] [未分配]        │  ← Tab 行
├──────────────────────────────────────────────────────────────────┤
│ [搜索设施🔍 ____________]           │  [搜索目标🔍 ____________]   │
├────────────────────────────────┤ ├───────────────────────────────┤
│ 主设施 (12)                 │ │ 附属设备 (37)                  │
│ ── Core ────────────────       │ │  ── Core ──────────────       │
│ [🛏icon] [💡icon] [🪞icon]      │ │  [🛏] [🛏] [🛏] [🛏] [🛏]    │
│  床头柜   台灯    梳妆台         │ │   ✓      ✓      ✗      ✓  ✓  │
│ ── VFE ────────────────        │ │  ── VFE ──────────────        │
│ [📦icon] [📦icon]              │ │  [🛏] [🛏]                    │
│  皇家衣柜  ...                  │ │   ✓     ✗                     │
│         ⋮（独立滚动）           │ │         ⋮（独立滚动）          │
├────────────────────────────────┴─┴───────────────────────────────┤
│ 选中: 皇家衣柜 → 目标 12/37 已连接                                │
│ [全部连接] [全部断开] [重置原始] │ [复制] [粘贴] │ [导出] [导入]    │
└──────────────────────────────────────────────────────────────────┘
```

- **左栏 = 可连接设施**（`CompProperties_Facility`，按 mod 来源分节）；
- **右栏 = 附属设备/目标建筑**（`CompProperties_AffectedByFacilities` 所属 def，按 mod 来源分节）；
- 类别 Tab 内容 = 现有 `CategoryInfo`，数据模型不变。

## 4. 交互模型：单侧选中驱动对侧状态

两侧结构对称，任一时刻**恰好一侧有一项选中**（高亮描边），另一侧图标实时显示与选中项的连接状态：

```
选中左栏设施 F:
  右栏目标 T 的图标渲染 = ShouldLink(类别, F, T) ? 原色+绿角标 : 灰度+红角标
  点击右栏 T → ToggleLink(类别, F, T) → ApplyInjection + Write → 图标即时翻转

选中右栏目标 T:（对称）
  左栏设施 F 的图标渲染 = ShouldLink(类别, F, T) ? 原色+绿角标 : 灰度+红角标
  点击左栏 F → 同上
```

规则：

1. 点击未选中侧的图标 = 切换该连接关系；
2. 点击已选中侧的另一图标 = 改变选中项（不触发连接变化）；
3. **无选中态**：初次打开默认选中左栏第一个设施；点击选中项自身 = 取消选中 → 对侧恢复原色无角标（纯浏览模式）；
4. 切换类别 Tab → 保留无选中态，重置两侧滚动；
5. 底部操作栏**作用于当前选中项**（全部连接/断开/重置原始/复制粘贴），无选中时禁用（置灰）。

> 该模型用"选中 → 矩阵行/列可视化"替代 N×M 完整矩阵（几百个格子无法平铺），是直观性与空间的最优平衡。

## 5. 视觉规范

| 元素 | 规范 |
|---|---|
| 图标格子 | `Widgets.DefIcon`，基准 56×56px，格间距 6px（常量集中定义，见 §8） |
| 已连接（当前选中视角下） | 原色图标 + 右上角 12×12 绿色圆点 |
| 已排除 | `DefIcon` 传灰色 `color` 参数（去饱和）+ 右上角红色叉 |
| 未参与（对侧无选中时） | 原色、无角标 |
| 选中项 | `Widgets.DrawHighlight` 高亮 + 2px 描边（类别色） |
| mod 分节标题 | 22px 行，文字 + 细分隔线，颜色沿用现 `SourceGroupColor` 体系 |
| 图标下方标签 | 图标名（`def.label`），超出省略，完整信息进 Tooltip |
| Tooltip | 名称 / defName / mod 来源 / 当前与对侧选中项的连接状态，`TooltipHandler.TipRegion` |
| 角标素材 | 不引入外部贴图：圆点用 `GUI.DrawTexture` + 纯色小圆（`TexUI.Highlight`类内置资源）或直接 `Widgets.DrawBoxSolid`；叉用文本 "✕"（GUI style）——实施时取内置资源优先，**禁止新增贴图文件** |

## 6. API 依据（已验证 1.6 反编译源码）

| API | 用途 |
|---|---|
| [Widgets.DefIcon(Rect, Def, scale, color, ...)](file:///e:/steam/steamapps/common/RimWorld/Source_Decompiled/Assembly-CSharp/Verse/Widgets.cs#L74) | def 图标绘制，支持灰度（color 参数）与缩放 |
| [Widgets.ButtonImageWithBG](file:///e:/steam/steamapps/common/RimWorld/Source_Decompiled/Assembly-CSharp/Verse/Widgets.cs#L1557) | 带底纹的图标按钮 |
| `Widgets.DrawHighlight` / `Widgets.DrawBoxSolid` | 选中高亮 / 角标 |
| `TooltipHandler.TipRegion` | 悬停详情 |
| `Widgets.BeginScrollView` | 两侧独立滚动 |
| `Widgets.TextField` + `Text.CurTextFieldStyle` | 搜索框 |
| `Window.draggable = true` + `Window.resizeable = true` | 可拖动缩放窗口 |

> `DefIcon` 内部已处理建筑 uiIcon、iconColor、无图 def 的占位（`drawPlaceholder`），无需自行兜底贴图缺失。

## 7. 组件与文件结构

```
Source/
  UI/
    Dialog_FacilityMatrix.cs   # 主窗口：Tab/双栏/底栏编排、选中状态机
    IconGridSection.cs         # 图标网格控件：单侧一节（mod 分组）的绘制/命中/搜索过滤
  Mod.cs                       # DoSettingsWindowContents 简化为：标题 + [打开配置界面] + 状态摘要
```

- `IconGridSection`：输入（def 列表、角标谓词、命中回调），输出（内容高度）；不持有业务状态 → 可复用于两侧；
- 选中状态机集中于 `Dialog_FacilityMatrix`：`SelectedSide`（左/右/无）+ `SelectedDefName`；
- 旧 UI 的折叠集合（`expandedCategories`/`expandedGroups`/`manualExpanded`）与 `CalcHeight` 全部删除（U6/U5 根除）。

## 8. 布局计算

```csharp
/// UI 尺寸常量（集中定义，禁止魔法数字散落）
private const float IconSize   = 56f;   // 图标格子边长
private const float IconGap    = 6f;    // 格间距
private const float CellPitch  = IconSize + IconGap;
private const float LabelH     = 18f;   // 图标下文字行高
private const float CellH      = IconSize + LabelH + IconGap;
private const float SectionHdrH = 22f;  // mod 分节标题行高

/// 某节内容高度（用于滚动视口总量）
float SectionHeight(int count, float width)
{
    int cols = Mathf.Max(1, Mathf.FloorToInt(width / CellPitch));
    int rows = Mathf.CeilToInt(count / (float)cols);
    return SectionHdrH + rows * CellH;
}
```

- 窗口 `InitialSize` 取 `UI.screenWidth * 0.85 × screenHeight * 0.85`，最小尺寸 960×600；
- 两侧宽度对半，中间 4px 分隔线；
- 性能：格子总量 = 设施数 + 目标数（数百级），顺序绘制 + 视口外跳过（`rect.yMin > viewBottom` 剪裁判断），单帧毫秒级。

## 9. 与修复方案的衔接

| 环节 | 依赖 |
|---|---|
| toggle 即时生效 | `ApplyInjection()`（修复方案 §4.3）在每次 `ToggleLink` 后调用 + `Settings.Write()` |
| 底部"全部连接/断开/重置原始" | 复用 `EnableAllTargets` / `DisableAllTargets` / `ResetFacilityToOriginal` |
| 复制/粘贴 | 复用 `FacilityClipboard`（其跨类别过滤逻辑不变，粘贴后同样即时 `ApplyInjection`） |
| 导入/导出 | 复用 `FacilityPreset`，导入完成后即时 `ApplyInjection` |
| 状态摘要 | 底部"选中: X → 目标 n/m 已连接"由 `ShouldLink` 实时统计 |

旧 `DoSettingsWindowContents` 内嵌的三层树 UI 代码全部移除，Mod 设置页仅保留入口按钮与"运行时注入已生效"状态行（首次启动后显示注入统计）。

## 10. 变更清单

| 操作 | 内容 |
|---|---|
| 新增 | `Source/UI/Dialog_FacilityMatrix.cs`（主窗口 + 选中状态机，约 350 行） |
| 新增 | `Source/UI/IconGridSection.cs`（图标网格控件，约 150 行） |
| 重写 | `Mod.DoSettingsWindowContents`：入口按钮 + 状态摘要（删约 300 行） |
| 删除 | `expandedCategories` / `expandedGroups` / `manualExpanded` / `CalcHeight` / `DrawCategory` / `DrawSourceGroup` / `DrawFacilityRow` |
| 保留 | 数据层全部不动：`CategoryInfo` / `BuildingTarget` / `excludedTargets` / `ShouldLink` / `ToggleLink` / 剪贴板 / 预设导入导出 |
| 翻译 | `Languages/*/Keyed/Keys.xml` 新增窗口标题、搜索占位、底栏按钮、状态行等 key；废弃 key 保留一轮后清理 |

## 11. 测试清单

| # | 场景 | 预期 |
|---|---|---|
| UI-T1 | 1366×768 / 2560×1440 下打开 | 双栏完整显示、无裁切、滚动正常 |
| UI-T2 | 选中设施 → 点击目标图标 | 连接翻转、图标角标/灰度即时变化；放置蓝图预览同步（联动修复方案 T3） |
| UI-T3 | 切换为选中目标 → 点击设施图标 | 对称行为一致 |
| UI-T4 | 点击选中项自身 | 取消选中，对侧恢复纯浏览态 |
| UI-T5 | 搜索"床头柜"（label）与 defName | 两侧列表正确过滤，清空恢复 |
| UI-T6 | 无选中时底栏批量按钮 | 全部置灰不可点 |
| UI-T7 | 复制 A 设施配置 → 粘贴到 B（跨类别） | 沿用剪贴板过滤语义，图标状态即时更新 |
| UI-T8 | 类别 Tab 切换 | 列表刷新、滚动重置、无残留选中 |
| UI-T9 | 悬停任意图标 | Tooltip 内容完整（名称/defName/来源/状态） |
| UI-T10 | 无贴图 def（个别 mod） | `DefIcon` 占位渲染，不报错 |
