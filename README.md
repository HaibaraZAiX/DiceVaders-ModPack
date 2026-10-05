# DiceVaders 体验优化 Mod 合集

《DiceVaders》（Steam AppID `3917700`）的 BepInEx 6 (IL2CPP) 插件合集。

**设计取向：把信息摊开给你看，而不是替你改数值。** 少数几个确实会改数值的开关，默认都是关的，你自己按需打开。

---

## 功能一览

| 功能 | 说明 | 默认 |
|---|---|---|
| **商店概率显示** | 在商店/神器界面旁竖排显示各稀有度当前的真实出货概率（含星界 2%） | 开 |
| **无限时空点 / 挪移** | 时空点、挪移每次被消耗后自动补回设定值 | 开 |
| **星座可刷新** | 星座界面加一个刷新按钮，可重掷当前星座选项 | 开 |
| **开局专长三选一** | 开局 6 秒后自动弹出「专长三选一」，其中必定有 2 个是当前指挥官的专属专长 | 开 |
| **游戏原生设置面板开关** | 在游戏自己的 Settings 界面里插入 4 行开关，随时开关上面这些功能 | 开 |
| **难度机制解除** | 拦掉 LockedSlots / Scarcity / CursedShop 等一批「削弱型」局内修正 | 开 |

---

## 安装

### 方式一：完整包（推荐，解压即用）

1. 打开 Steam → 右键 DiceVaders → 管理 → **浏览本地文件**
2. 把压缩包里的 `BepInEx/`、`dotnet/`、`winhttp.dll`、`doorstop_config.ini`、`.doorstop_version` **全部解压到游戏根目录**（覆盖同名文件）
3. 启动游戏

> ⚠️ 如果游戏里已经装过 BepInEx，只解压 `BepInEx/plugins/` 和 `BepInEx/config/` 就够了，别覆盖 `core/` 和 `dotnet/`。

### 方式二：纯插件包（已装 BepInEx 的人）

把 `BepInEx/plugins/*.dll` 丢进游戏的 `BepInEx/plugins/` 目录即可。

---

## 前置要求

- **BepInEx 6.0.0-be.788 (IL2CPP)** + Il2CppInterop —— 完整包已内置，纯插件包需要自己装
- .NET 6 运行时 —— 完整包已内置 `dotnet/` 目录
- 游戏版本：Unity 6000.3.10f1，IL2CPP，metadata v39

---

## 各项功能详解

### 商店概率显示

游戏真实的出货概率公式是这样的（反编译实证，见 `_analysis/商店与掉落系统_完整分析.md`）：

```
W = MIN((ActNumber - 1) * 5, 20) + 当前商店稀有度加成
P(传说) = clamp01(W*0.001 - 0.03)
P(稀有) = clamp01(W*0.003 + 0.0) - P(传说)
P(罕见) = clamp01(W*0.018 + 0.1) - P(稀有) - P(传说)
P(普通) = 1 - 剩下的
```

面板会实时显示这几个数字。**注意 `W=0` 时稀有是 0%（不是 3%）** —— 这是游戏设计，不是 bug。

配置项：`ShowPanel` 总开关、`FontSize` 字号、`OffsetX/RightOffsetX/OffsetY` 位置微调。

### 无限时空点 / 挪移

每次被消耗后自动补回。`CapAbove = true` 表示**低于**目标值才补（不会把你攒的超额数量砍掉）。

配置项：`ChronoAmount` / `BudgeAmount` 目标数量、`GiveChrono` / `GiveBudge` 分别开关。

### 星座可刷新

星座界面会出现一个刷新按钮（以及每个卡片上的单独刷新小按钮）。

配置项：`MaxRerollPerSession` 单次进界面的重掷上限（默认 20，防止刷爆）。

### 开局专长三选一

开局会弹出和「打完一幕」一样的专长三选一面板，**其中前两个固定是当前指挥官的专属专长**。

这是通过改写 `SpecialRewardPanel._artifacts` 实现的 —— 点选时游戏读的就是这个列表，所以走的是游戏**自己的**发放流程（`CreateLegendaryArtifactTask`），不是硬塞进槽位。

> 📌 **需要已解锁的专长才会出现** —— 专属专长本身要随等级解锁，没解锁时开局给不出来。这符合游戏设计。

配置项：`AutoOpenOnRunStart` 自动弹出开关、`AutoOpenDelay` 延迟秒数（默认 6，**别调太小**，开局有一长串初始化任务在跑）、`TestKey` 手动热键（默认 Z）。

### 游戏原生设置面板开关

不用去翻 cfg 文件 —— 游戏自己的设置界面里会多出这几行：

```
★ Mod：星座可刷新
★ Mod：无限时空点/挪移
★ Mod：显示稀有度概率
★ Mod：开局额外专长
```

点一下就能开关。

---

## 已知注意事项

1. **内存**：这游戏吃内存不小。如果启动时卡住或崩溃，先关掉浏览器/聊天软件腾出 2~3 GB。
2. **换局白块**：星座界面在换局后可能出现过白色色块（Addressables 卸载贴图导致），插件已针对性处理。
3. **专长面板的位置**：优先使用游戏自己的布局逻辑，不手动挪动 UI 槽位。

---

## 从源码构建

```powershell
# 需要 .NET SDK 6+
# 需要把游戏目录的 BepInEx/interop 和 BepInEx/unity-libs 作为引用来源
dotnet build src/ModKit/DiceVaders.ModKit.csproj -c Release
dotnet build src/ShopInfo/DiceVaders.ShopInfo.csproj -c Release
# ... 其余同理
```

构建产物在各自的 `bin/Release/`。**只需要拷 `DiceVaders.*.dll`**，同目录下其它 DLL 是引用复制品，不用管。

---

## 目录结构

```
src/
  ModKit/                共享库（跨插件开关注册表、UI 注入、IL2CPP 辅助）
  ShopInfo/              商店概率显示
  ResourceButtons/       无限时空点/挪移
  ConstellationTool/     星座刷新
  NativeSandbox/         游戏原生设置面板开关行
  ExtraRewardOption/     开局专长三选一
  QoL/                   难度机制解除
release/
  BepInEx/plugins/       编译好的插件
  BepInEx/config/        预配好的配置文件
```

---

## 许可

MIT，见 [LICENSE](LICENSE)。
