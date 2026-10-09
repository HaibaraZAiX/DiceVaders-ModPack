# DiceVaders 体验优化 MOD 合集

适用于《DiceVaders》（Steam AppID `3917700`），基于 BepInEx 6 / Unity IL2CPP。

**当前源码与内置 DLL：1.0.5-rc2 预发布测试版。** 代码审查、编译和离线边界回归已通过，游戏内效果正在验收。下载见 [Releases](https://github.com/HaibaraZAiX/DiceVaders-ModPack/releases)。

## 功能与默认值

| 功能 | 行为 | 默认 |
|---|---|---|
| 商店概率面板 | 分别显示商品棋子与神器概率；星界另列，教程中为 0 | 开 |
| 无限时空点 / 挪移 | 分别维持目标值，默认各 10 | 开 |
| 星座刷新 | 全部刷新与卡片单项刷新；单次界面预算默认 20 | 开 |
| 开局专长三选一 | 默认至少等待 2 秒及引擎空闲，保留并补足合法专属候选 | 开 |
| 原生沙盒设置行 | 四行 MOD 开关，专长自动弹出与热键独立 | 开 |
| QoL | 掉落权重、商店权重、全解锁三个独立选项 | 全关 |

普通角色最多补足两个合法专属，融合角色按合法候选补足；保留已有候选位置，不保证“前两个固定专属”。手动热键默认 Z，自动面板正常完成后不会重复弹出。

`CapAbove = true` 会将高于目标的资源也压回目标；设为 `false` 只补不足部分，保留超额数量。

## 安装与升级

1. 关闭游戏，备份存档、`BepInEx/plugins` 与 `BepInEx/config`。
2. 已安装 BepInEx 的用户下载纯插件包，将其中 7 个 DLL 放入游戏的 `BepInEx/plugins`；首次安装可用完整包。
3. 已有配置可保留。包内 cfg 是默认模板，覆盖会重置同名设置；新增选项由插件补齐。移除的 `SourceButtonName`、`ShowPoolSummary` 已不生效。
4. 启动游戏，按 [测试清单](TESTING.md) 检查设置行、星座实际效果、专长选择与换局。

完整包只用于首次配置运行时。升级插件时使用纯插件包，保留现有 `core`、`interop` 与 `dotnet`。

## 前置与版本

- BepInEx `6.0.0-be.788`（IL2CPP）及 .NET 6 运行时。
- 当前核验游戏：Unity `6000.3.10f1`、metadata v39；GameAssembly 哈希见 [版本清单](release_plan.json)。游戏更新后需重新核查原生接口。
- 第四专长扩容没有实现；本包保持原生三选一，不包含停用实验和 DevTools。

## 从源码构建

需要 .NET SDK 和本机游戏生成的 BepInEx interop。PowerShell 执行：

```powershell
./scripts/build.ps1 -GameDir 'C:\Program Files (x86)\Steam\steamapps\common\DiceVaders'
```

构建先生成 ModKit，再生成其余六个插件，输出各模块的 `bin/Release`。仅编译，不自动部署。

## 验证状态

两轮审查共保留并通过 139 项回归，本地 13 个工程均为 0 警告、0 错误。测试使用实际方法体与受控 Unity/原生边界桩，不能替代实机测试。仓库只包含发布的 7 个模块。

非连续解锁星座的槽位映射、原生协程与本地化时序、动画和实际点击效果仍待验收。遇到未知提交结果会阻挡重复操作，避免继续破坏当前局。

参见 [更新记录](CHANGELOG.md)、[验证摘要](docs/verification-1.0.5-rc2.json)及 [测试清单](TESTING.md)。早期审查报告保留为历史记录，以当前版本说明为准。
