using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using UnityEngine;

namespace DiceVaders.ExtraRewardOption
{
    /// <summary>
    /// 开局自动打开原生专长三选一面板，保留合法专属候选。
    ///
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders Extra Reward Option", "1.4.3")]
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

            // 原生面板负责生成草案与玩家点选后的发放。
            AutoOpenOnRunStart = Config.Bind("2-开局自动弹出", "AutoOpenOnRunStart", true,
                "开局自动弹出专长三选一面板（不用打幕、也不用按热键）。\n" +
                "保留原有合法专属；普通角色最多补足两个，融合角色按合法候选补足。");
            ModToggleRegistry.ExtraRewardAutoOpen = AutoOpenOnRunStart;
            AutoOpenDelay = Config.Bind("2-开局自动弹出", "AutoOpenDelay", 2f,
                new ConfigDescription("开新局后等待的最短秒数；任务引擎忙时继续等待。",
                    new AcceptableValueRange<float>(0.5f, 30f)));

            TestHotkeyEnabled = Config.Bind("3-测试", "TestHotkey", true,
                "额外保留一个热键，随时手动弹出面板。");
            TestKey = Config.Bind("3-测试", "TestKey", "Z",
                "触发测试面板的按键。可填 Z / X / C / V / F1~F12。\n" +
                "★ 游戏占用了 F2（Bug 报告），别填 F2。");

            Logger.LogInfo("===== DiceVaders ExtraRewardOption v1.4.3 =====");

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
    /// 自动弹出或按配置热键打开原生专长三选一面板。
    ///
    /// ★ 为什么要热键：打完整整一幕要很久，而幕奖励面板（SpecialRewardPanel）的
    ///   CreateNewDraft 是自包含的（反编译确认：它自己 new List&lt;ArtifactName&gt;()、
    ///   自己 new ArtifactFactory()、自己从 ContentGetter 取候选池），
    ///   所以直接起协程就能得到一个真实的奖励面板，在原生草案完成后调整合法专属候选。
    /// </summary>
    public class TestHotkeyKeeper : MonoBehaviour
    {
        public TestHotkeyKeeper(IntPtr ptr) : base(ptr) { }

        private float _cooldown;

        private void Update()
        {
            var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
            if (!RefreshRun(ec)) return;

            if (_draftReady)
            {
                int q, r;
                if (EngineIdle(ec, out q, out r))
                {
                    _draftReady = false;
                    _draftRunning = false;
                    // 玩家已关闭/选完的面板不再改写。
                    if (_panel != null && _panel.IsActive)
                    {
                        ForceCommanderPerks(_panel);
                        BringToFront(_panel);
                    }
                    _panel = null;
                }
            }

            if (Plugin.AutoOpenOnRunStart != null && Plugin.AutoOpenOnRunStart.Value &&
                !_autoOpened && _autoOpenAt >= 0f && Time.realtimeSinceStartup >= _autoOpenAt)
            {
                int q, r;
                if (EngineIdle(ec, out q, out r)) TryAutoOpen();
                else if (Time.realtimeSinceStartup > _autoGiveUpAt)
                {
                    _autoOpened = true;
                    ModKitLog.Warn("ExtraReward: 等待引擎空闲超时，本局不再自动尝试");
                }
                else if (Time.realtimeSinceStartup >= _idleLogAt)
                {
                    _idleLogAt = Time.realtimeSinceStartup + 3f;
                    Plugin.Logger?.LogInfo($"ExtraReward: 等待引擎空闲（队列 {q} / 运行栈 {r}）");
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

        /// <summary>
        /// 预览、停机或不可读的引擎均不视为空闲。
        /// 队列与运行栈必须同时为空，避免新草案与原生任务交错破坏栈顺序。
        /// </summary>
        private bool EngineIdle(StarVaders.EncounterController ec, out int queue, out int running)
        {
            queue = -1; running = -1;
            try
            {
                var te = Il2CppHelpers.Safe(() => ec.TaskEngine, null, "EngineIdle.TaskEngine");
                if (te == null || te.IsPreviewMode || te.IsStopped) return false;

                try { if (te.TaskQueue != null) queue = te.TaskQueue.Count; }
                catch (Exception __e) { LogOnce.Warn("EngineIdle.TaskQueue", __e); }

                try
                {
                    var rs = te.RunningStack;
                    if (rs != null && rs.MainStack != null) running = rs.MainStack.Count;
                }
                catch (Exception __e) { LogOnce.Warn("EngineIdle.RunningStack", __e); }

                return queue == 0 && running == 0;
            }
            catch (Exception __e) { LogOnce.Warn("EngineIdle", __e); return false; }
        }

        private bool Trigger()
        {
            RewardPanelPreparation preparation = null;
            try
            {
                var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
                if (!RefreshRun(ec) || _draftRunning) return false;
                int queue, running;
                if (!EngineIdle(ec, out queue, out running)) return false;
                var panel = Il2CppHelpers.FindCached<StarVaders.SpecialRewardPanel>(0.5f);
                if (panel == null || panel.IsActive) return false;
                // 在改动界面前取得草案；null 不留下空面板。
                var native = panel.CreateNewDraft();
                if (native == null) return false;
                preparation = new RewardPanelPreparation(panel);
                preparation.Show();
                if (!panel.IsActive) throw new InvalidOperationException("奖励面板未进入活动状态");
                _panel = panel;
                _draftRunning = true;
                _draftReady = false;
                // 提交后结果可能不确定，保留所有权，绝不中止原生协程或重复启动。
                preparation.Commit();
                var started = panel.StartCoroutine(DraftCompletion.Run(native, this, _runEpoch).WrapToIl2Cpp());
                if (started == null)
                {
                    _autoOpened = true;
                    ModKitLog.Warn("ExtraReward: 未取得协程句柄，本局暂停重复触发");
                    return false;
                }
                BringToFront(panel);
                Plugin.Logger?.LogInfo("ExtraReward: 已启动 CreateNewDraft，等待原生完成");
                return true;
            }
            catch (Exception e)
            {
                if (_draftRunning) _autoOpened = true;
                ModKitLog.Warn("ExtraReward Trigger 异常: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
            finally { preparation?.Dispose(); }
        }

        private StarVaders.SpecialRewardPanel _panel;
        private bool _draftRunning, _draftReady;
        private IntPtr _lastModel;
        private int _runEpoch;

        private bool RefreshRun(StarVaders.EncounterController ec)
        {
            IntPtr encounter = IntPtr.Zero, model = IntPtr.Zero;
            try
            {
                if (ec != null && ec.EncounterModel != null)
                {
                    encounter = ec.Pointer;
                    model = ec.EncounterModel.Pointer;
                }
            }
            catch (Exception e) { LogOnce.Warn("ExtraReward.RunIdentity", e); return false; }
            if (encounter != _lastEncounter || model != _lastModel)
            {
                _lastEncounter = encounter; _lastModel = model; _runEpoch++;
                _panel = null;
                _draftRunning = false; _draftReady = false;
                _cooldown = 0f; _autoAttempts = 0; _autoOpened = false;
                _autoOpenAt = -1f; _autoGiveUpAt = -1f; _idleLogAt = 0f;
                Il2CppHelpers.OnRunChanged();
                if (encounter != IntPtr.Zero && model != IntPtr.Zero)
                {
                    float delay = Plugin.AutoOpenDelay != null ? Plugin.AutoOpenDelay.Value : 2f;
                    _autoOpenAt = Time.realtimeSinceStartup + delay;
                    _autoGiveUpAt = Time.realtimeSinceStartup + 60f;
                }
            }
            return encounter != IntPtr.Zero && model != IntPtr.Zero;
        }

        private void TryAutoOpen()
        {
            _autoOpenAt = -1f;
            _autoAttempts++;
            if (Trigger()) _autoOpened = true;
            else if (!_autoOpened && _autoAttempts < 2)
                _autoOpenAt = Time.realtimeSinceStartup + 3f;
            else _autoOpened = true;
        }

        internal void MarkDraftFinished(int epoch, bool success)
        {
            var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
            if (!RefreshRun(ec) || epoch != _runEpoch || !_draftRunning) return;
            if (success) _draftReady = true;
            else
            {
                _autoOpened = true;
                ModKitLog.Warn("ExtraReward: 原生草案失败，本局禁止重复启动");
            }
        }

        /// <summary>等引擎空闲的最晚时刻（超过就放弃自动弹出）。</summary>
        private float _autoGiveUpAt = -1f;
        /// <summary>「等待引擎空闲」日志的下次打印时刻（3 秒一次，不刷屏）。</summary>
        private float _idleLogAt = 0f;

        /// <summary>
        /// 面板及最多六层父节点依次移到兄弟节点末尾。
        /// 不修改共享 Canvas 的排序层，避免影响其他游戏界面。
        /// </summary>
        private void BringToFront(StarVaders.SpecialRewardPanel panel)
        {
            try
            {
                var t = panel.transform;
                int level = 0;
                while (t != null && level < 6)
                {
                    try
                    {
                        string cvInfo = "";
                        try
                        {
                            var cv = t.GetComponent<UnityEngine.Canvas>();
                            if (cv != null)
                                cvInfo = $" [Canvas '{t.name}' order={cv.sortingOrder} reason={cv.renderMode}]";
                        }
                        catch (Exception __e) { LogOnce.Warn($"BringToFront.Canvas.L{level}", __e); }

                        int before = -1, cnt = -1;
                        try { before = t.GetSiblingIndex(); cnt = (t.parent != null) ? t.parent.childCount : -1; } catch (Exception __e) { LogOnce.Warn($"BringToFront.Read.L{level}", __e); }
                        t.SetAsLastSibling();

                        Plugin.Logger?.LogInfo($"  [置顶] L{level} '{t.name}' 兄弟序 {before}/{cnt}{cvInfo}");
                    }
                    catch (Exception __e) { LogOnce.Warn($"BringToFront.L{level}", __e); }

                    t = t.parent;
                    level++;
                }
            }
            catch (Exception __e) { LogOnce.Warn("BringToFront", __e); }
        }

        // ★ 开局自动弹出用的状态
        private IntPtr _lastEncounter = IntPtr.Zero;
        private float _autoOpenAt = -1f;
        private bool _autoOpened = false;
        private int _autoAttempts = 0;

        /// <summary>
        /// 保留原草案合法专属，按当前类型与原生合法性判据补足缺项。
        /// 展示更新成功后才提交选择数据；模型准备失败不修改原草案。
        /// </summary>
        private void ForceCommanderPerks(StarVaders.SpecialRewardPanel panel)
        {
            try
            {
                if (panel == null || !panel.IsActive) return;
                var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
                if (!RefreshRun(ec)) return;
                var em = ec.EncounterModel;
                var arts = panel._artifacts;
                var views = panel.ArtifactViews;
                if (arts == null || arts.Count == 0 || views == null || views.Count < arts.Count) return;
                var scratch = new StarVaders.ArtifactFactory(0);
                var cur = StarVaders.CharacterSelectionController.CurrentCommander;
                var owners = new System.Collections.Generic.HashSet<StarVaders.CommanderName> { cur };
                if (cur == StarVaders.CommanderName.Overseer)
                {
                    owners.Add(StarVaders.CharacterSelectionController.OverseerCommander1);
                    owners.Add(StarVaders.CharacterSelectionController.OverseerCommander2);
                }
                owners.Remove(StarVaders.CommanderName.None);
                var original = new StarVaders.ArtifactModel[arts.Count];
                for (int i = 0; i < arts.Count; i++)
                {
                    original[i] = scratch.CreateArtifactModel(arts[i]);
                    if (original[i] == null || views[i] == null) return;
                }
                var type = original[0].ArtifactType;
                for (int i = 1; i < original.Length; i++)
                    if (original[i].ArtifactType != type) return;

                var legal = new System.Collections.Generic.List<StarVaders.ArtifactName>();
                var models = StarVaders.ContentGetter.GetAllArtifactsOfType(type);
                if (models == null) return;
                for (int i = 0; i < models.Count; i++)
                {
                    var m = models[i];
                    if (m == null || !owners.Contains(m.CommanderUnique) || legal.Contains(m.ArtifactName)) continue;
                    // 与当前游戏原生 CreateNewDraft.b__4 使用同一合法性判据，
                    // 包括解锁、挑战限制、当前指挥官和已拥有过滤；融合归属也由游戏裁决。
                    if (!StarVaders.ContentGetter.IsArtifactGettableInCurrentRun(m.ArtifactName, em)) continue;
                    legal.Add(m.ArtifactName);
                }
                var used = new System.Collections.Generic.HashSet<StarVaders.ArtifactName>();
                int kept = 0;
                for (int i = 0; i < arts.Count; i++)
                {
                    used.Add(arts[i]);
                    if (legal.Contains(arts[i])) kept++;
                }
                int target = Math.Min(arts.Count, cur == StarVaders.CommanderName.Overseer ? legal.Count : Math.Min(2, legal.Count));
                for (int i = 0; i < arts.Count && kept < target; i++)
                {
                    if (legal.Contains(arts[i])) continue; // 保留原草案已有的合法专属。
                    foreach (var name in legal)
                    {
                        if (used.Contains(name)) continue;
                        var built = scratch.CreateArtifactModel(name);
                        if (built == null) continue;
                        // 先准备并更新展示，成功后才提交选择数据。
                        try { views[i].SetModel(built, false); }
                        catch (Exception e)
                        {
                            try { views[i].SetModel(original[i], false); }
                            catch (Exception rollback) { LogOnce.Warn("ExtraReward.ViewRollback", rollback); }
                            LogOnce.Warn("ExtraReward.ViewCommit", e);
                            return;
                        }
                        var previous = arts[i];
                        try { arts[i] = name; }
                        catch
                        {
                            views[i].SetModel(original[i], false);
                            throw;
                        }
                        used.Remove(previous); used.Add(name); kept++;
                        break;
                    }
                }
            }
            catch (Exception e) { LogOnce.Warn("ExtraReward.CommanderPerks", e); }
        }

    }

    internal sealed class RewardPanelPreparation : IDisposable
    {
        private readonly StarVaders.SpecialRewardPanel _panel;
        private readonly System.Collections.Generic.List<GameObject> _objects = new();
        private readonly System.Collections.Generic.List<bool> _active = new();
        private bool _committed, _showAttempted;

        internal RewardPanelPreparation(StarVaders.SpecialRewardPanel panel)
        {
            _panel = panel;
            for (var t = panel.transform; t != null; t = t.parent) Remember(t.gameObject);
            Remember(panel.PilotDraftScreen); Remember(panel.LevelUpScreen);
            Remember(panel.LegendaryDraftScreen); Remember(panel.OverseerLevelupScreen);
            if (panel.LevelUpScreen == null) throw new InvalidOperationException("专长屏幕缺失");
        }
        private void Remember(GameObject go)
        {
            if (go == null || _objects.Contains(go)) return;
            _objects.Add(go); _active.Add(go.activeSelf);
        }
        internal void Show()
        {
            for (var t = _panel.transform; t != null; t = t.parent)
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            _panel.PilotDraftScreen?.SetActive(false);
            _panel.LevelUpScreen.SetActive(true);
            _panel.LegendaryDraftScreen?.SetActive(false);
            _panel.OverseerLevelupScreen?.SetActive(false);
            _showAttempted = true;
            _panel.Show();
        }
        internal void Commit() { _committed = true; }
        public void Dispose()
        {
            if (_committed) return;
            if (_showAttempted)
                try { _panel.Hide(true); }
                catch (Exception e) { LogOnce.Warn("ExtraReward.PrepareHide", e); }
            for (int i = _objects.Count - 1; i >= 0; i--)
                try { if (_objects[i] != null) _objects[i].SetActive(_active[i]); }
                catch (Exception e) { LogOnce.Warn("ExtraReward.PrepareRollback", e); }
        }
    }

    // 普通托管辅助类：避免向 ClassInjector 暴露托管 IEnumerator 方法签名。
    internal static class DraftCompletion
    {
        internal static System.Collections.IEnumerator Run(
            Il2CppSystem.Collections.IEnumerator native, TestHotkeyKeeper owner, int epoch)
        {
            while (true)
            {
                bool more;
                Il2CppSystem.Object current = null;
                try
                {
                    more = native.MoveNext();
                    if (more) current = native.Current;
                }
                catch (Exception e)
                {
                    owner.MarkDraftFinished(epoch, false);
                    ModKitLog.Warn("ExtraReward: CreateNewDraft 原生异常 " + e.Message);
                    yield break;
                }
                if (!more) break;
                yield return current;
            }
            owner.MarkDraftFinished(epoch, true);
        }
    }

}
