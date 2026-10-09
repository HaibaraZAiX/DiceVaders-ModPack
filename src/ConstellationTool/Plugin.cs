using System;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using StarVaders;
using UnityEngine;
using UnityEngine.UI;

namespace DiceVaders.ConstellationTool
{
    /// <summary>
    /// 仅在星座选择界面执行全部或单项重掷。
    /// 全部重掷由原生协程生成，完成并确认引擎空闲后规范化列表；
    /// 单项重掷由原生 CreateArtifactTask 替换实际装备，再同步展示 ID。
    /// 界面按列表下标重建；非连续解锁布局仍需游戏内验证。
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders Constellation Tool", "3.4.2")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.constellationtool";

        internal static ManualLogSource Logger;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> UseNativeButton;
        internal static ConfigEntry<bool> ShowImGuiPanel;
        internal static ConfigEntry<bool> AutoDump;
        internal static ConfigEntry<float> RestoreDelay;
        internal static ConfigEntry<float> BtnRightOffset;
        internal static ConfigEntry<float> BtnTopOffset;

        internal static ConfigEntry<bool> AnchorRight;
        internal static ConfigEntry<float> LeftOffset;
        internal static ConfigEntry<float> BottomOffset;
        internal static ConfigEntry<float> BtnWidth;
        internal static ConfigEntry<float> ForceFontSize;
        internal static ConfigEntry<bool> LogCandidates;
        internal static ConfigEntry<int> MaxRerollPerSession;
        internal static ConfigEntry<bool> AnchorBottom;
        internal static ConfigEntry<bool> EnableSingleReroll;
        internal static ConfigEntry<float> SingleBtnWidth;

        public override void Load()
        {
            Logger = base.Log;
            ModKitLog.Sink      = m => Logger.LogInfo(m);
            ModKitLog.WarnSink  = m => Logger.LogWarning(m);
            ModKitLog.ErrorSink = m => Logger.LogError(m);

            Enabled = Config.Bind("0-开关", "Enabled", true,
                "星座刷新功能总开关。也可以在游戏的「沙盒设置」面板里切换。");
            UseNativeButton = Config.Bind("1-按钮", "UseNativeButton", true,
                "克隆游戏原生按钮做「重掷星座」按钮。");
            BtnRightOffset = Config.Bind("1-按钮", "RightOffset", 60f,
                new ConfigDescription("右上角锚点时：距屏幕右边缘像素。", new AcceptableValueRange<float>(0f, 800f)));
            BtnTopOffset = Config.Bind("1-按钮", "TopOffset", 300f,
                new ConfigDescription("右上角锚点时：距屏幕顶部像素。", new AcceptableValueRange<float>(0f, 1000f)));

            AnchorRight = Config.Bind("1-按钮", "AnchorRight", true,
                "true=靠右侧（默认）；false=靠左侧。");
            AnchorBottom = Config.Bind("1-按钮", "AnchorBottom", true,
                "配合 AnchorRight 决定四角：右+底=右下角（默认）/ 右+顶=右上角 / 左+底=左下角。" +
                "右下角与右上角都读 RightOffset；右下角用 BottomOffset，右上角用 TopOffset。");
            LeftOffset = Config.Bind("1-按钮", "LeftOffset", 120f,
                new ConfigDescription("左下角锚点时：距屏幕左边缘像素。", new AcceptableValueRange<float>(0f, 1200f)));
            BottomOffset = Config.Bind("1-按钮", "BottomOffset", 235f,
                new ConfigDescription("左下角锚点时：距屏幕底部像素（「星座系统」上方）。", new AcceptableValueRange<float>(0f, 1200f)));

            BtnWidth = Config.Bind("1-按钮", "Width", 350f,
                new ConfigDescription("主按钮宽度（高度自动按游戏原版长宽比跟随，不单独设置 —— " +
                    "原版按钮的上下边框是独立子对象、按原高度定位，压缩高度会把边框挤出可见区）。",
                    new AcceptableValueRange<float>(100f, 700f)));
            ForceFontSize = Config.Bind("1-按钮", "FontSize", 42f,
                new ConfigDescription("自建按钮的字号，0=沿用取到的原字号。", new AcceptableValueRange<float>(0f, 120f)));

            EnableSingleReroll = Config.Bind("1-按钮", "EnableSingleReroll", true,
                "显示 3 个单项刷新按钮，各自贴在对应星座卡片下方。关掉则只有「刷新全部」。");
            SingleBtnWidth = Config.Bind("1-按钮", "SingleButtonWidth", 170f,
                new ConfigDescription("单项刷新按钮的宽度（高度同样按比例跟随）。",
                    new AcceptableValueRange<float>(50f, 400f)));

            LogCandidates = Config.Bind("2-调试", "LogCandidates", true,
                "每次进星座界面时打印场景里的按钮候选清单（便于排查）。");

            MaxRerollPerSession = Config.Bind("3-安全", "MaxRerollPerSession", 20,
                new ConfigDescription(
                    "单次停留在星座界面的最大重掷次数。防止 CreateConstellations() 反复追加数据把本局搞崩。",
                    new AcceptableValueRange<int>(1, 200)));

            ShowImGuiPanel = Config.Bind("2-调试", "ShowDebugPanel", false,
                "显示屏幕左上角的调试面板（兜底用）。默认关。");
            AutoDump = Config.Bind("2-调试", "AutoDumpOnSceneOpen", true,
                "进入星座场景时自动 dump 状态到日志。");
            RestoreDelay = Config.Bind("2-调试", "DetailRestoreDelay", 0.65f,
                new ConfigDescription("重掷后等待多久再补详情（秒）。",
                    new AcceptableValueRange<float>(0f, 5f)));

            ModToggleRegistry.ConstellationReroll = Enabled;
            ModToggleRegistry.Log = m => Logger.LogInfo(m);

            Logger.LogInfo("===== Constellation Tool v3.4.2 (刷新全部 + 每张卡片独立刷新) =====");

            ClassInjector.RegisterTypeInIl2Cpp<ConstellationUI>();
            var go = new GameObject("DiceVaders_ConstellationTool");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<ConstellationUI>();

            Logger.LogInfo("宿主已创建。");
        }
    }

    public class ConstellationUI : MonoBehaviour
    {
        public ConstellationUI(IntPtr ptr) : base(ptr) { }

        private IntPtr _lastModelPtr;
        private int _epoch;
        private bool _allPending, _allFinished;
        private bool _wasInScene;
        private bool _wasShowing;
        private GameObject _nativeButton;
        private bool _anyButtonVisible = true;

        /// <summary>
        /// 上一次看到的 EncounterController 原生指针。
        /// 用它判断"换局了"——换了就必须销毁缓存的按钮，否则第二局会显示白色色块
        /// （按钮的 sprite 指向的美术资源已被 Addressables 卸载）。
        /// </summary>
        private IntPtr _lastEncounterPtr = IntPtr.Zero;
        /// <summary>单项重掷小按钮（下标 = 星座槽位序号）。</summary>
        private readonly System.Collections.Generic.List<GameObject> _singleButtons = new System.Collections.Generic.List<GameObject>();
        private static GameObject _overlayCanvas;
        private float _restoreAt = -1f;
        private float _verifyAt = -1f;
        /// <summary>延迟裁剪的时刻（v3.3：必须等 UpdateConstellationView 的异步刷新跑完）。</summary>
        private float _trimAt = -1f;

        private int _rerollCount;            // 本次停留已重掷次数
        private float _lastRerollAt = -9f;
        // 原生任务完成前不改展示列表；所有回写只属于发起时的对局。
        private bool _singlePending;
        private bool _singleFailed;
        private bool _singleViewDirty;
        private IntPtr _singleEncounterPtr;
        private IntPtr _singleModelPtr;
        private int _singleIndex;
        private int _singleOldId;
        private ArtifactName _singleName;
        private float _singleStartedAt;
        private bool _singleWaitLogged;
        private readonly System.Collections.Generic.List<TMPro.TextMeshProUGUI> _textFixTmps = new System.Collections.Generic.List<TMPro.TextMeshProUGUI>();
        private readonly System.Collections.Generic.List<string> _textFixLabels = new System.Collections.Generic.List<string>();
        private string _status = "";
        private float _statusUntil;

        private static void Log(string m) => Plugin.Logger?.LogInfo(m);

        /// <summary>
        /// 自建 ScreenSpaceOverlay Canvas。
        /// 不借用游戏的 Canvas：实测游戏是 ScreenSpaceCamera 模式，挂进去后锚点算不出有效矩形
        /// （世界四角恒为 0,0）。Overlay 模式下 rect 就是屏幕像素，坐标确定、层级由 sortingOrder 决定。
        /// 不挂 CanvasScaler，让 scaleFactor 保持 1，这样 RightOffset/TopOffset 就是真实像素。
        /// </summary>
        private static GameObject EnsureOverlayCanvas()
        {
            if (_overlayCanvas != null) return _overlayCanvas;
            var go = new GameObject("DiceVaders_RerollCanvas");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;      // 压到最上层
            canvas.overrideSorting = true;

            // ★ v3.6 修复（审查 M18）：必须挂 GraphicRaycaster。
            //   没有它，这个 Canvas 下的按钮对 EventSystem **完全不可见** ——
            //   点击会射线穿透到同位置的游戏 UI 上（目前靠代码里自己轮询
            //   Mouse.current + RectangleContainsScreenPoint 兜住，但那是纯几何判定，
            //   和游戏 UI 重叠时会「一次点击打给两个对象」）。
            //   挂上之后按钮才真正参与 Unity 的事件系统。
            try { go.AddComponent<GraphicRaycaster>(); }
            catch (Exception __e) { LogOnce.Warn("EnsureOverlayCanvas.GraphicRaycaster", __e); }

            _overlayCanvas = go;
            Log("  已创建独立 Overlay Canvas (sortingOrder=32000, +GraphicRaycaster)");
            return go;
        }

        private void SetStatus(string s)
        {
            _status = s; _statusUntil = Time.realtimeSinceStartup + 8f; Log("[状态] " + s);
        }

        // 类型判定统一走 ModKit。
        private static string RealTypeName(Component comp) => Il2CppHelpers.RealTypeName(comp);

        private static bool IsGameType(string full) => Il2CppHelpers.IsGameType(full);

        // ---------- 查找缓存 ----------
        // FindObjectOfType 是每次遍历全场景的昂贵调用，而 Update 每帧都要用它，
        // 120FPS 下就是每秒 120 次全场景扫描。这里加 0.25 秒的短缓存：
        // 查找结果最多缓存 0.25 秒，但扫描次数降到每秒 4 次。
        private static ConstellationController _ccCache;
        private static EncounterController _ecCache;
        private static float _ccCacheAt = -99f;
        private static float _ecCacheAt = -99f;
        private const float LookupInterval = 0.25f;

        private static ConstellationController FindController()
        {
            float now = Time.realtimeSinceStartup;
            if (_ccCache != null && now - _ccCacheAt < LookupInterval) return _ccCache;
            try { _ccCache = UnityEngine.Object.FindObjectOfType<ConstellationController>(); }
            catch { _ccCache = null; }
            _ccCacheAt = now;
            return _ccCache;
        }

        private static EncounterController FindEncounter()
        {
            float now = Time.realtimeSinceStartup;
            if (_ecCache != null && now - _ecCacheAt < LookupInterval) return _ecCache;
            try { _ecCache = UnityEngine.Object.FindObjectOfType<EncounterController>(); }
            catch { _ecCache = null; }
            _ecCacheAt = now;
            return _ecCache;
        }

        // ---------- 生命周期 ----------

        private void Update()
        {
            // 控制器或模型变化时作废旧局回写，并重建引用了本局美术资源的按钮。
            try
            {
                var ecNow = FindEncounter();
                IntPtr curPtr = IntPtr.Zero;
                if (ecNow != null) curPtr = ecNow.Pointer;

                var curModel = ecNow != null && ecNow.EncounterModel != null ? ecNow.EncounterModel.Pointer : IntPtr.Zero;
                if (curPtr != _lastEncounterPtr || curModel != _lastModelPtr)
                {
                    ResetSingleReroll();
                    _lastModelPtr = curModel; _epoch++;
                    _allPending = false; _allFinished = false;
                    _restoreAt = _verifyAt = _trimAt = -1f;
                    _rerollCount = 0;
                    _wasShowing = false;
                    bool hadButtons = (_nativeButton != null) || (_singleButtons.Count > 0);
                    _lastEncounterPtr = curPtr;
                    if (hadButtons) DestroyBuiltButtons("换局");
                    // 同步清理共享查找缓存与日志去重表。
                    if (curPtr != IntPtr.Zero) Il2CppHelpers.OnRunChanged();
                }
            }
            catch (Exception e)
            {
                LogOnce.Warn("ConstellationUI.RunIdentity", e);
                SyncButtonVisibility(null, false);
                return; // 身份不可读时保留任务所有权，暂停旧局操作。
            }

            // 已提交的原生任务不能靠关开关取消；仍需同步当前局的最终装备结果。
            PollSingleReroll();
            // ★ 总开关（可在游戏「沙盒设置」面板里切换）—— 关闭时隐藏全部按钮并跳过所有逻辑
            if (Plugin.Enabled != null && !Plugin.Enabled.Value)
            {
                if (_anyButtonVisible)
                {
                    _anyButtonVisible = false;
                    _restoreAt = _verifyAt = _trimAt = -1f;
                    _wasShowing = false;
                    try
                    {
                        if (_nativeButton != null) _nativeButton.SetActive(false);
                        for (int i = 0; i < _singleButtons.Count; i++)
                        {
                            var b = _singleButtons[i];
                            if (b != null) b.SetActive(false);
                        }
                    }
                    catch (Exception __e) { LogOnce.Warn("ConstellationUI.Update:263", __e); }
                }
                return;
            }

            _anyButtonVisible = true;
            var cc = FindController();
            bool inScene = cc != null;

            // 关键：ConstellationController 在【局内也存在】，不能只看它是否存在，
            // 要用游戏的 IsShowing 标志判断"星座界面是否正在显示"，
            // 否则局内也会飘一个按钮盖住右侧面板。
            bool showing = false;
            if (inScene)
            {
                try { showing = ConstellationController.IsShowing; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.Update:299", __e); }
            }

            // ★ v3.4：_trimAt 也要一起复位 —— 否则上一局排下的延迟裁剪会打到下一局的数据上
            //   （cc 已换，旧时刻仍会到期触发）。
            if (!inScene && _wasInScene) { _restoreAt = -1f; _verifyAt = -1f; _trimAt = -1f; _rerollCount = 0; }
            _wasInScene = inScene;
            if (_allPending && _allFinished)
            {
                var ec = FindEncounter();
                if (ec != null && SingleEngineIdle(ec.TaskEngine) && cc != null && !cc.IsTransitioning)
                {
                    _allPending = _allFinished = false;
                    if (showing) _restoreAt = Time.realtimeSinceStartup + Plugin.RestoreDelay.Value;
                }
            }
            if (!showing && cc != null && !cc.IsTransitioning && !_allPending && !_singlePending)
            {
                _rerollCount = 0; _restoreAt = _verifyAt = _trimAt = -1f;
            }
            SyncButtonVisibility(cc, showing);
            if (_singlePending || _singleFailed || _allPending) return;
            if (_singleViewDirty && showing && cc != null && !cc.IsTransitioning)
            {
                var ec = FindEncounter();
                if (ec != null && SingleEngineIdle(ec.TaskEngine))
                {
                    TryUpdateView(cc);
                    _singleViewDirty = false;
                    _restoreAt = Time.realtimeSinceStartup + Plugin.RestoreDelay.Value;
                }
            }

            // 每次重新打开星座界面做一次初始化。
            // 注意：重掷会让界面重新过渡并再次触发"显示"，所以这里【不能】重置重掷计数
            //       （否则每次点击都被记成「第 1 次」，v1.4 日志实证）。
            // 另：这个块曾经被复制成两份（v1.5 遗留），导致 Dump 每次跑两遍 —— 已合并。
            if (showing && !_wasShowing)
            {
                if (Plugin.AutoDump.Value) Dump("星座界面显示");
                if (Plugin.UseNativeButton.Value) TryBuildNativeButton(cc);

                // 列表刷新含异步本地化；延迟后还须确认过渡结束、引擎空闲。
                _trimAt = Time.realtimeSinceStartup + 2.5f;
            }

            if (showing && Plugin.UseNativeButton.Value && Plugin.EnableSingleReroll.Value &&
                cc != null && cc.Constellations != null && _singleButtons.Count != cc.Constellations.Count)
                TryBuildNativeButton(cc);

            // 延迟裁剪（等异步刷新跑完 + 不在过渡中）
            if (_trimAt > 0f && Time.realtimeSinceStartup >= _trimAt)
            {
                _trimAt = -1f;
                bool transitioning = false;
                try { transitioning = cc.IsTransitioning; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.Update:340", __e); return; }
                if (transitioning)
                {
                    _trimAt = Time.realtimeSinceStartup + 1.5f;   // 还在过渡，再等等
                }
                else
                {
                    TrimModelConstellations(cc);
                }
            }

            SyncButtonVisibility(cc, showing);

            // 小按钮贴到各自卡片旁边（卡片自由布局，索引顺序 ≠ 视觉顺序）
            if (showing) UpdateSingleButtonPositions(cc);

            // 游戏会把克隆按钮的文字重写回「揭晓！」，每帧盯住
            if (showing) EnforceButtonTexts();
            _wasShowing = showing;

            CheckButtonClick();

            if (_verifyAt > 0f && Time.realtimeSinceStartup >= _verifyAt)
            {
                _verifyAt = -1f;
                if (_nativeButton != null) VerifyVisible(_nativeButton, "布局更新后");
            }

            if (_restoreAt > 0f && Time.realtimeSinceStartup >= _restoreAt)
            {
                _restoreAt = -1f;
                RestoreDetails(cc);
            }
        }

        // ---------- 原生按钮 ----------

        /// <summary>
        /// 同步显隐；状态不可读的卡片隐藏，任务等待期间仍执行。
        /// </summary>
        private void SyncButtonVisibility(ConstellationController cc, bool showing)
        {
            bool visible = showing && Plugin.Enabled != null && Plugin.Enabled.Value &&
                Plugin.UseNativeButton != null && Plugin.UseNativeButton.Value;
            if (_nativeButton != null)
            {
                try { if (_nativeButton.activeSelf != visible) _nativeButton.SetActive(visible); }
                catch (Exception e) { LogOnce.Warn("ConstellationUI.MainVisibility", e); }
            }
            for (int i = 0; i < _singleButtons.Count; i++)
            {
                var b = _singleButtons[i];
                if (b == null) continue;
                bool singleVisible = false;
                try
                {
                    singleVisible = visible && Plugin.EnableSingleReroll != null && Plugin.EnableSingleReroll.Value &&
                        cc != null && cc.Constellations != null && i < cc.Constellations.Count &&
                        cc.Constellations[i] != null && !cc.Constellations[i].isLocked;
                }
                catch (Exception e) { LogOnce.Warn("ConstellationUI.SingleVisibility", e); }
                try { if (b.activeSelf != singleVisible) b.SetActive(singleVisible); }
                catch (Exception e) { LogOnce.Warn("ConstellationUI.SingleSetActive", e); }
            }
        }

        /// <summary>输出场景按钮清单，仅用于诊断。</summary>
        private void LogButtonCandidates()
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType<StarVaders.ButtonView>();
                Log($"  场景中 ButtonView 数量 = {arr.Length}");
                foreach (var bv in arr)
                {
                    if (bv == null) continue;
                    var go = bv.gameObject;
                    if (go == null) continue;
                    var rt = go.GetComponent<RectTransform>();
                    bool act = go.activeInHierarchy;
                    var sz = (rt != null) ? rt.sizeDelta : Vector2.zero;
                    bool hasTmp = go.GetComponentInChildren<TMPro.TextMeshProUGUI>() != null;
                    Log($"   候选 '{go.name}' active={act} size=({sz.x:0},{sz.y:0}) tmp={hasTmp}");
                }
            }
            catch (Exception e) { Log("  遍历 ButtonView 失败: " + e.Message); }

        }

        /// <summary>从游戏里取一份可用字体（否则自建按钮的中文出不来）。</summary>
        private TMPro.TMP_FontAsset FindAnyFont(out float size)
        {
            size = 42f;
            // 1) 星座卡片容器（实测它有 TMP，显示的是星座名）
            try
            {
                var cc = FindController();
                if (cc != null && cc.ConstellationButtonObject != null)
                {
                    var tmp = cc.ConstellationButtonObject.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    if (tmp != null && tmp.font != null)
                    {
                        if (tmp.fontSize > 0f) size = tmp.fontSize;
                        Log($"  字体取自 ConstellationButtonObject: {tmp.font.name} (size={size})");
                        return tmp.font;
                    }
                }
            }
            catch (Exception __e) { LogOnce.Warn("ConstellationUI.FindAnyFont:476", __e); }

            // 2) 场景里任意 TMP
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
                foreach (var t in all)
                {
                    if (t != null && t.font != null)
                    {
                        if (t.fontSize > 0f) size = t.fontSize;
                        Log($"  字体取自场景 TMP '{t.name}': {t.font.name} (size={size})");
                        return t.font;
                    }
                }
            }
            catch (Exception e) { Log("  搜索场景字体失败: " + e.Message); }

            Log("  未取到字体，使用 TMP 默认");
            return null;
        }

        /// <summary>
        /// 找游戏原版按钮底图。实测 bundle 里有：
        ///   T_MM_plainbutton9slicebase（普通按钮九宫格底）★
        ///   T_MM_playbutton9slicebase / T_MM_morebuttonbase / UiButton
        /// 这些已在场景里加载，可直接用 Resources.FindObjectsOfTypeAll 取到。
        /// </summary>
        private static UnityEngine.Sprite FindButtonSprite()
        {
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Sprite>();
                if (all == null) return null;

                // 1) 精确名优先
                foreach (var s in all)
                {
                    if (s == null) continue;
                    if ((s.name ?? "") == "T_MM_plainbutton9slicebase") return s;
                }
                // 2) 含 9slice 的按钮底
                foreach (var s in all)
                {
                    if (s == null) continue;
                    var n = (s.name ?? "").ToLowerInvariant();
                    if (n.Contains("button") && n.Contains("9slice")) return s;
                }
                // 3) 任意 buttonbase
                foreach (var s in all)
                {
                    if (s == null) continue;
                    var n = (s.name ?? "").ToLowerInvariant();
                    if (n.Contains("buttonbase") || n.Contains("button_base")) return s;
                }
                Log($"  未找到原版按钮底图（当前已加载 Sprite 数 = {all.Length}）");
            }
            catch (Exception e) { Log("  查找按钮底图失败: " + e.Message); }
            return null;
        }

        /// <summary>
        /// 从场景里找一个现成的长条按钮，复制它的 sprite 与 color 当模板。
        /// v1.0 教训：T_MM_plainbutton9slicebase 疑似纯白九宫格（靠 Image.color 染色），
        /// 我写死 Color.white 就成了白底白字全白块。
        /// </summary>
        private UnityEngine.UI.Image FindButtonImageTemplate()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Image>();
                Log($"  场景 Image 总数 = {all.Length}");
                UnityEngine.UI.Image best = null;
                float bestScore = -1f;
                foreach (var img in all)
                {
                    if (img == null || !img.enabled) continue;
                    var go = img.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    var sz = rt.sizeDelta;
                    // 长条按钮：宽 250~560，高 55~120
                    if (sz.x < 250f || sz.x > 560f) continue;
                    if (sz.y < 55f || sz.y > 120f) continue;
                    if (img.sprite == null) continue;

                    Log($"    候选 '{go.name}' size=({sz.x:0},{sz.y:0}) sprite={img.sprite.name} color={img.color}");
                    float score = sz.x * sz.y;
                    if (score > bestScore) { bestScore = score; best = img; }
                }
                if (best != null)
                    Log($"  -> 按钮模板: '{best.gameObject.name}' sprite={best.sprite.name} color={best.color}");
                else
                    Log("  -> 没找到合适的长条按钮模板，退回默认素材");
                return best;
            }
            catch (Exception e) { Log("  找按钮模板失败: " + e.Message); }
            return null;
        }

        /// <summary>
        /// 兜底：自己拼一个按钮，完全不依赖游戏对象。
        /// 底图与配色优先复制场景里现成的长条按钮；字体来自游戏 TMP。
        /// </summary>
        private GameObject CreateOwnButton(TMPro.TMP_FontAsset font, float fontSize, string label, float widthScale = 1f)
        {
            var go = new GameObject("DiceVaders_RerollButton_" + label);
            var rt = go.AddComponent<RectTransform>();
            go.layer = 5;   // UI

            rt.sizeDelta = new Vector2(Plugin.BtnWidth.Value * widthScale, 60f * widthScale);
            ApplyAnchorForOffset(rt, 0f);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;

            var img = go.AddComponent<UnityEngine.UI.Image>();

            var tpl = FindButtonImageTemplate();
            if (tpl != null && tpl.sprite != null)
            {
                img.sprite = tpl.sprite;
                img.type = tpl.type;                       // 沿用原按钮的拉伸方式
                img.pixelsPerUnitMultiplier = tpl.pixelsPerUnitMultiplier;
                img.color = tpl.color;                     // ★ 关键：复制原按钮配色，不再写死白色
                img.material = tpl.material;
                Log($"  自建按钮沿用模板: sprite={tpl.sprite.name} color={tpl.color} type={tpl.type}");
            }
            else
            {
                var sp = FindButtonSprite();
                if (sp != null)
                {
                    img.sprite = sp;
                    img.type = Image.Type.Sliced;
                    img.color = new Color(0.10f, 0.34f, 0.40f, 0.95f);   // 深青，不写白
                    Log($"  自建按钮用默认底图+深青色: {sp.name}");
                }
                else
                {
                    img.color = new Color(0.10f, 0.34f, 0.40f, 0.95f);
                    Log("  自建按钮使用纯深青色底（未取到素材）");
                }
            }

            var tgo = new GameObject("Text");
            tgo.layer = 5;
            tgo.transform.SetParent(go.transform, false);
            var trt = tgo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(10f, 4f);
            trt.offsetMax = new Vector2(-10f, -4f);

            var tmp = tgo.AddComponent<TMPro.TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            float want = Plugin.ForceFontSize.Value;
            if (want > 0f) tmp.fontSize = want;
            else if (fontSize > 0f) tmp.fontSize = fontSize;
            tmp.text = label;
            tmp.color = Color.white;     // 底图已是深色，白字可读
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            try { tmp.enableAutoSizing = false; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.CreateOwnButton:638", __e); }

            Log($"  已建按钮 '{label}'（font={(font != null ? font.name : "默认")} size={tmp.fontSize} 尺寸={rt.sizeDelta}）");
            return go;
        }

        /// <summary>
        /// 找游戏现成的「揭晓！」按钮对象（实测名字是 RevealObject）。
        /// ★ 这是整个 mod 里最关键的一处发现：
        ///   游戏按钮是【多层叠加】做的 —— 一个容器装着四个子对象：
        ///     'BG'    sprite=T_CT_constellationproceed        底
        ///     'Top'   sprite=T_CT_constellationproceedbuttop  上边
        ///     'Bot'   sprite=T_CT_constellationproceedbuttom  下边
        ///     'Title' sprite=T_MM_playbutton9slicebase + TMP  标题
        ///   单独取任何一层都是纯白（color 全是 1,1,1,1），拼出来就是空白方块 ——
        ///   v0.6~v2.3 一直在犯这个错。必须克隆【整个容器】，美术才会跟着过来。
        /// </summary>
        private GameObject FindRevealButton()
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType<StarVaders.ButtonView>();
                GameObject fallback = null;
                foreach (var bv in arr)
                {
                    if (bv == null) continue;
                    var go = bv.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    var n = (go.name ?? "").ToLowerInvariant();
                    var rt = go.GetComponent<RectTransform>();
                    var sz = rt != null ? rt.sizeDelta : Vector2.zero;
                    bool hasTmp = go.GetComponentInChildren<TMPro.TextMeshProUGUI>() != null;

                    if (n.Contains("reveal")) return go;                    // 首选
                    if (fallback == null && hasTmp && sz.x > 200f) fallback = go;   // 次选：带文字的长条按钮
                }
                return fallback;
            }
            catch (Exception e) { Log("  查找揭晓按钮失败: " + e.Message); }
            return null;
        }

        /// <summary>
        /// 把克隆出来的按钮改成我们的文案，并登记到"持续校正"名单。
        /// ★ v2.4 教训：克隆体的 TMP 文字设好之后会被游戏某处每帧重写回原值
        ///   （实测日志显示改字成功，画面上却还是「揭晓！」），所以只能每帧盯着它。
        /// </summary>
        private void RetextButton(GameObject go, string label)
        {
            try
            {
                int n = 0;
                var tmps = go.GetComponentsInChildren<TMPro.TextMeshProUGUI>();
                if (tmps != null)
                    foreach (var t in tmps)
                    {
                        if (t == null) continue;
                        t.text = label;
                        try { t.ForceMeshUpdate(); } catch (Exception __e) { LogOnce.Warn("ConstellationUI.RetextButton:696", __e); }
                        n++;
                    }
                if (n > 0)
                {
                    Log($"  已改文字为 '{label}'（{n} 个 TMP），并入校正名单");
                    foreach (var t in tmps)
                    {
                        if (t == null) continue;
                        _textFixTmps.Add(t);
                        _textFixLabels.Add(label);
                    }
                }
                else Log("  克隆体里没有 TMP，需要补文字层");
            }
            catch (Exception e) { Log("  改文字失败: " + e.Message); }
        }

        /// <summary>
        /// 每帧校正按钮文字 —— 游戏会把它重写回「揭晓！」。
        /// 只比较缓存的 TMP 引用（不再 GetComponentsInChildren），每帧开销降到几次字符串比较。
        /// </summary>
        private void EnforceButtonTexts()
        {
            for (int i = 0; i < _textFixTmps.Count; i++)
            {
                var t = _textFixTmps[i];
                if (t == null) continue;
                string want = _textFixLabels[i];
                try
                {
                    if (t.text != want)
                    {
                        t.text = want;
                        try { t.ForceMeshUpdate(); } catch (Exception __e) { LogOnce.Warn("ConstellationUI.EnforceButtonTexts:730", __e); }
                    }
                }
                catch (Exception __e) { LogOnce.Warn("ConstellationUI.EnforceButtonTexts:733", __e); }
            }
        }

        /// <summary>
        /// 在指定"沿排列方向的叠加偏移"处应用锚点（主按钮 extra=0，小按钮依次叠开）。
        /// </summary>
        private void ApplyAnchorForOffset(RectTransform rt, float extra)
        {
            if (Plugin.AnchorRight.Value && Plugin.AnchorBottom.Value)
            {
                // ★ 右下角（默认）
                rt.anchorMin = new Vector2(1f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(1f, 0f);
                rt.anchoredPosition = new Vector2(-Plugin.BtnRightOffset.Value,
                                                  Plugin.BottomOffset.Value + extra);
            }
            else if (Plugin.AnchorRight.Value)
            {
                // 右上角
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-Plugin.BtnRightOffset.Value,
                                                  -Plugin.BtnTopOffset.Value - extra);
            }
            else
            {
                // 左下角
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(0f, 0f);
                rt.pivot = new Vector2(0f, 0f);
                rt.anchoredPosition = new Vector2(Plugin.LeftOffset.Value,
                                                  Plugin.BottomOffset.Value + extra);
            }
        }

        /// <summary>
        /// 检查对象树下有没有「sprite 还在、但贴图已被卸载」的 Image。
        ///
        /// ★ 为什么需要这个判据：Unity 里 sprite 引用非空、texture 为 null 时，
        ///   Image 会渲染成一个纯白方块 —— 正是用户看到的「白色色块」。
        ///   游戏的 UI 美术走 Addressables，换局时会被卸载，克隆出来的按钮就会落到这个状态。
        /// </summary>
        private static bool HasBrokenSprites(GameObject go)
        {
            try
            {
                if (go == null) return false;
                var imgs = go.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                if (imgs == null) return false;

                for (int i = 0; i < imgs.Length; i++)
                {
                    var im = imgs[i];
                    if (im == null) continue;
                    var sp = im.sprite;
                    if (sp == null) continue;          // 本来就没 sprite 的（纯色块）不算坏
                    if (sp.texture == null) return true;
                }
            }
            catch (Exception __e) { LogOnce.Warn("ConstellationUI.HasBrokenSprites:795", __e); }
            return false;
        }

        /// <summary>
        /// 销毁自建/克隆出来的所有按钮（主按钮 + 每张卡片的单项按钮）。
        ///
        /// ★ 为什么要销毁而不是复用：这些按钮的 Image.sprite 指向游戏用 Addressables 加载的美术资源，
        ///   换局时那些资源会被卸载 —— 继续复用就会渲染成白色色块（用户实测症状）。
        ///   销毁后下次打开星座界面会按新资源重建。
        /// </summary>
        private void DestroyBuiltButtons(string tag)
        {
            int killed = 0;
            try
            {
                if (_nativeButton != null)
                {
                    try { UnityEngine.Object.Destroy(_nativeButton); killed++; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.DestroyBuiltButtons:813", __e); }
                }
            }
            catch (Exception __e) { LogOnce.Warn("ConstellationUI.DestroyBuiltButtons:816", __e); }
            _nativeButton = null;

            for (int i = 0; i < _singleButtons.Count; i++)
            {
                var b = _singleButtons[i];
                if (b != null)
                {
                    try { UnityEngine.Object.Destroy(b); killed++; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.DestroyBuiltButtons:824", __e); }
                }
            }
            _singleButtons.Clear();

            _textFixTmps.Clear();
            _textFixLabels.Clear();

            _restoreAt = -1f;
            _verifyAt = -1f;
            _trimAt = -1f;

            Log($"  [{tag}] 已销毁 {killed} 个旧按钮（下次打开星座界面按新资源重建，避免白色色块）");
        }

        private void TryBuildNativeButton(ConstellationController cc)
        {            try
            {
                if (_nativeButton != null)
                {
                    // ★ 保险：sprite 还在但贴图已被卸载时，Unity 会把它画成白色方块。
                    //   换局检测不一定每次都能命中（比如指针被复用），所以复用前再验一次。
                    if (HasBrokenSprites(_nativeButton))
                    {
                        Log("  缓存按钮的贴图已失效，重建");
                        DestroyBuiltButtons("贴图失效");
                    }
                    else
                    {
                        _nativeButton.SetActive(true);

                        // ★ v3.6 修复（审查 M15）：主按钮可以复用，但**小按钮可能还没成功建过**。
                        //   首次构建时若 FindRevealButton() 返回 null，BuildSingleButtons 会直接跳过，
                        //   而旧实现在这里无条件 return —— 此后每次打开星座界面都命中复用分支，
                        //   单项刷新按钮**永远不会被创建**，用户只看到「少了三个按钮」，日志里只有一行「跳过」。
                        if (Plugin.EnableSingleReroll.Value && cc.Constellations != null && _singleButtons.Count != cc.Constellations.Count)
                        {
                            var host2 = EnsureOverlayCanvas();
                            var src2 = FindRevealButton();
                            if (host2 != null && src2 != null)
                            {
                                Log("  检测到小按钮缺失，补建一次");
                                BuildSingleButtons(src2, host2, _nativeButton);
                            }
                        }
                        return;
                    }
                }

                // 重建按钮时清空文字校正名单（主按钮与所有小按钮会重新登记）
                _textFixTmps.Clear();
                _textFixLabels.Clear();

                var host = EnsureOverlayCanvas();
                if (host == null) { SetStatus("Overlay Canvas 创建失败"); return; }

                // v0.8 教训：克隆路已证死 ——
                //   'Button'(421x87) 的 Image.sprite 实测为 null，底图其实靠 ButtonView 渲染，
                //   而 ButtonView 必须 Strip 掉（否则点它会触发原逻辑），剥完就只剩白块。
                // 所以不再克隆，一律自建：原版九宫格底图 + 游戏字体。
                if (Plugin.LogCandidates.Value)
                {
                    try { LogButtonCandidates(); } catch (Exception __e) { LogOnce.Warn("ConstellationUI.TryBuildNativeButton:870", __e); }
                }

                float fontSize = 42f;
                TMPro.TMP_FontAsset font = FindAnyFont(out fontSize);

                // ★ 优先克隆游戏现成的「揭晓！」按钮容器 —— 它自带全套分层美术
                var revealSrc = FindRevealButton();
                if (revealSrc != null)
                {
                    Log($"  克隆源: '{revealSrc.name}'（完整按钮容器）");
                    try
                    {
                        _nativeButton = UnityEngine.Object.Instantiate(revealSrc, host.transform);
                        _nativeButton.name = "DiceVaders_RerollButton_All";
                        _nativeButton.layer = 5;
                        _nativeButton.SetActive(true);
                        _nativeButton.transform.SetAsLastSibling();

                        StripGameComponents(_nativeButton);   // 只删 StarVaders 逻辑组件，子对象的美术保留
                        RetextButton(_nativeButton, "刷新全部");

                        var nrt = _nativeButton.GetComponent<RectTransform>();
                        if (nrt != null)
                        {
                            // ★ 保持源按钮的长宽比 —— 它的上/下边框是独立子对象、按原高度定位，
                            //   压缩高度会把它们推出可见区域（v2.5 实测：只剩白底、看不见边框）
                            var srt = revealSrc.GetComponent<RectTransform>();
                            float baseW = (srt != null && srt.sizeDelta.x > 1f) ? srt.sizeDelta.x : 342.8f;
                            float baseH = (srt != null && srt.sizeDelta.y > 1f) ? srt.sizeDelta.y : 123.3f;
                            float k = Plugin.BtnWidth.Value / baseW;
                            nrt.sizeDelta = new Vector2(baseW * k, baseH * k);
                            Log($"  主按钮尺寸: 源 {baseW:0}x{baseH:0} × {k:0.00} = {nrt.sizeDelta.x:0}x{nrt.sizeDelta.y:0}");
                            ApplyAnchorForOffset(nrt, 0f);
                            nrt.localScale = Vector3.one;
                            nrt.localRotation = Quaternion.identity;
                        }
                    }
                    catch (Exception e)
                    {
                        Log("  克隆揭晓按钮失败，退回自建: " + e.Message);
                        if (_nativeButton != null) { try { UnityEngine.Object.Destroy(_nativeButton); } catch (Exception __e) { LogOnce.Warn("ConstellationUI.TryBuildNativeButton:911", __e); } }
                        _nativeButton = null;
                    }
                }

                if (_nativeButton == null)
                {
                    _nativeButton = CreateOwnButton(font, fontSize, "刷新全部");
                    if (_nativeButton != null)
                    {
                        _nativeButton.transform.SetParent(host.transform, false);
                        _nativeButton.transform.SetAsLastSibling();
                        _nativeButton.SetActive(true);
                    }
                    else Log("  !! 自建按钮也失败");
                }

                // ★ 每张卡片下方的「刷新」按钮，复用同一个克隆源缩小尺寸
                if (Plugin.EnableSingleReroll.Value) BuildSingleButtons(revealSrc, host, _nativeButton);

                _verifyAt = Time.realtimeSinceStartup + 0.5f;
            }
            catch (Exception e)
            {
                Log($"创建按钮失败: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// 创建 3 个单项刷新按钮（文字统一为「刷新」），各自贴在对应星座卡片下方。
        /// 复用主按钮的克隆源，只改尺寸与文字，保证外观与原版一致。
        /// 竖排在主按钮【外侧】—— 右下角时往上叠、右上角时往下叠（由 ApplyAnchorForOffset 的 extra 决定方向）。
        /// </summary>
        private void BuildSingleButtons(GameObject src, GameObject host, GameObject mainBtn)
        {
            // 先清旧的：星座界面每次重新打开都会走到这里
            for (int i = 0; i < _singleButtons.Count; i++)
            {
                try { if (_singleButtons[i] != null) UnityEngine.Object.Destroy(_singleButtons[i]); } catch (Exception __e) { LogOnce.Warn("ConstellationUI.BuildSingleButtons:949", __e); }
            }
            _singleButtons.Clear();

            if (src == null || host == null) { Log("  小按钮：无克隆源，跳过"); return; }

            // 源尺寸（用于保持原版长宽比）
            float baseW = 342.8f, baseH = 123.3f;
            try
            {
                var srt = src.GetComponent<RectTransform>();
                if (srt != null && srt.sizeDelta.x > 1f && srt.sizeDelta.y > 1f)
                { baseW = srt.sizeDelta.x; baseH = srt.sizeDelta.y; }
            }
            catch (Exception __e) { LogOnce.Warn("ConstellationUI.BuildSingleButtons:963", __e); }

            float kOne = Plugin.SingleBtnWidth.Value / baseW;
            float oneH = baseH * kOne;
            float kMain = Plugin.BtnWidth.Value / baseW;
            float mainH = baseH * kMain;

            var ccSlots = FindController();
            int count = ccSlots != null && ccSlots.Constellations != null ? ccSlots.Constellations.Count : 0;
            for (int i = 0; i < count; i++)
            {
                _singleButtons.Add(null);
                try
                {
                    var b = UnityEngine.Object.Instantiate(src, host.transform);
                    b.name = $"DiceVaders_RerollButton_One{i + 1}";
                    b.layer = 5;
                    b.SetActive(true);
                    b.transform.SetAsLastSibling();

                    StripGameComponents(b);
                    RetextButton(b, "刷新");

                    var nrt = b.GetComponent<RectTransform>();
                    if (nrt != null)
                    {
                        nrt.sizeDelta = new Vector2(baseW * kOne, oneH);
                        nrt.localScale = Vector3.one;
                        nrt.localRotation = Quaternion.identity;
                        // 初始位置先放屏幕外，随后由 UpdateSingleButtonPositions()
                        // 每帧贴到对应卡片旁边 —— 卡片是自由布局，索引顺序 ≠ 视觉顺序，
                        // 用固定偏移排数字按钮会对不上号（v3.0 首测实测）。
                        nrt.anchorMin = nrt.anchorMax = new Vector2(0f, 0f);
                        nrt.pivot = new Vector2(0.5f, 0.5f);
                        nrt.anchoredPosition = new Vector2(-9999f, -9999f);
                    }

                    _singleButtons[i] = b;
                    Log($"  小按钮 {i + 1} 已创建 ({baseW * kOne:0}x{oneH:0})");
                }
                catch (Exception e) { Log($"  小按钮 {i + 1} 创建失败: {e.Message}"); }
            }
        }

        /// <summary>
        /// 只删除 StarVaders 命名空间的游戏逻辑脚本，其余（Image/TMP/AllIn1Shader/UIParticle 等视觉组件）一律保留。
        /// 反转白名单是有意的：游戏 UI 大量依赖 AllIn1SpriteShader 与 Coffee.UIExtensions，
        /// 之前用白名单会把这些效果组件一起删掉，导致按钮视觉异常。
        /// </summary>
        private void StripGameComponents(GameObject go)
        {
            try
            {
                var comps = go.GetComponentsInChildren<Component>(true);
                int killed = 0;
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var full = RealTypeName(comp);

                    // 只碰游戏自己的脚本
                    if (!IsGameType(full)) continue;
                    // Transform 不能删
                    if (full.EndsWith("RectTransform") || full.EndsWith("Transform")) continue;

                    try { UnityEngine.Object.Destroy(comp); killed++; }
                    catch (Exception e) { Log($"  Strip '{full}' 失败: {e.Message}"); }
                }
                Log($"  Strip 完成：删除 {killed} 个 StarVaders 逻辑组件");
            }
            catch (Exception e) { Log("  StripGameComponents 失败: " + e.Message); }
        }

        /// <summary>强制渲染状态可见：源按钮 active=False，克隆体可能继承了禁用的 Image / CanvasRenderer。</summary>
        private void ForceVisible(GameObject go)
        {
            try
            {
                var img = go.GetComponent<UnityEngine.UI.Image>();
                if (img != null)
                {
                    img.enabled = true;
                    var c = img.color; c.a = 1f; img.color = c;
                    Log($"  Image: enabled=True alpha={img.color.a} sprite={(img.sprite != null ? img.sprite.name : "null")}");
                }
                else Log("  !! 没有 Image 组件");
            }
            catch (Exception e) { Log("  修 Image 失败: " + e.Message); }

            try
            {
                var cr = go.GetComponent<CanvasRenderer>();
                if (cr != null) cr.SetAlpha(1f);
            }
            catch (Exception e) { Log("  修 CanvasRenderer 失败: " + e.Message); }

            try
            {
                var cg = go.GetComponent<CanvasGroup>();
                if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; }
            }
            catch (Exception __e) { LogOnce.Warn("ConstellationUI.ForceVisible:1061", __e); }

            try
            {
                var tmp = go.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (tmp != null) { tmp.enabled = true; Log($"  TMP: enabled=True text='{tmp.text}'"); }
            }
            catch (Exception e) { Log("  修 TMP 失败: " + e.Message); }
        }

        /// <summary>
        /// 可见性诊断 —— 刻意避开 GetWorldCorners（interop 下数组回填不可靠，恒返回 0,0 误导判断）。
        /// 改用 rect / position / 屏幕坐标这些标量与小结构。
        /// </summary>
        private void VerifyVisible(GameObject go, string tag)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"--- 可见性 [{tag}] ---");
                sb.AppendLine($"  activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy} layer={go.layer}");

                var rt = go.GetComponent<RectTransform>();
                if (rt == null) { sb.AppendLine("  !! 无 RectTransform"); Log(sb.ToString()); return; }

                var r = rt.rect;
                sb.AppendLine($"  rect: x={r.x:0} y={r.y:0} w={r.width:0} h={r.height:0}");
                sb.AppendLine($"  anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} pivot={rt.pivot}");
                sb.AppendLine($"  sizeDelta={rt.sizeDelta} anchoredPos={rt.anchoredPosition} localScale={rt.localScale}");
                sb.AppendLine($"  worldPos={rt.position}");

                try
                {
                    var sp = RectTransformUtility.WorldToScreenPoint(null, rt.position);
                    sb.AppendLine($"  屏幕坐标(Overlay 直读)=({sp.x:0},{sp.y:0})   屏幕尺寸=({Screen.width}x{Screen.height})");
                }
                catch (Exception e) { sb.AppendLine("  屏幕坐标失败: " + e.Message); }

                var img = go.GetComponent<UnityEngine.UI.Image>();
                sb.AppendLine($"  Image: {(img == null ? "无" : $"enabled={img.enabled} alpha={img.color.a:0.00}")}");

                var tmp = go.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                sb.AppendLine($"  TMP: {(tmp == null ? "无" : $"enabled={tmp.enabled} text='{tmp.text}'")}");

                var cv = go.GetComponentInParent<Canvas>();
                if (cv != null)
                    sb.AppendLine($"  Canvas: renderMode={cv.renderMode} sortingOrder={cv.sortingOrder} scaleFactor={cv.scaleFactor}");

                Log(sb.ToString());
            }
            catch (Exception e) { Log("  VerifyVisible 失败: " + e.Message); }
        }

        // ---------- 点击检测 ----------

        private void CheckButtonClick()
        {
            // 安全：只在星座界面显示中才响应点击（局内绝不响应）
            bool showing = false;
            try { showing = ConstellationController.IsShowing; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.CheckButtonClick:1145", __e); }
            if (!showing) return;

            // 主按钮：刷新全部
            if (HitTest(_nativeButton))
            {
                Log("「刷新全部」被点击");
                DoReroll();
                return;
            }

            // 各卡片下方的「刷新」按钮
            for (int i = 0; i < _singleButtons.Count; i++)
            {
                if (HitTest(_singleButtons[i]))
                {
                    Log($"「刷新第 {i + 1} 个」被点击");
                    DoRerollSingle(i);
                    return;
                }
            }
        }

        /// <summary>命中测试：鼠标左键这一帧是否点在指定 UI 上。</summary>
        private bool HitTest(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return false;
            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return false;

                var pos = mouse.position.ReadValue();
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) return false;

                // 自建 Overlay Canvas 传 null 即可；若挂到别的 Canvas 则要带相机
                Camera cam = null;
                try
                {
                    var cvs = go.GetComponentInParent<Canvas>();
                    if (cvs != null && cvs.renderMode != RenderMode.ScreenSpaceOverlay) cam = cvs.worldCamera;
                }
                catch (Exception __e) { LogOnce.Warn("ConstellationUI.HitTest:1188", __e); }

                return RectTransformUtility.RectangleContainsScreenPoint(rt, pos, cam);
            }
            catch (Exception e) { Log("  命中测试异常: " + e.Message); return false; }
        }

        // ---------- 重掷 ----------

        private void DoReroll()
        {
            if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
            if (_allPending) return;

            if (_singlePending || _singleFailed) { SetStatus("单项替换尚未完成或失败，已拦截刷新"); return; }
            var cc = FindController();
            if (cc == null) { SetStatus("不在星座场景"); return; }

            // ---- 安全拦截：绝不在局内 / 过渡中重掷 ----
            // v0.8 崩溃记录：CreateConstellations() 是往 EncounterModel.Constellations【追加】，
            // 局内调用会把本局数据堆坏，任务引擎直接崩（游戏提示"删除本局"）。
            bool showing = false, transitioning = false;
            try { showing = ConstellationController.IsShowing; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.DoReroll:1206", __e); }
            try { transitioning = cc.IsTransitioning; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.DoReroll:1207", __e); return; }

            if (!showing) { SetStatus("已拦截：当前不在星座界面"); Log("[安全] 拒绝重掷 —— IsShowing=false"); return; }
            if (transitioning) { SetStatus("已拦截：界面过渡中"); Log("[安全] 拒绝重掷 —— IsTransitioning=true"); return; }
            if (Time.realtimeSinceStartup - _lastRerollAt < 0.45f) return;   // 防连点

            if (!RerollBudgetAvailable()) return;
            try
            {
                Log($"调用 CreateConstellations() ... (第 {_rerollCount + 1} 次)");
                var routine = cc.CreateConstellations();
                if (routine == null) { SetStatus("CreateConstellations() 返回 null"); return; }
                var activeEncounter = FindEncounter();
                if (activeEncounter == null || !SingleEngineIdle(activeEncounter.TaskEngine)) return;
                _allPending = true; _allFinished = false; _rerollCount++;
                var started = cc.StartCoroutine(RerollCompletion.Run(routine, this, _epoch).WrapToIl2Cpp());
                if (started == null)
                {
                    _singleFailed = true;
                    SetStatus("未取得重掷协程句柄，本局暂停重复提交");
                    return;
                }
                _lastRerollAt = Time.realtimeSinceStartup;
                _restoreAt = -1f;
                SetStatus("已重掷，正在补详情…");
            }
            catch (Exception e) { if (_allPending) _singleFailed = true; SetStatus("重掷失败: " + e.GetType().Name + ": " + e.Message); }
        }

        // ---------- 单项重掷 ----------

        /// <summary>
        /// 单项重掷通过原生队列替换装备，不直接伪造模型或效果。
        /// 任务完成后核验实际 ID、名称、类型和展示槽所有权，再同步界面。
        /// </summary>
        private void DoRerollSingle(int index)
        {
            if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
            if (Plugin.EnableSingleReroll == null || !Plugin.EnableSingleReroll.Value) return;
            if (_allPending) return;

            if (_singlePending || _singleFailed) { SetStatus("单项替换尚未完成或失败，已拦截重复操作"); return; }
            var cc = FindController();
            var ec = FindEncounter();
            if (cc == null || ec == null) { SetStatus("不在星座场景"); return; }

            // ---- 与全部重掷一致的安全守卫 ----
            bool showing = false, transitioning = false;
            try { showing = ConstellationController.IsShowing; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.DoRerollSingle:1269", __e); }
            try { transitioning = cc.IsTransitioning; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.DoRerollSingle:1270", __e); return; }
            if (!showing) { SetStatus("已拦截：当前不在星座界面"); return; }
            if (transitioning) { SetStatus("已拦截：界面过渡中"); return; }
            if (Time.realtimeSinceStartup - _lastRerollAt < 0.45f) return;

            if (!RerollBudgetAvailable()) return;
            var em = ec.EncounterModel;
            if (em == null) { SetStatus("取不到 EncounterModel"); return; }

            var cl = em.Constellations;
            if (cl == null || index < 0 || index >= cl.Count)
            {
                SetStatus($"槽位 {index + 1} 无数据（列表 {(cl == null ? -1 : cl.Count)} 项）");
                return;
            }

            // 已锁定的槽位不动 —— 对 locked 做操作会把卡片揭到错误位置（v1.1 教训）
            bool locked = false;
            try
            {
                var ui = cc.Constellations;
                if (ui == null || index >= ui.Count || ui[index] == null)
                { SetStatus("目标卡片不可读，未提交任务"); return; }
                locked = ui[index].isLocked;
            }
            catch (Exception __e) { LogOnce.Warn("ConstellationUI.DoRerollSingle:1292", __e); return; }
            if (locked) { SetStatus($"槽位 {index + 1} 已锁定，跳过"); return; }

            try
            {
                // 1) 算候选池（复刻游戏自己的三重过滤）—— 结果写在 _candidatePool 字段里
                BuildCandidatePool(em);
                var pool = _candidatePool;
                if (pool.Count == 0) { SetStatus("没有可替换的星座（候选池为空）"); return; }

                var te = ec.TaskEngine;
                if (!SingleEngineIdle(te)) { SetStatus("任务引擎忙或状态未知，稍后再刷新"); return; }
                int slotIndex = 13 + index; // 原生 CreateConstellations: 一起点位置 14+i。
                if (em.ArtifactSlots == null || slotIndex >= em.ArtifactSlots.Count)
                { SetStatus("星座实际装备槽不存在，未提交任务"); return; }
                var oldId = em.ArtifactSlots[slotIndex];
                if (cl[index].Number != oldId.Number)
                { SetStatus("展示与装备已不同步，未提交任务，请重新进入星座界面"); return; }

                // 使用 Unity 随机源选候选，不改变游戏存档中的随机状态。
                var pick = pool[UnityEngine.Random.Range(0, pool.Count)];
                // Position 必须是装箱 Int32（一基槽号），不能装箱 ArtifactID。
                Il2CppSystem.Object position = slotIndex + 1;
                var task = new CreateArtifactTask(pick.ArtifactName, position, false, false);
                _singleEncounterPtr = ec.Pointer;
                _singleModelPtr = em.Pointer;
                _singleIndex = index;
                _singleOldId = oldId.Number;
                _singleName = pick.ArtifactName;
                _singleStartedAt = Time.realtimeSinceStartup;
                _singleWaitLogged = false;
                _singlePending = true;
                // 调用前已经记下所有权；抛错时也不再猜测任务是否部分提交。
                _rerollCount++;
                te.QueueTask(task);
                _lastRerollAt = Time.realtimeSinceStartup;
                _restoreAt = -1f;
                _trimAt = -1f;
                SetStatus($"槽位 {index + 1} 正在替换…");
                Log($"[单项重掷] 已入队: 槽号={slotIndex + 1} 旧ID={_singleOldId} 名字={_singleName}");
            }
            catch (Exception e)
            {
                if (_singlePending) { _singlePending = false; _singleFailed = true; }
                SetStatus("单项重掷失败: " + e.GetType().Name + ": " + e.Message);
                Log($"[单项重掷] 异常: {e}");
            }
        }

        private bool RerollBudgetAvailable()
        {
            if (_rerollCount < (Plugin.MaxRerollPerSession?.Value ?? 20)) return true;
            SetStatus("已达本次星座界面重掷上限");
            return false;
        }

        internal void FinishAllReroll(int epoch, bool success)
        {
            var ec = FindEncounter();
            if (ec == null || ec.Pointer != _lastEncounterPtr || ec.EncounterModel == null ||
                ec.EncounterModel.Pointer != _lastModelPtr || epoch != _epoch || !_allPending) return;
            if (success) _allFinished = true;
            else _singleFailed = true;
        }

        private bool SingleEngineIdle(TaskEngine te)
        {
            try
            {
                return te != null && !te.IsPreviewMode && !te.IsStopped &&
                    te.TaskQueue != null && te.TaskQueue.Count == 0 &&
                    te.RunningStack != null && te.RunningStack.MainStack != null &&
                    te.RunningStack.MainStack.Count == 0;
            }
            catch (Exception e) { LogOnce.Warn("单项重掷.引擎状态", e); return false; }
        }

        private void ResetSingleReroll()
        {
            _singlePending = false;
            _singleFailed = false;
            _singleViewDirty = false;
            _singleEncounterPtr = IntPtr.Zero;
            _singleModelPtr = IntPtr.Zero;
        }

        private void PollSingleReroll()
        {
            if (!_singlePending) return;
            try
            {
                var ec = FindEncounter();
                var em = ec != null ? ec.EncounterModel : null;
                if (ec == null || em == null || ec.Pointer != _singleEncounterPtr || em.Pointer != _singleModelPtr)
                { ResetSingleReroll(); Log("[单项重掷] 对局已变化，取消旧局展示回写"); return; }
                if (!SingleEngineIdle(ec.TaskEngine))
                {
                    if (!_singleWaitLogged && Time.realtimeSinceStartup - _singleStartedAt > 30f)
                    {
                        _singleWaitLogged = true;
                        Log("[单项重掷] 原生任务仍忙或引擎未知，保持阻挡；不终止原生任务，不提前改卡片");
                    }
                    return;
                }
                int slot = 13 + _singleIndex;
                if (em.ArtifactSlots == null || slot >= em.ArtifactSlots.Count ||
                    em.Constellations == null || _singleIndex >= em.Constellations.Count)
                    throw new InvalidOperationException("任务完成后星座槽数量不匹配");
                var id = em.ArtifactSlots[slot];
                var model = GetSingleArtifactModel(em, id);
                if (id.Number == _singleOldId || model == null || model.ArtifactName != _singleName ||
                    model.ArtifactType != ArtifactType.Constellation || !em.HasArtifact(_singleName))
                    throw new InvalidOperationException("原生任务未得到预期装备结果");
                if (em.Constellations[_singleIndex].Number != _singleOldId)
                    throw new InvalidOperationException("展示槽被其他流程修改，拒绝覆盖");
                em.Constellations[_singleIndex] = id;
                _singlePending = false;
                _singleViewDirty = true;
                var cc = FindController();
                if (Plugin.Enabled != null && Plugin.Enabled.Value && cc != null && ConstellationController.IsShowing && !cc.IsTransitioning)
                {
                    TryUpdateView(cc);
                    _singleViewDirty = false;
                    _restoreAt = Time.realtimeSinceStartup + Plugin.RestoreDelay.Value;
                }
                SetStatus($"槽位 {_singleIndex + 1} → {_singleName}");
                Log($"[单项重掷] 装备完成: 槽号={slot + 1} 旧ID={_singleOldId} 新ID={id.Number} 名字={model.ArtifactName}");
            }
            catch (Exception e)
            {
                _singlePending = false;
                _singleFailed = true;
                SetStatus("单项替换结果无法确认，已阻挡后续刷新；请结束本局后检查日志");
                Log("[单项重掷] 完成核查失败，未伪造展示结果: " + e);
            }
        }

        private ArtifactModel GetSingleArtifactModel(EncounterModel em, ArtifactID id)
        {
            unsafe
            {
                ArtifactID local = id;
                IntPtr boxed = IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<ArtifactID>.NativeClassPtr, (IntPtr)(&local));
                if (boxed == IntPtr.Zero) throw new InvalidOperationException("神器 ID 装箱失败");
                return em.GetModelItem<ArtifactModel>(new ID(boxed));
            }
        }

        /// <summary>
        /// 构建可选星座池 —— 复刻游戏 CreateConstellations 里那三个过滤谓词。
        ///
        /// ★ 结果写入实例字段 _candidatePool，方法本身返回 void：
        ///   Il2CppInterop 注入 mono 类型时会拒绝签名里含托管类型的方法
        ///   （运行时日志实证："has unsupported return type System.Collections.Generic.List`1"），
        ///   所以不能返回 List&lt;T&gt;，参数也只能留 IL2CPP 类型。
        /// </summary>
        private void BuildCandidatePool(StarVaders.EncounterModel em)
        {
            _candidatePool.Clear();
            try
            {
                var all = ContentGetter.GetAllArtifactsOfType(ArtifactType.Constellation);
                if (all == null) { Log("  候选池: GetAllArtifactsOfType(Constellation) 返回 null"); return; }

                // 收集「本局已有」的 ArtifactName。
                // 从 UI 槽位的 ArtifactModel 取名字，避开 GetModelItem<ID> 的装箱麻烦。
                var taken = new System.Collections.Generic.HashSet<int>();
                try
                {
                    var cc0 = FindController();
                    if (cc0 != null)
                    {
                        var ui = cc0.Constellations;
                        if (ui != null)
                        {
                            for (int j = 0; j < ui.Count; j++)
                            {
                                var c = ui[j];
                                if (c == null) continue;
                                try
                                {
                                    var am0 = c.ArtifactModel;
                                    if (am0 != null) taken.Add((int)am0.ArtifactName);
                                }
                                catch (Exception __e) { LogOnce.Warn("ConstellationUI.BuildCandidatePool:1414", __e); }
                            }
                        }
                    }
                }
                catch (Exception __e) { LogOnce.Warn("ConstellationUI.BuildCandidatePool:1419", __e); }

                for (int i = 0; i < all.Count; i++)
                {
                    var a = all[i];
                    if (a == null) continue;
                    try
                    {
                        if (!ContentGetter.IsArtifactGettableInCurrentRun(a.ArtifactName, em)) continue;
                        if (taken.Contains((int)a.ArtifactName)) continue;
                        _candidatePool.Add(a);
                    }
                    catch (Exception __e) { LogOnce.Warn("ConstellationUI.BuildCandidatePool:1431", __e); }
                }
                Log($"  候选池: 全部 {all.Count} / 界面已占 {taken.Count} / 可用 {_candidatePool.Count}");
            }
            catch (Exception e) { Log("  构建候选池失败: " + e.Message); }
        }

        private readonly System.Collections.Generic.List<ArtifactModel> _candidatePool = new System.Collections.Generic.List<ArtifactModel>();

        /// <summary>
        /// 把 3 个小按钮贴到各自对应的星座卡片旁边。
        ///
        /// ★ 为什么不能用固定偏移排：游戏这 3 张卡片是自由布局的，屏幕上的视觉顺序与
        ///   cc.Constellations 的索引顺序不一致 —— 按索引排出来的 "1/2/3" 会对不上号
        ///   （v3.0 首测实测：点「2」刷新的是看着像「1」的那张）。
        ///   改成直接跟随卡片位置，位置本身就是对应关系。
        /// </summary>
        private void UpdateSingleButtonPositions(ConstellationController cc)
        {
            if (_singleButtons.Count == 0 || cc == null) return;
            var ui = cc.Constellations;
            if (ui == null) return;

            // 取渲染相机：游戏的 Canvas 是 ScreenSpaceCamera，世界坐标必须经它转换。
            // 注意 ConstellationController.Canvas 字段其实是 CanvasGroup（不是 Canvas），
            // 所以从卡片对象往上找真正的 Canvas。
            Camera cam = null;
            try
            {
                for (int i = 0; i < ui.Count; i++)
                {
                    var ci = ui[i];
                    if (ci == null) continue;
                    var cv = ci.GetComponentInParent<Canvas>();
                    if (cv != null) { cam = cv.worldCamera; break; }
                }
            }
            catch (Exception __e) { LogOnce.Warn("ConstellationUI.UpdateSingleButtonPositions:1468", __e); }

            for (int i = 0; i < _singleButtons.Count && i < ui.Count; i++)
            {
                var b = _singleButtons[i];
                var c = ui[i];
                if (b == null || c == null) continue;
                try
                {
                    Vector3 world = c.transform.position;
                    Vector3 screen = (cam != null) ? cam.WorldToScreenPoint(world) : world;
                    if (screen.z < 0f) continue;      // 在相机背后，跳过

                    var rt = b.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    // Overlay Canvas 的坐标就是屏幕像素（锚点在左下角）。
                    // 偏移量按实测界面标定：Constellation 的 transform 落在「连线图与卡片之间」，
                    // 再往下 370 像素才到卡片底部附近（v3.0.1 首测截图标定）。
                    rt.anchoredPosition = new Vector2(screen.x, screen.y - 370f);
                }
                catch (Exception __e) { LogOnce.Warn("ConstellationUI.UpdateSingleButtonPositions:1488", __e); }
            }
        }

        /// <summary>
        /// 打开每个 Constellation 的卡片承载部件。
        /// 这四个字段是 private，但 Il2CppInterop 会为它们生成同名属性：
        ///   UnitSetObject(0x40) / ArtifactSetObject(0x48) / PanelDescription(0x50) / RevealTransform(0x70)
        /// </summary>
        private void ActivateCardParts(ConstellationController cc)
        {
            int on = 0, fail = 0;
            try
            {
                var list = cc.Constellations;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null) continue;
                    try
                    {
                        if (c.UnitSetObject != null) { c.UnitSetObject.SetActive(true); on++; }
                        if (c.ArtifactSetObject != null) { c.ArtifactSetObject.SetActive(true); on++; }
                        if (c.PanelDescription != null) { c.PanelDescription.enabled = true; on++; }
                    }
                    catch (Exception e) { fail++; Log($"  激活卡片部件[{i}] 失败: {e.GetType().Name}: {e.Message}"); }
                }
                Log($"  卡片部件激活: 打开 {on} 个 / 失败 {fail}");
            }
            catch (Exception e) { Log("  ActivateCardParts 异常: " + e.Message); }
        }

        /// <summary>尝试整表刷新。此前 UI 3 项 vs 数据 2 项导致越界，现在数量一致后再试。</summary>
        private void TryUpdateView(ConstellationController cc)
        {
            try
            {
                cc.UpdateConstellationView();
                Log("  UpdateConstellationView() 调用成功");
            }
            catch (Exception e)
            {
                Log($"  UpdateConstellationView() 仍失败（可忽略）: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// 按完全可读的未锁定卡片数去掉原生全部重掷累积的旧列表项。
        /// 界面关闭、过渡、引擎忙或状态未知时不删除；原生删除失败时不再追加重试。
        /// </summary>
        private void TrimModelConstellations(ConstellationController cc)
        {
            try
            {
                var ec = FindEncounter();
                if (Plugin.Enabled == null || !Plugin.Enabled.Value || cc == null ||
                    !ConstellationController.IsShowing || cc.IsTransitioning || ec == null ||
                    ec.EncounterModel == null || !SingleEngineIdle(ec.TaskEngine)) return;
                var cl = ec.EncounterModel.Constellations;
                if (cl == null) return;

                // UI 需要多少个星座数据 = 未锁定的槽位数
                int need = 0;
                try
                {
                    var ui = cc.Constellations;
                    if (ui != null)
                    {
                        for (int i = 0; i < ui.Count; i++)
                        {
                            var c = ui[i];
                            if (c == null) return; // 部分可读的列表不能成为删除依据。
                            bool locked = false;
                            try { locked = c.isLocked; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.TrimModelConstellations:1568", __e); return; }
                            if (!locked) need++;
                        }
                    }
                }
                catch (Exception e2) { Log("  统计 UI 槽位失败: " + e2.Message); return; }
                // 统计不到就放弃裁剪 —— 早先这里硬编码 need=2，在已解锁 3 个槽位的局里会误删真实数据。
                if (need <= 0) { Log("  UI 槽位数统计不到，跳过本次规范化（不猜）"); return; }

                int before = cl.Count;
                if (before > need)
                {
                    int remove = before - need;
                    try
                    {
                        cl.RemoveRange(0, remove);
                        Log($"  规范化: {before} -> {cl.Count}（裁掉开头 {remove} 项，UI 需 {need}）");
                    }
                    catch (Exception e3)
                    {
                        Log("  规范化提交失败，停止继续删除: " + e3.Message);
                    }
                }
                else
                {
                    Log($"  无需规范化（{before} 项，UI 需 {need}）");
                }
            }
            catch (Exception e) { Log("  规范化失败: " + e.Message); }
        }

        /// <summary>
        /// 稳定且引擎空闲后补齐连线图与详情，并刷新原生界面。
        /// 未锁定卡片才执行重新揭晓；未知过渡状态不授权修改。
        /// </summary>
        private void RestoreDetails(ConstellationController cc)
        {
            if (Plugin.Enabled == null || !Plugin.Enabled.Value || !ConstellationController.IsShowing) return;
            if (cc == null) { Log("补详情：controller 没了"); return; }
            int ok = 0, fail = 0, none = 0, shape = 0, detail = 0, reveal = 0, skipLocked = 0;
            try
            {
                var list = cc.Constellations;
                if (list == null) { Log("补详情：列表 null"); return; }
                var activeEncounter = FindEncounter();
                if (activeEncounter == null || !SingleEngineIdle(activeEncounter.TaskEngine))
                {
                    _restoreAt = Time.realtimeSinceStartup + 1f;
                    return;
                }

                // 异步刷新尚未完成时延后，避免修改其正在索引的列表。
                bool transitioning = false;
                try { transitioning = cc.IsTransitioning; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.RestoreDetails:1630", __e); return; }
                if (transitioning)
                {
                    _restoreAt = Time.realtimeSinceStartup + 1.0f;
                    return;
                }
                else
                {
                    TrimModelConstellations(cc);
                }

                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null) { fail++; continue; }
                    ArtifactModel am = null;
                    try { am = c.ArtifactModel; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.RestoreDetails:1646", __e); }
                    if (am == null) { none++; continue; }

                    // 1) 基础字段（标题等）
                    try { c.Initialize(am, cc); ok++; }
                    catch (Exception e) { fail++; Log($"  补[{i}] Initialize 失败: {e.GetType().Name}: {e.Message}"); }

                    // 2) 星座连线图
                    //    v1.2 试过 ResetAndPlay，未见改善；回退到最简的 Build+Play。
                    try
                    {
                        var sv = c.ShapeVisualizer;
                        if (sv != null)
                        {
                            sv.SetConstellation(am.ArtifactName);
                            sv.Build();
                            try { sv.Play(); } catch (Exception __e) { LogOnce.Warn("ConstellationUI.RestoreDetails:1666", __e); }
                            shape++;
                        }
                    }
                    catch (Exception e) { Log($"  补[{i}] 连线失败: {e.GetType().Name}: {e.Message}"); }

                    // 3) 详情卡片（私有方法，用 SendMessage 触发）
                    try
                    {
                        c.gameObject.SendMessage("SetConstellationToArtifact", am, SendMessageOptions.DontRequireReceiver);
                        detail++;
                    }
                    catch (Exception e) { Log($"  补[{i}] 卡片失败: {e.GetType().Name}: {e.Message}"); }

                    // 4) Initialize / SetConstellationToArtifact 之后可能又被置回 true，再兜一次
                    try { if (c.isRevealed) c.isRevealed = false; } catch (Exception __e) { LogOnce.Warn("ConstellationUI.RestoreDetails:1681", __e); }

                    // 5) 自动揭晓（仅限已解锁星座）★ 详情卡片的真正装配入口
                    //    用户实测事实：游戏原版是"一次性生成"，重掷后详情卡片不再装配。
                    //    对策：先把揭示状态复位，再走完整的 OnPressReveal() 流程迫使它重建。
                    //    只对未锁定星座做 —— 对 locked 的做会把未解锁卡片揭到错误位置（v1.1 教训）。
                    if (!c.isLocked)
                    {
                        try
                        {
                            c.isRevealed = false;
                            c.isRevealing = false;
                            c.OnPressReveal();
                            reveal++;
                        }
                        catch (Exception e) { Log($"  揭晓[{i}] 失败: {e.GetType().Name}: {e.Message}"); }
                    }
                    else skipLocked++;
                }
                SetStatus($"OK {ok} / 连线 {shape} / 卡片 {detail} / 揭晓 {reveal} / 锁定跳过 {skipLocked} / 空 {none} / 失败 {fail}");
                Log($"RestoreDetails: ok={ok} shape={shape} detail={detail} reveal={reveal} skipLocked={skipLocked} none={none} fail={fail}");

                // ④ 手动把卡片的承载部件打开 —— Initialize 这条路径不会打开它们
                ActivateCardParts(cc);

                // ⑤ 数量现已一致，再试一次整表刷新（此前因 3 vs 2 越界而失败）
                TryUpdateView(cc);

                Dump("补详情后");
            }
            catch (Exception e) { Log("RestoreDetails 异常: " + e.Message); }
        }

        // ---------- dump ----------

        private void Dump(string tag)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"===== 星座状态 [{tag}] =====");
            var cc = FindController();
            if (cc == null) sb.AppendLine("  不在星座场景");
            else
            {
                sb.AppendLine($"  IsShowing={Safe(() => ConstellationController.IsShowing.ToString())} IsTransitioning={Safe(() => cc.IsTransitioning.ToString())}");
                try
                {
                    var list = cc.Constellations;
                    sb.AppendLine($"  cc.Constellations.Count = {(list == null ? -1 : list.Count)}");
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                        {
                            var c = list[i];
                            if (c == null) { sb.AppendLine($"    [{i}] null"); continue; }
                            string am = "null";
                            try { if (c.ArtifactModel != null) am = c.ArtifactModel.ArtifactName.ToString(); } catch (Exception __e) { LogOnce.Warn("ConstellationUI.Dump:1735", __e); }
                            sb.AppendLine($"    [{i}] '{Safe(() => c.name)}' locked={Safe(() => c.isLocked.ToString())} revealed={Safe(() => c.isRevealed.ToString())} Artifact={am}");
                        }
                }
                catch (Exception e) { sb.AppendLine("  读失败: " + e.Message); }
            }

            var ec = FindEncounter();
            if (ec != null)
            {
                try
                {
                    var em = ec.EncounterModel;
                    if (em != null)
                    {
                        var cl = em.Constellations;
                        sb.AppendLine($"  EncounterModel.Constellations.Count = {(cl == null ? -1 : cl.Count)}");
                        if (cl != null)
                            for (int i = 0; i < cl.Count; i++)
                                try { sb.AppendLine($"    [{i}] {cl[i]}"); } catch { sb.AppendLine($"    [{i}] <err>"); }
                        sb.AppendLine($"  Difficulty={Safe(() => em.CurrentDifficulty.ToString())} Challenge={Safe(() => em.CurrentChallenge.ToString())}");
                    }
                }
                catch (Exception e) { sb.AppendLine("  读 EncounterModel 失败: " + e.Message); }
            }
            Log(sb.ToString());
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch (Exception e) { return "<err:" + e.Message + ">"; }
        }

        // ---------- 兜底调试面板 ----------

        private void OnGUI()
        {
            if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
            if (Plugin.ShowImGuiPanel == null || !Plugin.ShowImGuiPanel.Value) return;

            var cc = FindController();
            bool inScene = cc != null;
            const float W = 210f, H = 26f;
            float x = 12f, y = 12f;
            var old = GUI.color;

            GUI.color = inScene ? Color.white : new Color(1f, 1f, 1f, 0.5f);
            GUI.Label(new Rect(x, y, W, 20f), inScene ? "星座工具（调试）" : "非星座场景");
            y += 22f;
            GUI.enabled = inScene;

            GUI.color = new Color(0.2f, 0.85f, 0.5f, 1f);
            if (GUI.Button(new Rect(x, y, W, H), "刷新星座")) DoReroll();
            y += H + 4f;
            GUI.color = new Color(0.4f, 0.6f, 0.9f, 1f);
            if (GUI.Button(new Rect(x, y, W, H), "补详情")) RestoreDetails(cc);
            y += H + 4f;
            GUI.color = new Color(0.9f, 0.75f, 0.3f, 1f);
            if (GUI.Button(new Rect(x, y, W, H), "重建按钮")) { _nativeButton = null; if (cc != null) TryBuildNativeButton(cc); }
            y += H + 4f;
            GUI.color = new Color(0.55f, 0.55f, 0.55f, 1f);
            if (GUI.Button(new Rect(x, y, W, H), "打印状态")) Dump("手动");
            y += H + 4f;

            GUI.enabled = true;
            if (!string.IsNullOrEmpty(_status) && Time.realtimeSinceStartup < _statusUntil)
            {
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y, W, 46f), _status);
            }
            GUI.color = old;
        }
    }
    internal static class RerollCompletion
    {
        internal static System.Collections.IEnumerator Run(Il2CppSystem.Collections.IEnumerator native, ConstellationUI owner, int epoch)
        {
            while (true)
            {
                bool more; Il2CppSystem.Object current = null;
                try { more = native.MoveNext(); if (more) current = native.Current; }
                catch (Exception e) { owner.FinishAllReroll(epoch, false); ModKitLog.Warn("星座原生重掷失败: " + e.Message); yield break; }
                if (!more) break;
                yield return current;
            }
            owner.FinishAllReroll(epoch, true);
        }
    }

}
