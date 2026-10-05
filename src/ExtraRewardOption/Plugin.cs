using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using UnityEngine;

namespace DiceVaders.ExtraRewardOption
{
    /// <summary>
    /// 把「幕奖励」的专长选项从 3 个增加到 4 个。
    ///
    /// ═══ 实证依据（Ghidra 反编译 SpecialRewardPanel.&lt;CreateNewDraft&gt;d__30.MoveNext，RVA 0x1D231B0）═══
    ///
    ///   // 第 1 处硬编码 3：从加权随机结果里取前 N 个
    ///   while ((count = list.Count(), i &lt; count &amp;&amp; i &lt; 3)) { result.Add(list[i]); i++; }
    ///
    ///   // 第 2 处硬编码 3：填充 N 个 UI 槽位
    ///   for (i = 0; i &lt; 3; i++) { ArtifactViews[i].xxx(result[i]); }
    ///
    /// 这两处都是内联在状态机 MoveNext 里的立即数，所以用 Harmony transpiler
    /// 把 IL 里的 ldc.i4.3 换成 ldc.i4.4。
    ///
    /// 同时要在运行前把面板上的 ArtifactViews 从 3 个克隆成 4 个 ——
    /// 否则第 2 个循环访问 index 3 会抛 ArgumentOutOfRange。
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders Extra Reward Option", "1.3.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.extrareward";

        internal static ManualLogSource Logger;
        internal static ConfigEntry<bool> TestHotkeyEnabled;
        internal static ConfigEntry<string> TestKey;
        internal static ConfigEntry<bool> AutoOpenOnRunStart;
        internal static ConfigEntry<float> AutoOpenDelay;

        public override void Load()
        {
            Logger = Log;
            ModKitLog.Sink = m => Logger.LogInfo(m);

            // ★★ 用户要的核心功能：开局自动弹出「三选一专长」面板 ★★
            //   走的完全是游戏自己的流程：
            //     SpecialRewardPanel.CreateNewDraft()   建候选 + 铺界面
            //     玩家点选 → OnPressArtifactSelect(i)
            //         → CreateLegendaryArtifactTask(选中的名字)   真正发放
            //         → StartTurnTask / LevelUpStarterArtifactTask
            //   （反编译 OnPressArtifactSelect 实证，RVA 0x1D21370）
            //
            // ★ v1.3.1：原来的「选项数 3→4」配置项已整体移除，理由见文件末尾注释 ——
            //   那个机制在 IL2CPP 下从未生效过，且用户要求保持三选一。
            AutoOpenOnRunStart = Config.Bind("2-开局自动弹出", "AutoOpenOnRunStart", true,
                "开局自动弹出专长三选一面板（不用打幕、也不用按热键）。\n" +
                "★ 其中前两个选项固定是当前指挥官的专属专长（需已解锁）。");
            AutoOpenDelay = Config.Bind("2-开局自动弹出", "AutoOpenDelay", 6f,
                new ConfigDescription("开新局后等多少秒再弹。\n" +
                    "★ 别设太小：开局有一长串初始化 task 在跑。",
                    new AcceptableValueRange<float>(1f, 30f)));

            TestHotkeyEnabled = Config.Bind("3-测试", "TestHotkey", true,
                "额外保留一个热键，随时手动弹出面板。");
            TestKey = Config.Bind("3-测试", "TestKey", "Z",
                "触发测试面板的按键。可填 Z / X / C / V / F1~F12。\n" +
                "★ 游戏占用了 F2（Bug 报告），别填 F2。");

            Logger.LogInfo("===== DiceVaders ExtraRewardOption v1.3.1 =====");

            // ★ v1.3.1：这里原来挂的是 RewardOptionPatch（选项数 3→4 的 transpiler）。
            //   该机制在 IL2CPP 下从未生效（理由见文件末尾注释），已整体移除。
            //   现在这个插件**不挂任何 Harmony 补丁** —— 全部功能靠
            //   TestHotkeyKeeper 里的 MonoBehaviour 轮询 + 直接改面板数据实现。

            // 热键宿主 / 自动弹出宿主
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<TestHotkeyKeeper>();
                var go = new GameObject("DiceVaders_ExtraRewardTest");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<TestHotkeyKeeper>();
                string key = (TestKey != null) ? TestKey.Value : "Z";
                Logger.LogInfo($"ExtraReward: 宿主已挂（手动热键 = {key}）");
            }
            catch (Exception e)
            {
                Logger.LogError("ExtraReward: 挂宿主失败 " + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    /// <summary>
    /// F8 测试热键：直接打开幕奖励面板。
    ///
    /// ★ 为什么要热键：打完整整一幕要很久，而幕奖励面板（SpecialRewardPanel）的
    ///   CreateNewDraft 是自包含的（反编译确认：它自己 new List&lt;ArtifactName&gt;()、
    ///   自己 new ArtifactFactory()、自己从 ContentGetter 取候选池），
    ///   所以直接起协程就能得到一个真实的奖励面板，用来验证选项数量补丁。
    /// </summary>
    public class TestHotkeyKeeper : MonoBehaviour
    {
        public TestHotkeyKeeper(IntPtr ptr) : base(ptr) { }

        private float _cooldown;

        private void Update()
        {
            // 面板铺好后把前两个选项换成当前指挥官的专属专长
            // （必须等游戏 CreateNewDraft 填完 _artifacts 才能改）
            if (_applyPerksAt > 0f && Time.realtimeSinceStartup >= _applyPerksAt)
            {
                _applyPerksAt = -1f;
                try { ForceCommanderPerks(_panel); }
                catch (Exception e) { ModKitLog.Warn("ExtraReward: ForceCommanderPerks 异常 " + e.Message); }
            }

            // ★★ 开局自动弹出「专长三选一」面板 ★★
            //   走的完全是游戏自己的流程：CreateNewDraft() 建候选 → 玩家点选
            //   → OnPressArtifactSelect(i) → CreateLegendaryArtifactTask 真正发放。
            if (Plugin.AutoOpenOnRunStart != null && Plugin.AutoOpenOnRunStart.Value)
            {
                var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
                IntPtr cur = IntPtr.Zero;
                try { if (ec != null) cur = ec.Pointer; } catch (Exception __e) { LogOnce.Warn("TestHotkeyKeeper.Update:140", __e); }

                if (cur == IntPtr.Zero)
                {
                    if (_lastEncounter != IntPtr.Zero)
                    {
                        _lastEncounter = IntPtr.Zero;
                        _autoOpened = false;
                        _autoOpenAt = -1f;
                    }
                }
                else if (cur != _lastEncounter)
                {
                    _lastEncounter = cur;
                    _autoOpened = false;
                    float d = (Plugin.AutoOpenDelay != null) ? Plugin.AutoOpenDelay.Value : 6f;
                    _autoOpenAt = Time.realtimeSinceStartup + d;
                    // ★ v1.3.1：统一的换局收尾 —— 查找缓存失效 + LogOnce 去重表复位
                    Il2CppHelpers.OnRunChanged();
                    Plugin.Logger?.LogInfo($"ExtraReward: 检测到新对局，{d:0.0}s 后自动弹出专长三选一");
                }
                else if (!_autoOpened && _autoOpenAt > 0f && Time.realtimeSinceStartup >= _autoOpenAt)
                {
                    // ★ 重试机制：开局那几秒游戏还在跑初始化 task，面板 show 出来会被它自己的流程盖掉。
                    //   实测日志：Show() 返回 IsActive=True，但玩家屏幕上什么都没有。
                    //   所以弹完之后延迟检查一次「面板是不是还在显示」，不在就再弹一次（最多 6 次）。
                    _autoOpenAt = -1f;
                    _autoAttempts++;
                    Plugin.Logger?.LogInfo($"ExtraReward: 自动弹出专长三选一（第 {_autoAttempts} 次）");
                    Trigger();
                    _recheckAt = Time.realtimeSinceStartup + 2.5f;
                }

                // 弹完 2.5 秒后检查有没有真的留在屏幕上
                if (_recheckAt > 0f && Time.realtimeSinceStartup >= _recheckAt)
                {
                    _recheckAt = -1f;
                    bool stillOpen = false;
                    try
                    {
                        var pnl = Il2CppHelpers.FindCached<StarVaders.SpecialRewardPanel>(0.5f);
                        if (pnl != null) stillOpen = pnl.IsActive;
                    }
                    catch (Exception __e) { LogOnce.Warn("TestHotkeyKeeper.Update:181", __e); }

                    if (stillOpen)
                    {
                        _autoOpened = true;
                        Plugin.Logger?.LogInfo("ExtraReward: 面板已稳定显示，停止重试");
                    }
                    else if (_autoAttempts < 6)
                    {
                        Plugin.Logger?.LogInfo($"ExtraReward: 面板被盖掉了（第 {_autoAttempts} 次没留住），2 秒后再试");
                        _autoOpenAt = Time.realtimeSinceStartup + 2f;
                    }
                    else
                    {
                        _autoOpened = true;
                        Plugin.Logger?.LogInfo("ExtraReward: 重试 6 次仍被盖掉，放弃（可手动按热键）");
                    }
                }
            }

            if (Plugin.TestHotkeyEnabled == null || !Plugin.TestHotkeyEnabled.Value) return;

            _cooldown -= Time.unscaledDeltaTime;
            if (_cooldown > 0f) return;

            bool pressed = false;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null) pressed = IsKeyPressed(kb, Plugin.TestKey != null ? Plugin.TestKey.Value : "Z");
            }
            catch (Exception __e) { LogOnce.Warn("TestHotkeyKeeper.Update:212", __e); }
            if (!pressed) return;

            _cooldown = 1.0f;
            Trigger();
        }

        /// <summary>
        /// 按键名取「这一帧是否刚按下」。
        /// ★ 只接 IL2CPP 类型 + string —— 注入类的方法签名里不能出现托管类型。
        /// </summary>
        private bool IsKeyPressed(UnityEngine.InputSystem.Keyboard kb, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            switch (key.Trim().ToUpperInvariant())
            {
                case "Z": return kb.zKey.wasPressedThisFrame;
                case "X": return kb.xKey.wasPressedThisFrame;
                case "C": return kb.cKey.wasPressedThisFrame;
                case "V": return kb.vKey.wasPressedThisFrame;
                case "B": return kb.bKey.wasPressedThisFrame;
                case "N": return kb.nKey.wasPressedThisFrame;
                case "M": return kb.mKey.wasPressedThisFrame;
                case "Q": return kb.qKey.wasPressedThisFrame;
                case "E": return kb.eKey.wasPressedThisFrame;
                case "R": return kb.rKey.wasPressedThisFrame;
                case "T": return kb.tKey.wasPressedThisFrame;
                case "Y": return kb.yKey.wasPressedThisFrame;
                case "F1": return kb.f1Key.wasPressedThisFrame;
                case "F3": return kb.f3Key.wasPressedThisFrame;
                case "F4": return kb.f4Key.wasPressedThisFrame;
                case "F5": return kb.f5Key.wasPressedThisFrame;
                case "F6": return kb.f6Key.wasPressedThisFrame;
                case "F7": return kb.f7Key.wasPressedThisFrame;
                case "F8": return kb.f8Key.wasPressedThisFrame;
                case "F9": return kb.f9Key.wasPressedThisFrame;
                case "F10": return kb.f10Key.wasPressedThisFrame;
                case "F11": return kb.f11Key.wasPressedThisFrame;
                case "F12": return kb.f12Key.wasPressedThisFrame;
                default: return kb.zKey.wasPressedThisFrame;   // 填了不认识的键就退回 Z
            }
        }

        private void Trigger()
        {
            try
            {
                var panel = Il2CppHelpers.FindCached<StarVaders.SpecialRewardPanel>(0.5f);
                if (panel == null)
                {
                    Plugin.Logger?.LogInfo("ExtraReward[TEST]: 找不到 SpecialRewardPanel（可能不在对局里）");
                    return;
                }

                Plugin.Logger?.LogInfo("ExtraReward[TEST]: 找到面板，开始手动铺界面");

                // ── 1) 激活整条父链 + 面板自身 ──
                try
                {
                    var t = panel.transform;
                    while (t != null)
                    {
                        var g = t.gameObject;
                        if (g != null && !g.activeSelf) g.SetActive(true);
                        t = t.parent;
                    }
                    if (!panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
                }
                catch (Exception e) { Plugin.Logger?.LogInfo("  激活父链失败: " + e.Message); }

                // ── 2) 界面切换：复现 SetupLevelUp 里的 SetActive 组合 ──
                //    反编译 SetupLevelUp.MoveNext 得到（字段偏移来自 dump.cs）：
                //      +0x48 PilotDraftScreen        -> false
                //      +0x38 LevelUpScreen            -> true
                //      +0x40 LegendaryDraftScreen     -> false
                //      +0x78 OverseerLevelupScreen    -> false
                TrySetActive(panel.PilotDraftScreen, false, "PilotDraftScreen");
                TrySetActive(panel.LevelUpScreen, true, "LevelUpScreen");
                TrySetActive(panel.LegendaryDraftScreen, false, "LegendaryDraftScreen");
                TrySetActive(panel.OverseerLevelupScreen, false, "OverseerLevelupScreen");

                // ── 2.5) ★ 关键补上的一步：调用基类 ABottomPanel.Show() ──
                //    上一版只设了子屏幕的显隐，没调 Show()，所以面板容器根本没被显示出来
                //    （日志显示四个字段全设对了、槽位也克隆了，但屏幕上什么都没有）。
                //    ABottomPanel 是 SpecialRewardPanel 的基类，提供：
                //      public virtual void Show();
                //      public virtual void Hide(bool instant = false);
                try
                {
                    panel.Show();
                    Plugin.Logger?.LogInfo($"  ABottomPanel.Show() 已调用（IsActive={panel.IsActive}）");
                }
                catch (Exception e) { Plugin.Logger?.LogInfo("  ABottomPanel.Show() 失败: " + e.Message); }

                // ── 3) 建选项（CreateNewDraft 是自包含的：自己 new List + ArtifactFactory）──
                // ★ v1.3.1 修复（审查 M29）：先停掉上一次没跑完的协程。
                //   自动弹出带重试（最多 6 次），旧实现没有协程引用、也没有在跑检查，
                //   实测一局连续重建了 5 次候选列表，每次都覆盖 _artifacts。
                try
                {
                    if (_draftCo != null) panel.StopCoroutine(_draftCo);
                }
                catch (Exception __e) { LogOnce.Warn("Trigger.StopCoroutine", __e); }

                // 注意类型：CreateNewDraft() 返回 Il2CppSystem.Collections.IEnumerator，
                // StartCoroutine 返回 UnityEngine.Coroutine —— 两者不能互相赋值。
                Il2CppSystem.Collections.IEnumerator routine = panel.CreateNewDraft();
                if (routine == null)
                {
                    ModKitLog.Warn("ExtraReward: CreateNewDraft() 返回 null，本次不铺界面");
                    return;
                }
                _draftCo = panel.StartCoroutine(routine);
                Plugin.Logger?.LogInfo("ExtraReward: 已启动 CreateNewDraft()");

                // ── 4) 记下面板与时间点：等 CreateNewDraft 填完 _artifacts 后再改前两个选项 ──
                _panel = panel;
                _applyPerksAt = Time.realtimeSinceStartup + 1.2f;
            }
            catch (Exception e)
            {
                ModKitLog.Warn("ExtraReward Trigger 异常: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private StarVaders.SpecialRewardPanel _panel;
        private float _applyPerksAt = -1f;
        private Coroutine _draftCo;

        // ★ 开局自动弹出用的状态
        private IntPtr _lastEncounter = IntPtr.Zero;
        private float _autoOpenAt = -1f;
        private bool _autoOpened = false;
        private float _recheckAt = -1f;
        private int _autoAttempts = 0;


        /// <summary>
        /// ★ 把「开局三选一」的前两个固定成当前指挥官自带的专属专长。
        ///
        /// ═══ 机制（全部来自反编译实证）═══
        ///
        /// 1) 点击时怎么取名字 —— OnPressArtifactSelect（RVA 0x1D21370）：
        ///      lVar8  = *(longlong *)(param_1 + 0xa0);            // SpecialRewardPanel._artifacts
        ///      uVar16 = *(undefined4 *)(lVar8 + 0x20 + i * 4);    // _artifacts[i]
        ///      CreateLegendaryArtifactTask → SetArg(task, 0x1c, boxed(uVar16))
        ///    → 直接改 _artifacts[i]，点选就真的发放对应的专长。
        ///
        /// 2) 「角色自带的专长」= ArtifactModel.CommanderUnique 等于当前指挥官的 artifact。
        ///    专长权重函数里也有旁证：HasCurrentCommander(model.CommanderUnique) ? 高权重 : 1.0
        ///
        /// 3) 显示必须同步：ArtifactViews[i].SetModel(ArtifactFactory.CreateArtifactModel(name))，
        ///    否则数据改了但图标还是旧的。
        /// </summary>
        private void ForceCommanderPerks(StarVaders.SpecialRewardPanel panel)
        {
            try
            {
                if (panel == null) return;
                var arts = panel._artifacts;
                if (arts == null) { Plugin.Logger?.LogInfo("ExtraReward[Commander]: _artifacts 为 null"); return; }
                if (arts.Count < 3) { Plugin.Logger?.LogInfo($"ExtraReward[Commander]: _artifacts 只有 {arts.Count} 项，跳过"); return; }

                var cur = StarVaders.CharacterSelectionController.CurrentCommander;
                Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 当前指挥官 = {cur}");

                // ── 0) 建一个【临时工厂】用于展示模型 ──
                // ★ v1.3.1 修复（审查 M27）：不要用本局真实的 ArtifactFactory ——
                //   CreateArtifactModel 会推进它的 ArtifactID 计数器，而 ArtifactSlots 正按
                //   Number 索引，每开一次面板最多吃掉 6 个编号。
                //   游戏自己的 CreateNewDraft 用的就是临时工厂 `new ArtifactFactory(0)`，这里对齐。
                StarVaders.ArtifactFactory scratch = null;
                try { scratch = new StarVaders.ArtifactFactory(0); }
                catch (Exception __e) { LogOnce.Warn("ForceCommanderPerks.scratchFactory", __e); }

                int poolType = -1;
                for (int i = 0; i < arts.Count; i++)
                {
                    try
                    {
                        var nm0 = arts[i];
                        StarVaders.ArtifactModel m0 = null;
                        if (scratch != null) m0 = scratch.CreateArtifactModel(nm0);
                        if (m0 != null)
                        {
                            Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 原候选[{i}] = {nm0}  type={(int)m0.ArtifactType} rarity={(int)m0.Rarity}");
                            if (poolType < 0) poolType = (int)m0.ArtifactType;
                        }
                        else
                        {
                            Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 原候选[{i}] = {nm0}  (建不出模型)");
                        }
                    }
                    catch (Exception e0) { LogOnce.Warn($"ExtraReward.原候选{i}", e0); }
                }
                Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 判定候选池类型 = {poolType}（0=Column 1=Power 2=Starter 3=Token 4=Pilot 5=Removed 6=Constellation）");

                // ── 1) 收集「同类型 + 当前指挥官专属 + 不在现有候选里」的 artifact ──
                //    ★ 只按 CommanderUnique 过滤会把「神器」（type=0 Column）也捞进来，
                //      把面板的「专长」挤掉 —— 所以强制同类型。
                //    ★ v1.3.1（审查 M26/M30）：还要排除**已经在候选列表里**的名字，
                //      否则同一个专长会出现两次（实机日志已复现过）。
                var uniques = new Il2CppSystem.Collections.Generic.List<StarVaders.ArtifactName>();
                var anyUnique = new Il2CppSystem.Collections.Generic.List<StarVaders.ArtifactName>();
                for (int t = 0; t <= 6; t++)
                {
                    Il2CppSystem.Collections.Generic.List<StarVaders.ArtifactModel> models = null;
                    try { models = StarVaders.ContentGetter.GetAllArtifactsOfType((StarVaders.ArtifactType)t); }
                    catch (Exception __e) { LogOnce.Warn($"GetAllArtifactsOfType[{t}]", __e); continue; }
                    if (models == null) continue;

                    for (int i = 0; i < models.Count; i++)
                    {
                        try
                        {
                            var m = models[i];
                            if (m == null) continue;
                            if (m.CommanderUnique == StarVaders.CommanderName.None) continue;
                            if (m.CommanderUnique != cur) continue;

                            var nm = m.ArtifactName;
                            if (!anyUnique.Contains(nm)) anyUnique.Add(nm);

                            if (poolType < 0 || (int)m.ArtifactType != poolType) continue;
                            if (arts.Contains(nm)) continue;          // 已经在候选里 → 不要重复摆
                            if (uniques.Contains(nm)) continue;
                            uniques.Add(nm);
                        }
                        catch (Exception __e) { LogOnce.Warn("ForceCommanderPerks.扫描专属", __e); }
                    }
                }

                Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 指挥官专属 artifact 共 {anyUnique.Count} 个，" +
                                       $"其中和候选池同类型且未在候选里的 {uniques.Count} 个");

                if (uniques.Count == 0)
                {
                    Plugin.Logger?.LogInfo("ExtraReward[Commander]: 没有可替换的专属 artifact，保持原样不动");
                    return;
                }

                int fill = uniques.Count < 2 ? uniques.Count : 2;
                for (int i = 0; i < fill; i++)
                {
                    var nm = uniques[i];
                    Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 选项[{i}] {arts[i]} → {nm}");
                    arts[i] = nm;

                    try
                    {
                        StarVaders.ArtifactModel built = null;
                        if (scratch != null) built = scratch.CreateArtifactModel(nm);
                        if (built != null && panel.ArtifactViews != null && i < panel.ArtifactViews.Count)
                        {
                            var v = panel.ArtifactViews[i];
                            if (v != null) { v.SetModel(built, false); Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 选项[{i}] 显示已刷新"); }
                        }
                    }
                    catch (Exception e2) { Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 刷新选项[{i}] 显示失败 {e2.Message}"); }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo($"ExtraReward[Commander] 失败: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>设置一个 GameObject 的显隐并记日志（null 就跳过）。
        /// ★ 只接 IL2CPP 类型 + bool + string。</summary>
        private void TrySetActive(GameObject go, bool on, string name)
        {
            try
            {
                if (go == null) { Plugin.Logger?.LogInfo($"  {name}: 字段为 null（跳过）"); return; }
                if (go.activeSelf != on) go.SetActive(on);
                Plugin.Logger?.LogInfo($"  {name}: {(on ? "显示" : "隐藏")}");
            }
            catch (Exception e) { Plugin.Logger?.LogInfo($"  {name} 设置失败: {e.Message}"); }
        }
    }


    // ═══════════════════════════════════════════════════════════════════════
    // 【已移除】「幕奖励选项数 3 → 4」整套机制
    //
    // 原实现有两部分：
    //   ① HarmonyTranspiler 把 SpecialRewardPanel.<CreateNewDraft>d__30.MoveNext
    //      里的 ldc.i4.3 替换成 ldc.i4.N
    //   ② HarmonyPrefix 在 CreateNewDraft 前把 ArtifactViews 克隆到 N 个
    //
    // ★ 为什么删掉（代码审查结论 S1）：
    //   ① **在 IL2CPP 下根本不生效** —— BepInEx 6 对 IL2CPP 程序集走原生 detour，
    //      Harmony transpiler 只能改写托管 stub（那个 48 字节的
    //      il2cpp_runtime_invoke thunk），改不到 GameAssembly 里的原生立即数。
    //      也就是这个功能从来没有真正生效过，实测面板一直只有 3 个选项。
    //   ② 全量替换 ldc.i4.3 会误伤同一状态机里无关的常量 3。
    //   ③ 用户明确要求「保持三选一」，这个功能本来也不需要。
    //
    // 保留的功能（真正在工作、用户要的）：
    //   开局自动弹出「专长三选一」面板 + 前两个固定为当前指挥官的专属专长。
    //   它走游戏自己的 SpecialRewardPanel._artifacts 列表 +
    //   CreateLegendaryArtifactTask，不依赖任何 transpiler。
    // ═══════════════════════════════════════════════════════════════════════
}