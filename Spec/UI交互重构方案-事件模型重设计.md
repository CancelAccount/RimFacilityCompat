# RimFacilityCompat UI 交互重构方案：事件模型重设计

> 状态：待实施（供新对话/新会话直接采用，本文档自包含，无需 prior context）
> 前置阅读：`Spec/UI重设计方案-双栏贴图矩阵.md`（视觉与布局设计，仍然有效）
> 取代范围：仅交互层（事件检测与会话状态），布局与视觉设计不变
> 修订 v1.1（2026-09-06，评审后）：§4.3 伪代码补帧级去重（否则照抄复刻"点了没反应"，违反 L2/F3）；hitMap 坐标系由"屏幕统一"改为"窗口局部"（与 `Mouse.IsOver` 兼容，保留 `IsInputBlockedNow` 遮挡/焦点保护）；修正批量涂抹条件运算符优先级 bug；§4.5/五/六补保留项与验证项（PostClose 落盘、ExtraOnGUI 光标附件、上层窗口遮挡、搜索框焦点）

---

## 一、背景

双栏贴图矩阵 UI（`Dialog_FacilityMatrix` + `IconGridSection`）在滚动视口深处出现**格子单击无响应**：
- 床铺/工作台等长列表类别中，视口上方格子正常、滚动后下方格子点击失效
- 悬停高亮、批量涂抹（PaintOver 轮询）始终正常
- 失效范围因类别而异（各页格子数不同）

多轮增量修补（拖动涂抹 → 按钮批量模式 → 右键反向 → 自实现点击检测）均未根治，交互状态字段层层叠加、状态机交错。决定**一次性重设计交互模型**。

---

## 二、已确认的事实（日志实锤，勿重走弯路）

以下结论来自 `[FC-Dbg]` 调试日志（工具已在 `Source/Utils/Debug/FCDebug.cs`），均已多轮验证：

| # | 事实 | 证据 |
|---|---|---|
| F1 | **命中链路自洽可靠**：`Mouse.IsOver`（= `rect.Contains(Event坐标)` && `!IsInputBlockedNow`）在失效场景下 8/8 次判定正确、坐标帧间稳定、无遮挡（绿框）、无布局漂移 | `[按下/释放] 命中 … 被遮挡=False` 序列 |
| F2 | **引擎 `ButtonInvisibleDraggable` 在相同条件下静默失败**（不返回 Pressed）。其按下/释放匹配依赖 IMGUI `controlID` 跨帧一致（`Widgets.buttonInvisibleDraggable_activeControl == controlID`），在「滚动视口 + `IsVisible` 条件裁剪 + 异常事件序列」下 controlID 分配漂移 → Pressed 丢失 | 同一位置轮询日志命中、引擎 API 无返回 |
| F3 | **`DoWindowContents` 每个事件调用一次，一帧内多次**：实测序列含 `Layout×2 → mouseDown → Layout → repaint`（同一帧出现两次 Layout 属异常但真实存在） | 日志 `事件类型=` 字段 |
| F4 | **旧版曾出现 viewRect.y 帧间振荡（126↔158，差=TabH）**，布局计算受重复调用/绘制顺序影响 | 旧版日志（v1） |
| F5 | ev/ui 双十字相差 18px = `windowRect`（含边框）与 `inRect` 的差，**是调试工具换算错误，非游戏坐标 bug**（`UI.MousePositionOnUIInverted - windowRect.position` 应再减边框） | 可视化对照 |
| F6 | **底部 `Widgets.ButtonText` 始终正常**：`GUI.Button` 在 mouseDown 同帧立即返回（按下即触发），无跨帧匹配需求 | 全程未失效 |
| F7 | entries 列表每帧由 `CreateEntry` 重建——**引用比较跨帧必失配，须按 defName 匹配** | 代码结构 |

---

## 三、经验教训（机制级，重构须遵守）

- **L1 禁止依赖跨帧 controlID 匹配的引擎 API**（`ButtonInvisibleDraggable` 等）做格子交互。先读反编译源码确认 API 内部机制再用（`Source_Decompiled/Assembly-CSharp/Verse/Widgets.cs` L1596-1640）。
- **L2 `DoWindowContents` 内所有副作用必须幂等**——它按事件多次调用（L3/F3）。每帧只允许一次的状态变更须自行去重（如按 defName 置空标记）。
- **L3 交互判定用 `Input` 轮询 + `Mouse.IsOver`，与绘制事件解耦**——F1 证明该组合 100% 可靠；`Event.current.Use()` 不影响轮询。
- **L4 增量修补 = 状态机复杂度失控**。拖动启动→批量按钮→右键反向→自实现点击，每步加字段（painting/paintState/paintSide/batchMode/batchState/pressDefName/pressPos…）。交互模型必须一次性设计、单一状态类管理。
- **L5 布局计算与绘制分离**。绘制回调中即时算布局会受重复调用影响（F4 振荡）。布局应先算后画（或纯函数）。
- **L6 坐标系每层显式验证**：root → windowRect（含 ~18px 边框）→ inRect → BeginScrollView 平移。跨层换算须以调试工具双十字对照验证。
- **L7 调试工具先行**：FCDebug（命中链路日志/三色命中框/双十字/布局漂移检测/选中状态集中日志）是本次唯一有效的定位手段，**重构期间保留**，行为验证后才移除插桩。
- **L8（工程流程）**：对同一文件的并行编辑会互相覆盖；每轮改动后必须 `rg` 复核全部目标点再构建。

---

## 四、重构方案：窗口级事件路由 + 按下即触发

### 4.1 架构原则

1. **绘制纯函数化**：`DrawCell(rect, entry, mark, selected)` 零事件逻辑，只按参数画。控件层不持有任何交互状态。
2. **事件集中路由**：窗口级统一处理鼠标，基于「当帧 rect 缓存」做命中测试，不散落在每个格子里。
3. **按下即触发（click = mouseDown）**：对齐 F6（底部按钮从不失灵的原因）——单击在按下帧立即生效，**彻底消除跨帧匹配**这一整类 bug。拖动阈值/释放匹配逻辑全部删除。
4. **会话状态单类**：所有交互状态（选择、批量模式）收敛进一个类，唯一入口改状态。

### 4.2 组件设计

```
Dialog_FacilityMatrix（组装 + 事件路由）
 ├─ IconGridSection     —— 纯绘制（角标/描边/高亮/裁剪），绘制时向 HitMap 注册 rect（滚动内容坐标 → 窗口局部坐标换算在注册回调内完成）
 ├─ GridHitMap          —— 当帧缓存：defName → Rect（窗口局部坐标系，绘制阶段填充；Dialog 实例字段，禁止 static）
 ├─ InteractionState    —— 选中项(selSide/selDef) + 批量模式(batch/batchState)，唯一状态类
 └─ FCDebug             —— 调试工具（保留）
```

**坐标系约定（hitMap，v1.1 修订）**：hitMap 必须存**窗口局部坐标**（与 `DoWindowContents` 内 `Event.current.mousePosition` 同系），而非屏幕坐标——理由：

1. `Mouse.IsOver` 的判定输入是当前 GUI 上下文的 `Event.current.mousePosition`；`RouteMouse` 在 `EndScrollView` 之后调用时上下文为窗口局部。若 hitMap 存屏幕坐标，两者不同系，命中必错位（F5 的 18px 假偏移即同类换算错误）。
2. 命中查询（`TryHit*`）统一走 `Mouse.IsOver(rect)`，**白赚 `IsInputBlockedNow` 的遮挡/焦点保护**（`MouseObscuredNow / CurrentWindowGetsInput / mouseOverScrollViewStack` 三重检查）；若因坐标系不匹配改用裸 `rect.Contains` + 屏幕坐标，FloatMenu（全局操作菜单）/对话框打开时点击会穿透到下层格子，引入新 bug。
3. 注册侧换算：`IconGridSection` 在 `BeginScrollView` 内拿到的是滚动内容坐标，注册时换算 `windowRect = cellRect; windowRect.position += viewRect.position - scroll;`（viewRect 为窗口局部坐标）。
4. 注册命中区域 = `iconRect`（与描边/角标视觉一致，不含下方标签行）。

### 4.3 事件路由流程（伪代码）

```csharp
// DoWindowContents：只画 + 收集 hitmap，不处理任何点击
void DoWindowContents(Rect inRect)
{
    hitMap.Clear();                                  // 每次调用重建（幂等，一帧多次调用安全）
    DrawTabs(); DrawSearch(); DrawPanels(hitmap);    // DrawPanels 内部逐格注册 rect（窗口局部坐标）
    DrawBottomBar();
    RouteMouse();                                    // 统一事件路由
}

/// 帧级去重标记：上次触发单击的帧号（-1 = 无）
/// F3 实测一帧内 DoWindowContents 被调用 4 次（Layout×2 → mouseDown → repaint），
/// 而 Input.GetMouseButtonDown 是帧级状态、4 次调用全为 true——不去重则单击触发
/// 2~4 次 OnCellClick，toggle 语义下偶数次 = 视觉上"点了没反应"（L2 的强制要求）
private int lastClickFrame = -1;

// 统一路由：Input 轮询，基于 hitMap 命中；hitMap 存窗口局部坐标，
// TryHit* 内部用 Mouse.IsOver(rect) 判定（含 IsInputBlockedNow 遮挡/焦点检查，
// FloatMenu/对话框打开时点击自动失效，防穿透）
void RouteMouse()
{
    bool lmb = Input.GetMouseButton(0), rmb = Input.GetMouseButton(1);

    // 单击：按下即触发（mouseDown 帧）+ 帧级去重；
    // 先查主设施栏再查附属设备栏（两栏 rect 不重叠）
    if (Input.GetMouseButtonDown(0) && Time.frameCount != lastClickFrame)
    {
        lastClickFrame = Time.frameCount;
        GUIUtility.keyboardControl = 0; // 对齐引擎按钮语义：点击格子让出键盘焦点（搜索框）
        if (hitMap.TryHitMain(out var def))       OnCellClick(SelSide.Main, def);
        else if (hitMap.TryHitAccessory(out def)) OnCellClick(SelSide.Accessory, def);
    }

    // 批量涂抹：模式激活且按住期间每帧对悬停格应用（左 = 目标状态，右 = 反向）
    // 注意括号必须显式括住 (lmb || rmb)：&& 优先于 ||，
    // 裸写 batch.active && lmb || rmb 会让右键在批量模式关闭时也穿透进涂抹分支
    if (batch.active && (lmb || rmb) && hitMap.TryHitOpposite(sel.side, out var def2))
        batch.Apply(def2, inverted: rmb);
}
```

### 4.4 交互语义（与现行一致，仅实现重写）

| 手势 | 行为 |
|---|---|
| 单击格子（对侧未选中） | 选中/再点取消 |
| 单击格子（对侧已选中） | 切换连接 |
| 批量按钮激活 | 进入批量模式（锁 draggable、光标附件勾/叉） |
| 批量模式：左键按住拖过对侧 | 应用批量状态 |
| 批量模式：右键按住拖过对侧 | 应用反向状态 |
| 再次点击批量按钮 / 取消选中 | 退出批量模式并落盘 |

### 4.5 修正项（顺手）

- FCDebug 的 ui 十字换算：`UI.MousePositionOnUIInverted - windowRect.position` 需再减边框（`inRect` 原点偏移），消除 18px 假偏移。
- 静态字段 `pressDefName/pressPos`（自实现点击遗留）随重写删除。

### 4.6 保留项（重写时勿删，v1.1 补）

- **`PostClose() → settings.Write()`**：批量涂抹中途关窗（X / ESC）不丢改动的兜底落盘。
- **`ExtraOnGUI` 的批量模式光标附件**：`GenUI.DrawMouseAttachment` 必须在根级 OnGUI 阶段调用——它用 `Event.current.mousePosition` 定位 `ImmediateWindow`，在 `DoWindowContents`（窗口局部坐标）里调用会画到错误的固定位置（已踩过的坑）。
- **落盘策略不变**：单击 toggle 即时 `Write`、批量退出统一落盘、`PostClose` 兜底。交互层重写不改变数据层写盘语义。
- **批量模式锁 `draggable`**：进入批量模式置 `draggable = false`（涂抹与 `GUI.DragWindow` 的时序冲突已验证），退出恢复。

---

## 五、实施清单

1. 新建 `Source/UI/InteractionState.cs`（状态类：选中 + 批量）
2. 新建 `Source/UI/GridHitMap.cs`（defName → Rect 缓存 + 命中查询；**Dialog 实例字段，禁止 static**；坐标系 = 窗口局部，命中查询统一 `Mouse.IsOver`，禁止裸 `rect.Contains`）
3. 重写 `IconGridSection`：删除全部事件代码与静态会话字段，只留绘制 + `RegisterRect` 回调（滚动内容坐标 → 窗口局部坐标换算在注册回调内完成，见 §4.2 坐标系约定）
4. 重写 `Dialog_FacilityMatrix`：删 `OnMain/OnAccessoryClick`、`OnMain/OnAccessoryPaintOver`、`pressDefName` 等，改为 `RouteMouse` + `OnCellClick`（含帧级去重 `lastClickFrame`，见 §4.3）
5. 保留：FCDebug 全部插桩（重构验证期）、三语 Keys、布局常量、视觉设计、§4.6 全部保留项（`PostClose` 落盘、`ExtraOnGUI` 光标附件、落盘策略、批量锁 `draggable`）
6. 每步构建 + `rg` 复核（教训 L8）

## 六、验证清单

- [ ] 床铺/工作台页滚动至最底部，每个格子单击均可选中/切换连接（原 bug 场景）
- [ ] 一帧多次 DoWindowContents 调用下无重复触发（日志确认单击只产生一条 `选中变更`——帧级去重生效的直接证据）
- [ ] 批量模式左右键涂抹、右键反向、退出落盘全部正常
- [ ] **非批量模式下按住右键拖过格子**：无任何连接变化（验证涂抹条件括号修正，右键不穿透批量开关）
- [ ] **上层窗口遮挡**：全局操作 FloatMenu / 导入对话框打开时点击底层格子无效、无穿透（验证 `Mouse.IsOver` 的 `IsInputBlockedNow` 检查生效）
- [ ] **搜索框聚焦时点击格子**：连接正常切换、键盘焦点让出（`keyboardControl = 0`），无键盘误输入进搜索框
- [ ] **窗口拖动/缩放后**：hitMap 命中仍准确（坐标系随窗口几何每帧重建）
- [ ] 双十字重合（修正换算后）
- [ ] 关闭调试开关（DebugBuild=false）构建无警告，行为一致
