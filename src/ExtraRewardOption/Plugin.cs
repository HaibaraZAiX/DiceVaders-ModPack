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
    [BepInPlugin(Guid, "DiceVaders Extra Reward Option", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.extrareward";

        internal static ManualLogSource Logger;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> OptionCount;
        internal static ConfigEntry<bool> TestHotkeyEnabled;
        internal static ConfigEntry<string> TestKey;
        internal static ConfigEntry<bool> AutoOpenOnRunStart;
        internal static ConfigEntry<float> AutoOpenDelay;

        public override void Load()
        {
            Logger = Log;
            ModKitLog.Sink = m => Logger.LogInfo(m);

            Enabled = Config.Bind("1-开关", "Enabled", true,
                "把幕奖励（专长选择）的选项数量从 3 增加到 4。\n" +
                "★ 想保持三选一的话把它关掉 —— 关掉只停用数量补丁，不影响下面的「开局自动弹出」。");
            OptionCount = Config.Bind("1-开关", "OptionCount", 3,
                new ConfigDescription("目标选项数量。原版是 3。填 3 = 不改。", new AcceptableValueRange<int>(3, 6)));

            // ★★ 用户要的核心功能：开局自动弹出「三选一专长」面板 ★★
            //   走的完全是游戏自己的流程：
            //     SpecialRewardPanel.CreateNewDraft()   建候选 + 铺界面
            //     玩家点选 → OnPressArtifactSelect(i)
            //         → CreateLegendaryArtifactTask(选中的名字)   真正发放
            //         → StartTurnTask / LevelUpStarterArtifactTask
            //   （反编译 OnPressArtifactSelect 实证，RVA 0x1D21370）
            AutoOpenOnRunStart = Config.Bind("2-开局自动弹出", "AutoOpenOnRunStart", true,
                "开局自动弹出专长三选一面板（不用打幕、也不用按热键）。");
            AutoOpenDelay = Config.Bind("2-开局自动弹出", "AutoOpenDelay", 6f,
                new ConfigDescription("开新局后等多少秒再弹。\n" +
                    "★ 别设太小：开局有一长串初始化 task 在跑。",
                    new AcceptableValueRange<float>(1f, 30f)));

            TestHotkeyEnabled = Config.Bind("3-测试", "TestHotkey", true,
                "额外保留一个热键，随时手动弹出面板。");
            TestKey = Config.Bind("3-测试", "TestKey", "Z",
                "触发测试面板的按键。可填 Z / X / C / V / F1~F12。\n" +
                "★ 游戏占用了 F2（Bug 报告），别填 F2。");

            Logger.LogInfo("===== DiceVaders ExtraRewardOption v1.3.0 =====");

            try
            {
                var harmony = new Harmony("dicevaders.extrareward");
                harmony.PatchAll(typeof(RewardOptionPatch));
                Logger.LogInfo("ExtraReward: Harmony 补丁已挂");
            }
            catch (Exception e)
            {
                Logger.LogInfo("ExtraReward: 挂补丁失败 " + e.GetType().Name + ": " + e.Message);
            }

            // 测试热键宿主
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<TestHotkeyKeeper>();
                var go = new GameObject("DiceVaders_ExtraRewardTest");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<TestHotkeyKeeper>();
                Logger.LogInfo("ExtraReward: 测试热键已挂（F8）");
            }
            catch (Exception e)
            {
                Logger.LogInfo("ExtraReward: 挂热键失败 " + e.GetType().Name + ": " + e.Message);
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
            // 延迟重排（等游戏把 3 个槽排完）
            if (_relayoutAt > 0f && Time.realtimeSinceStartup >= _relayoutAt)
            {
                _relayoutAt = -1f;
                // ★ 先把前两个选项换成当前指挥官的专属专长（必须在 CreateNewDraft 填完 _artifacts 之后）
                try { ForceCommanderPerks(_panel); }
                catch (Exception e) { Plugin.Logger?.LogInfo("ExtraReward: ForceCommanderPerks 异常 " + e.Message); }
                Relayout();
            }

            // ★★ 开局自动弹出「专长三选一」面板 ★★
            //   走的完全是游戏自己的流程：CreateNewDraft() 建候选 → 玩家点选
            //   → OnPressArtifactSelect(i) → CreateLegendaryArtifactTask 真正发放。
            if (Plugin.AutoOpenOnRunStart != null && Plugin.AutoOpenOnRunStart.Value)
            {
                var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
                IntPtr cur = IntPtr.Zero;
                try { if (ec != null) cur = ec.Pointer; } catch { }

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
                    catch { }

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
            catch { }
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
                panel.StartCoroutine(panel.CreateNewDraft());
                Plugin.Logger?.LogInfo("ExtraReward[TEST]: 已启动 CreateNewDraft()");

                // ── 4) 记下时间点：稍后重排 4 个槽 ──
                _panel = panel;
                _relayoutAt = Time.realtimeSinceStartup + 1.2f;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo("ExtraReward[TEST] 异常: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private StarVaders.SpecialRewardPanel _panel;
        private float _relayoutAt = -1f;
        private bool _dumpedLayout;

        // ★ 开局自动弹出用的状态
        private IntPtr _lastEncounter = IntPtr.Zero;
        private float _autoOpenAt = -1f;
        private bool _autoOpened = false;
        private float _recheckAt = -1f;
        private int _autoAttempts = 0;

        /// <summary>
        /// 等游戏把 3 个槽排好之后，把 4 个槽重新均匀排开。
        ///
        /// ★ 为什么需要：反编译 CreateNewDraft 显示游戏是【用代码设置 localPosition】
        ///   来排这 3 个槽的（Transform::get/set_localPosition_Injected），
        ///   我克隆出来的第 4 个不在它的排布表里 → 会压在右边的「跳过换钱」按钮上。
        ///   实测症状：第 3、4 个槽挤在一起，第 4 个叠着绿色的钱图标。
        ///
        /// ★ 做法：读前 3 个槽的真实 localPosition 算步长，再以中间槽为中心重排 4 个。
        /// </summary>
        private void Relayout()
        {
            try
            {
                var panel = _panel;
                if (panel == null) return;
                var views = panel.ArtifactViews;
                if (views == null || views.Count < 2) return;

                int n = views.Count;

                // ── 先 dump 真实布局，便于诊断 ──
                if (!_dumpedLayout)
                {
                    _dumpedLayout = true;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            var v = views[i];
                            if (v == null) { Plugin.Logger?.LogInfo($"ExtraReward[Layout]: [{i}] null"); continue; }
                            var t = v.transform;
                            var sc = v.transform.localScale;
                            Plugin.Logger?.LogInfo(
                                $"ExtraReward[Layout]: [{i}] '{v.gameObject.name}' " +
                                $"localPos=({t.localPosition.x:0.#},{t.localPosition.y:0.#},{t.localPosition.z:0.#}) " +
                                $"scale=({sc.x:0.##},{sc.y:0.##}) active={v.gameObject.activeSelf}");
                        }
                        catch (Exception e) { Plugin.Logger?.LogInfo($"ExtraReward[Layout]: [{i}] 读取失败 {e.Message}"); }
                    }
                    try
                    {
                        var par = views[0].transform.parent;
                        if (par != null)
                            Plugin.Logger?.LogInfo($"ExtraReward[Layout]: 父节点 '{par.name}' 子对象数={par.childCount}");
                    }
                    catch { }
                }

                // ── 算步长：优先用 [0]→[1]，退化到 [1]→[2] ──
                float step = 0f;
                try
                {
                    var a = views[0].transform.localPosition;
                    var b = views[1].transform.localPosition;
                    step = b.x - a.x;
                }
                catch { }
                if (Mathf.Abs(step) < 1f)
                {
                    try
                    {
                        var b = views[1].transform.localPosition;
                        var c = views[2].transform.localPosition;
                        step = c.x - b.x;
                    }
                    catch { }
                }
                if (Mathf.Abs(step) < 1f)
                {
                    Plugin.Logger?.LogInfo("ExtraReward[Layout]: 步长算不出来，放弃重排");
                    return;
                }

                // ── 以原中间槽为中心重排 n 个 ──
                float centerX = 0f;
                try { centerX = views[(n - 1) / 2].transform.localPosition.x; }
                catch { }
                float startX = centerX - step * ((n - 1) / 2f);
                float baseY = 0f, baseZ = 0f;
                try
                {
                    var p0 = views[0].transform.localPosition;
                    baseY = p0.y; baseZ = p0.z;
                }
                catch { }

                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var v = views[i];
                        if (v == null) continue;
                        v.transform.localPosition = new Vector3(startX + step * i, baseY, baseZ);
                    }
                    catch (Exception e) { Plugin.Logger?.LogInfo($"ExtraReward[Layout]: 排第 {i} 个失败 {e.Message}"); }
                }

                Plugin.Logger?.LogInfo($"ExtraReward[Layout]: 已重排 {n} 个槽，步长 {step:0.#}，起始 x {startX:0.#}");

                // 等比缩放：4 个槽挤在原本 3 个的宽度里，略缩一点更协调
                try
                {
                    var scale = views[0].transform.localScale;
                    if (n > 3 && scale.x > 0.1f)
                    {
                        float shrink = 3f / n;      // 4 个 → 0.75
                        if (shrink < 1f)
                        {
                            var s2 = new Vector3(scale.x * shrink, scale.y * shrink, scale.z);
                            for (int i = 0; i < n; i++)
                            {
                                var v = views[i];
                                if (v != null) v.transform.localScale = s2;
                            }
                            Plugin.Logger?.LogInfo($"ExtraReward[Layout]: 缩放系数 {shrink:0.##}");
                        }
                    }
                }
                catch { }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo("ExtraReward[Layout] 失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

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

                // ── 0) 先把面板原本给的 3 个候选 dump 出来（关键诊断）──
                var em = Il2CppHelpers.Safe(() => Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f).EncounterModel);
                StarVaders.ArtifactFactory fac = null;
                try { if (em != null) fac = em.ArtifactFactory; } catch { }

                int poolType = -1;
                for (int i = 0; i < arts.Count; i++)
                {
                    try
                    {
                        var nm0 = arts[i];
                        StarVaders.ArtifactModel m0 = null;
                        if (fac != null) m0 = fac.CreateArtifactModel(nm0);
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
                    catch (Exception e0) { Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 原候选[{i}] 读取失败 {e0.Message}"); }
                }
                Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 判定候选池类型 = {poolType}（0=Column 1=Power 2=Starter 3=Token 4=Pilot 5=Removed 6=Constellation）");

                // ── 1) 收集「同类型 + 当前指挥官专属」的 artifact ──
                //    ★ 上一版只按 CommanderUnique 过滤，结果捞出来的是「神器」（type=0 Column），
                //      把面板的「专长」挤掉了。这一版强制同类型。
                var uniques = new Il2CppSystem.Collections.Generic.List<StarVaders.ArtifactName>();
                var anyUnique = new Il2CppSystem.Collections.Generic.List<StarVaders.ArtifactName>();
                for (int t = 0; t <= 6; t++)
                {
                    Il2CppSystem.Collections.Generic.List<StarVaders.ArtifactModel> models = null;
                    try { models = StarVaders.ContentGetter.GetAllArtifactsOfType((StarVaders.ArtifactType)t); }
                    catch { continue; }
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
                            if (poolType >= 0 && (int)m.ArtifactType == poolType && !uniques.Contains(nm)) uniques.Add(nm);
                        }
                        catch { }
                    }
                }

                Plugin.Logger?.LogInfo($"ExtraReward[Commander]: 指挥官专属 artifact 共 {anyUnique.Count} 个，其中和候选池同类型的 {uniques.Count} 个");

                if (uniques.Count == 0)
                {
                    Plugin.Logger?.LogInfo("ExtraReward[Commander]: 同类型里没有专属 artifact，保持原样不动");
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
                        if (fac != null) built = fac.CreateArtifactModel(nm);
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

    public static class RewardOptionPatch
    {
        // ────────────────────────────────────────────────────────────
        // 1) transpiler：把 MoveNext 里的 ldc.i4.3 换成 ldc.i4.N
        // ────────────────────────────────────────────────────────────
        [HarmonyPatch]
        public static class MoveNextPatch
        {
            [HarmonyPrepare]
            public static bool Prepare()
            {
                try
                {
                    var t = FindStateMachineType();
                    if (t == null) { Plugin.Logger?.LogInfo("ExtraReward: 找不到 CreateNewDraft 状态机类型"); return false; }
                    return true;
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogInfo("ExtraReward Prepare 失败: " + e.Message);
                    return false;
                }
            }

            [HarmonyTargetMethod]
            public static MethodBase TargetMethod()
            {
                var t = FindStateMachineType();
                if (t == null) return null;
                var m = t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Plugin.Logger?.LogInfo($"ExtraReward: 目标 = {t.FullName}.MoveNext  ({(m != null)})");
                return m;
            }

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                int n = 4;
                if (Plugin.OptionCount != null) n = Plugin.OptionCount.Value;
                if (n <= 3) return instructions;

                var list = new List<CodeInstruction>(instructions);
                int replaced = 0;

                for (int i = 0; i < list.Count; i++)
                {
                    var op = list[i].opcode;
                    if (op == OpCodes.Ldc_I4_3)
                    {
                        list[i] = new CodeInstruction(OpCodes.Ldc_I4, n);
                        replaced++;
                    }
                    else if (op == OpCodes.Ldc_I4_S && list[i].operand is sbyte sb && sb == 3)
                    {
                        list[i] = new CodeInstruction(OpCodes.Ldc_I4, n);
                        replaced++;
                    }
                    else if (op == OpCodes.Ldc_I4 && list[i].operand is int v && v == 3)
                    {
                        list[i] = new CodeInstruction(OpCodes.Ldc_I4, n);
                        replaced++;
                    }
                }

                Plugin.Logger?.LogInfo($"ExtraReward[Transpiler]: 把 {replaced} 处常量 3 改成了 {n}");
                return list;
            }

            private static Type FindStateMachineType()
            {
                try
                {
                    var t = typeof(StarVaders.SpecialRewardPanel);
                    var n = t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
                    if (n == null) return null;
                    for (int i = 0; i < n.Length; i++)
                    {
                        if (n[i] != null && n[i].Name.Contains("CreateNewDraft")) return n[i];
                    }
                }
                catch { }
                return null;
            }
        }

        // ────────────────────────────────────────────────────────────
        // 2) 运行前把 ArtifactViews 克隆到 N 个
        // ────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(StarVaders.SpecialRewardPanel), nameof(StarVaders.SpecialRewardPanel.CreateNewDraft))]
        [HarmonyPrefix]
        public static void Pre_CreateNewDraft(StarVaders.SpecialRewardPanel __instance)
        {
            if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
            try { EnsureSlots(__instance); }
            catch (Exception e) { Plugin.Logger?.LogInfo("ExtraReward EnsureSlots 失败: " + e.Message); }
        }

        /// <summary>
        /// 保证 ArtifactViews 至少有 OptionCount 个。
        /// ★ 方法只接 IL2CPP 类型 —— 注入/补丁类的静态方法没有注册限制，
        ///   但仍然避免托管类型出现在签名里，保持一致性。
        /// </summary>
        private static void EnsureSlots(StarVaders.SpecialRewardPanel panel)
        {
            var views = panel.ArtifactViews;
            if (views == null) { Plugin.Logger?.LogInfo("ExtraReward: ArtifactViews 为 null"); return; }

            int want = (Plugin.OptionCount != null) ? Plugin.OptionCount.Value : 4;
            int have = views.Count;
            if (have >= want) return;

            Plugin.Logger?.LogInfo($"ExtraReward: ArtifactViews 现有 {have} 个，需要 {want} 个，开始克隆");

            // 拿最后一个当模板
            StarVaders.ArtifactView proto = null;
            try { proto = views[have - 1]; } catch { }
            if (proto == null) { Plugin.Logger?.LogInfo("ExtraReward: 模板 ArtifactView 为 null"); return; }

            var protoGo = proto.gameObject;
            var parent = protoGo.transform.parent;
            if (parent == null) { Plugin.Logger?.LogInfo("ExtraReward: 模板没有父节点"); return; }

            for (int i = have; i < want; i++)
            {
                try
                {
                    var clone = UnityEngine.Object.Instantiate(protoGo, parent);
                    if (clone == null) continue;
                    clone.name = "DV_ExtraRewardSlot_" + i;
                    clone.SetActive(true);

                    // 位置：按模板的间距往右排（若是 LayoutGroup 会自己接管）
                    var rt = clone.GetComponent<RectTransform>();
                    var prt = protoGo.GetComponent<RectTransform>();
                    if (rt != null && prt != null)
                    {
                        float step = (prt.sizeDelta.x > 1f ? prt.sizeDelta.x : 200f) + 20f;
                        rt.anchoredPosition = prt.anchoredPosition + new Vector2(step * (i - have + 1), 0f);
                    }

                    var av = clone.GetComponent<StarVaders.ArtifactView>();
                    if (av == null) { Plugin.Logger?.LogInfo($"ExtraReward: 克隆体上没有 ArtifactView（{i}）"); continue; }
                    views.Add(av);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogInfo($"ExtraReward 克隆第 {i} 个槽失败: {e.GetType().Name}: {e.Message}");
                }
            }

            Plugin.Logger?.LogInfo($"ExtraReward: ArtifactViews 现在 {views.Count} 个");
        }
    }
}
