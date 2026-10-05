using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using UnityEngine;
using UnityEngine.UI;

namespace DiceVaders.NativeSandbox
{
    /// <summary>
    /// 在游戏原生的「选项与更多 → 沙盒」面板里追加【四行】开关。
    ///
    /// ═══ 实证依据（全部来自 dump.cs + GameAssembly.dll 反汇编）═══
    ///
    /// SettingsSceneController  (RVA 0x1C39BE0 Awake)
    ///   0xE8 SkipAnimationsToggle        跳过侵略动画
    ///   0xF0 ShowCompletionStarsToggle   显示完成星标
    ///   0xF8 AssistModeToggle            辅助模式（每回合+1挪移和+1时空点）  ← 拿它当模板
    ///   0x100 RichModeToggle             富裕模式
    ///   0x108 GrowModeToggle             成长模式
    ///   0x110 RemoveHardPilotsToggle
    ///   0x118 DisableLockedArtifactSlotsToggle
    ///   0x120 DisableConstellationsToggle
    ///
    /// OptionView   0xB0 = Description (TextMeshProUGUI)  ← 行文字
    /// OptionToggle 0xC0 = Toggle      (UnityEngine.UI.Toggle) ← 开关本体
    ///
    /// ★ 做法：Hook Awake 的 Postfix，把 AssistModeToggle 的 GameObject 克隆四份，
    ///   挂到同一个父节点下。不接管原组件的回调 —— 改成每 0.15 秒读 isOn 同步到注册表，
    ///   避开 Il2CppInterop 的委托编组坑。
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders Native Sandbox Rows", "1.1.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.nativesandbox";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<bool> Enabled;

        public override void Load()
        {
            Logger = Log;
            ModKitLog.Sink      = m => Logger.LogInfo(m);
            ModKitLog.WarnSink  = m => Logger.LogWarning(m);
            ModKitLog.ErrorSink = m => Logger.LogError(m);
            ModToggleRegistry.Log = m => Logger.LogInfo(m);

            Enabled = Config.Bind("1-开关", "Enabled", true,
                "在游戏的「沙盒设置」面板里追加四行 mod 开关。");

            Logger.LogInfo("===== DiceVaders NativeSandbox v1.1.0 =====");

            // ★ v1.1：类型注册与组件挂载拆成两个 try ——
            //   旧实现放在同一个 try 里，注册失败会连带跳过挂载，
            //   而且只在报告里留一行文字、日志仍打「已加载」，排查时具有误导性。
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<ToggleWatcher>();
            }
            catch (Exception e)
            {
                Logger.LogError("NativeSandbox: ToggleWatcher 类型注册失败，开关同步功能不可用: "
                                + e.GetType().Name + ": " + e.Message);
                return;
            }

            try
            {
                var go = new GameObject("DiceVaders_NativeSandbox");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<ToggleWatcher>();
            }
            catch (Exception e)
            {
                Logger.LogError("NativeSandbox: ToggleWatcher 挂载失败，开关同步功能不可用: "
                                + e.GetType().Name + ": " + e.Message);
                return;
            }

            // ★ v1.1（审查 L21）：补上异常保护 —— 与项目其它插件（QoL）的写法保持一致。
            //   旧实现裸调 PatchAll，失败时唯一线索是「已挂」这行日志没出现。
            try
            {
                var harmony = new Harmony("dicevaders.nativesandbox");
                harmony.PatchAll(typeof(SandboxRowInjector));
                Logger.LogInfo("NativeSandbox: Harmony 补丁已挂");
            }
            catch (Exception e)
            {
                Logger.LogError("NativeSandbox: Harmony 补丁挂载失败（面板里不会有 Mod 行）: "
                                + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    /// <summary>把三行开关塞进沙盒面板。</summary>
    public static class SandboxRowInjector
    {
        /// <summary>注入后的三行，供 ToggleWatcher 轮询。null = 还没注入 / 面板已销毁。</summary>
        public static StarVaders.OptionToggle RowConstellation;
        public static StarVaders.OptionToggle RowResource;
        public static StarVaders.OptionToggle RowProbability;
        public static StarVaders.OptionToggle RowExtraPerk;

        /// <summary>
        /// 每行对应的自定义文字。
        ///
        /// ★ 为什么要反复重写：实测第一版只在注入时写一次，结果面板显示的还是原模板的
        ///   「辅助模式（每回合+1挪移和+1时空点）」× 3 —— 说明游戏在 Awake 之后又跑了
        ///   一遍本地化/文本刷写，把我们的文字覆盖回去了。所以这里留着标签，
        ///   由 ToggleWatcher 定时重新写一遍。
        /// </summary>
        public static readonly string[] RowLabels =
        {
            "★ Mod：星座可刷新",
            "★ Mod：无限时空点/挪移",
            "★ Mod：显示稀有度概率",
            "★ Mod：开局额外专长",
        };

        private static bool _logged;

        [HarmonyPatch(typeof(StarVaders.SettingsSceneController), nameof(StarVaders.SettingsSceneController.Awake))]
        [HarmonyPostfix]
        public static void Awake_Postfix(StarVaders.SettingsSceneController __instance)
        {
            if (Plugin.Enabled != null && !Plugin.Enabled.Value) return;
            try
            {
                Inject(__instance);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo("NativeSandbox 注入失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Inject(StarVaders.SettingsSceneController sc)
        {
            // 拿「辅助模式」这一行当模板 —— 它和我们加的行长得最像（都是普通开关键）
            var proto = sc.AssistModeToggle;
            if (proto == null)
            {
                Plugin.Logger?.LogInfo("NativeSandbox: AssistModeToggle 为 null，跳过注入");
                return;
            }

            var protoGo = proto.gameObject;
            if (protoGo == null) { Plugin.Logger?.LogInfo("NativeSandbox: 模板 GameObject 为空"); return; }

            var parent = protoGo.transform.parent;
            if (parent == null) { Plugin.Logger?.LogInfo("NativeSandbox: 模板没有父节点"); return; }

            // ★ 防止重复注入：实测日志里「模板父节点 = VerticalLayoutGroup」出现了 4 次，
            //   说明 Awake 被调用多次 → 会注入多组行，多出来的行会和新行互相打架
            //   （表现为 ModToggles 的状态日志来回抖动）。
            //
            // ★ v1.1 修复（审查 M22）：旧实现是「发现任意一行 DV_ModRow_ 就整体 return」——
            //   如果上次注入只成功了一半（比如 4 行里成了 2 行），后续每次都直接返回，
            //   缺的行**永远不会被补上**。改成按精确名字核对四行齐不齐。
            bool allPresent = true;
            try
            {
                allPresent = RowExists(parent, 1) && RowExists(parent, 2)
                          && RowExists(parent, 3) && RowExists(parent, 4);
                if (allPresent)
                {
                    Plugin.Logger?.LogInfo("NativeSandbox: 该面板四行已齐，跳过注入");
                    return;
                }
                if (RowExists(parent, 1) || RowExists(parent, 2) || RowExists(parent, 3) || RowExists(parent, 4))
                    Plugin.Logger?.LogWarning("NativeSandbox: 检测到上次只注入了一部分，本次补齐缺失的行");
            }
            catch (Exception __e) { LogOnce.Warn("SandboxRowInjector.重复检测", __e); }

            Plugin.Logger?.LogInfo($"NativeSandbox: 模板父节点 = {parent.name}，层级 {parent.GetSiblingIndex()}");

            // 按需克隆 —— 已经存在的那一行不再重复建
            if (RowConstellation == null || RowConstellation.gameObject == null)
                RowConstellation = CloneRow(protoGo, parent, 1, RowLabels[0], ModToggleRegistry.GetConstellationReroll());
            if (RowResource == null || RowResource.gameObject == null)
                RowResource = CloneRow(protoGo, parent, 2, RowLabels[1], ModToggleRegistry.GetAutoResource());
            if (RowProbability == null || RowProbability.gameObject == null)
                RowProbability = CloneRow(protoGo, parent, 3, RowLabels[2], ModToggleRegistry.GetProbabilityDisplay());
            if (RowExtraPerk == null || RowExtraPerk.gameObject == null)
                RowExtraPerk = CloneRow(protoGo, parent, 4, RowLabels[3], ModToggleRegistry.GetExtraPerk());

            // ★ v1.1（审查 M22）：诊断信息**成功只报一次、失败每次都报** ——
            //   旧实现是首次注入后 _logged 永久为 true，面板重建时（唯一能定位
            //   「文字改不上」的手段）DumpRowInfo 就不再执行了。
            bool allOk = RowConstellation != null && RowResource != null
                      && RowProbability != null && RowExtraPerk != null;
            if (allOk)
            {
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Logger?.LogInfo("NativeSandbox: 四行注入结果 全部成功");
                    DumpRowInfo("星座", RowConstellation);
                    DumpRowInfo("资源", RowResource);
                    DumpRowInfo("概率", RowProbability);
                    DumpRowInfo("专长", RowExtraPerk);
                }
            }
            else
            {
                Plugin.Logger?.LogWarning(
                    $"NativeSandbox: 四行注入不完整 星座={(RowConstellation != null)} " +
                    $"资源={(RowResource != null)} 概率={(RowProbability != null)} " +
                    $"专长={(RowExtraPerk != null)}");
                DumpRowInfo("星座", RowConstellation);
                DumpRowInfo("资源", RowResource);
                DumpRowInfo("概率", RowProbability);
                DumpRowInfo("专长", RowExtraPerk);
            }
        }

        /// <summary>父节点下有没有名字正好是 DV_ModRow_{index} 的子对象。</summary>
        private static bool RowExists(Transform parent, int index)
        {
            string want = "DV_ModRow_" + index;
            int n = parent.childCount;
            for (int i = 0; i < n; i++)
            {
                var ch = parent.GetChild(i);
                if (ch != null && ch.name == want) return true;
            }
            return false;
        }

        /// <summary>把一行的组件情况写进日志，便于定位「文字改不上」这类问题。</summary>
        private static void DumpRowInfo(string tag, StarVaders.OptionToggle row)
        {
            try
            {
                if (row == null) { Plugin.Logger?.LogInfo($"NativeSandbox[{tag}]: row 为空"); return; }
                var ov = row.GetComponent<StarVaders.OptionView>();
                string descNow = (ov != null && ov.Description != null) ? ov.Description.text : "(Description 为空)";
                Plugin.Logger?.LogInfo(
                    $"NativeSandbox[{tag}]: OptionView={(ov != null)} " +
                    $"Description={(ov != null && ov.Description != null)} " +
                    $"Toggle={(row.Toggle != null)} 当前文字=\"{descNow}\"");
            }
            catch (Exception e) { Plugin.Logger?.LogInfo($"NativeSandbox[{tag}] DumpRowInfo 失败: {e.Message}"); }
        }

        /// <summary>把标签重新写回四行（游戏本地化会覆盖，所以由 ToggleWatcher 定时调用）。
        /// ★ v1.1（审查 L22）：改为写缓存的 OptionView 引用 —— 旧实现每次调用都对四行各做一次
        ///   泛型 GetComponent&lt;OptionView&gt;()，按 0.15 秒的节奏就是约 27 次/秒的
        ///   il2cpp 类型解析与封送。</summary>
        public static void ReapplyLabels()
        {
            ApplyLabelCached(RowConstellation, ref _ovConstellation, RowLabels[0]);
            ApplyLabelCached(RowResource, ref _ovResource, RowLabels[1]);
            ApplyLabelCached(RowProbability, ref _ovProbability, RowLabels[2]);
            ApplyLabelCached(RowExtraPerk, ref _ovExtraPerk, RowLabels[3]);
        }

        private static StarVaders.OptionView _ovConstellation, _ovResource, _ovProbability, _ovExtraPerk;

        private static void ApplyLabelCached(StarVaders.OptionToggle row,
            ref StarVaders.OptionView cached, string label)
        {
            try
            {
                if (row == null) return;
                // 缓存失效（对象被销毁）时重新取一次
                if (cached == null) cached = row.GetComponent<StarVaders.OptionView>();
                if (cached == null || cached.Description == null) return;
                if (cached.Description.text != label) cached.Description.text = label;
            }
            catch (Exception __e) { LogOnce.Warn("SandboxRowInjector.ApplyLabel", __e); }
        }

        /// <summary>面板销毁 / 换局时清掉静态引用。
        /// ★ v1.1（审查 L19）：旧实现没有清理入口，注释里承诺的「面板已销毁 → null」
        ///   语义不成立，只靠 Unity 的 == 重载兜住。</summary>
        public static void ClearRows()
        {
            RowConstellation = null;
            RowResource = null;
            RowProbability = null;
            RowExtraPerk = null;
            _ovConstellation = _ovResource = _ovProbability = _ovExtraPerk = null;
        }

        /// <summary>
        /// 克隆一行。
        /// ★ 参数只用 IL2CPP 类型 + string + bool + int，避免被 Il2CppInterop 拒注册。
        /// </summary>
        private static StarVaders.OptionToggle CloneRow(GameObject protoGo, Transform parent, int index,
            string label, bool initial)
        {
            try
            {
                var clone = UnityEngine.Object.Instantiate(protoGo, parent);
                if (clone == null) return null;
                clone.name = "DV_ModRow_" + index;
                clone.SetActive(true);

                // 位置：模板下面依次排开（若容器有 LayoutGroup，它会自己接管，这里设了也无害）
                var rt = clone.GetComponent<RectTransform>();
                var prt = protoGo.GetComponent<RectTransform>();
                if (rt != null && prt != null)
                {
                    float step = (prt.sizeDelta.y > 1f ? prt.sizeDelta.y : 64f) + 8f;
                    rt.anchoredPosition = prt.anchoredPosition + new Vector2(0f, -step * index);
                }

                // 文字
                var ov = clone.GetComponent<StarVaders.OptionView>();
                if (ov != null && ov.Description != null) ov.Description.text = label;

                // 开关：设为初始值。
                // ★ v1.1 修复（审查 M23）：Instantiate 会连 Inspector 里序列化的**持久监听器**
                //   一起复制过来 —— 也就是模板行（辅助模式）自己的回调。旧实现只调了
                //   SetIsOnWithoutNotify，没有摘掉这些监听器，注释里说的
                //   「不接管原组件回调」在代码里并没有保证。这里先清空再设值。
                var ot = clone.GetComponent<StarVaders.OptionToggle>();
                if (ot != null && ot.Toggle != null)
                {
                    try { ot.Toggle.onValueChanged.RemoveAllListeners(); }
                    catch (Exception __e) { LogOnce.Warn("CloneRow.RemoveAllListeners", __e); }
                    try { ot.Toggle.SetIsOnWithoutNotify(initial); }
                    catch (Exception __e)
                    {
                        LogOnce.Warn("CloneRow.SetIsOnWithoutNotify", __e);
                        ot.Toggle.isOn = initial;
                    }
                }

                return ot;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo($"NativeSandbox CloneRow({label}) 失败: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 每帧读三行的开关状态，同步进注册表 —— 功能插件据此开/关。
    ///
    /// 为什么不用 Toggle.onValueChanged 回调：
    ///   Il2CppInterop 的 UnityAction&lt;bool&gt; 委托编组有坑（托管委托 → il2cpp 委托），
    ///   轮询更稳且代价可忽略（三个 bool 比较）。
    /// </summary>
    public class ToggleWatcher : MonoBehaviour
    {
        public ToggleWatcher(IntPtr ptr) : base(ptr) { }

        private float _t;

        // ★ v1.1（审查 M21）：记录上一次读到并已同步过的值。
        //   旧实现用「on != 注册表当前值」判是否需要 Set —— 当那一项**没被任何插件注册**时
        //   （例如 ExtraPerk 未安装），Get 恒为 false，于是只要 UI 是开的就永远不等，
        //   每 0.15 秒重设一次、日志无限刷。改成只在 UI 值**真正变化**时才同步。
        private bool _seen1, _seen2, _seen3, _seen4;
        private bool _last1, _last2, _last3, _last4;

        private void Update()
        {
            _t += Time.unscaledDeltaTime;
            if (_t < 0.15f) return;   // 没必要每帧查
            _t = 0f;

            // ★ 先重写文字 —— 游戏在 Awake 之后会再刷一遍本地化，把注入时写的标签覆盖掉。
            //   实测：第一版只写一次，面板上四行全显示模板原文「辅助模式（每回合+1挪移和+1时空点）」。
            try { SandboxRowInjector.ReapplyLabels(); } catch (Exception __e) { LogOnce.Warn("ToggleWatcher.ReapplyLabels", __e); }

            // ★ 四项各自内联展开 —— 不抽成带 Func/Action 参数的辅助方法：
            //   注入类的方法签名里出现托管类型（委托、StringBuilder、List<T>）
            //   会被 Il2CppInterop 拒绝注册。
            try
            {
                var r1 = SandboxRowInjector.RowConstellation;
                if (r1 != null && r1.Toggle != null)
                {
                    bool on = r1.Toggle.isOn;
                    if (!_seen1) { _seen1 = true; _last1 = on; }
                    else if (on != _last1) { _last1 = on; ModToggleRegistry.SetConstellationReroll(on); }
                }
            }
            catch (Exception __e) { LogOnce.Warn("ToggleWatcher.Row1", __e); }

            try
            {
                var r2 = SandboxRowInjector.RowResource;
                if (r2 != null && r2.Toggle != null)
                {
                    bool on = r2.Toggle.isOn;
                    if (!_seen2) { _seen2 = true; _last2 = on; }
                    else if (on != _last2) { _last2 = on; ModToggleRegistry.SetAutoResource(on); }
                }
            }
            catch (Exception __e) { LogOnce.Warn("ToggleWatcher.Row2", __e); }

            try
            {
                var r3 = SandboxRowInjector.RowProbability;
                if (r3 != null && r3.Toggle != null)
                {
                    bool on = r3.Toggle.isOn;
                    if (!_seen3) { _seen3 = true; _last3 = on; }
                    else if (on != _last3) { _last3 = on; ModToggleRegistry.SetProbabilityDisplay(on); }
                }
            }
            catch (Exception __e) { LogOnce.Warn("ToggleWatcher.Row3", __e); }

            try
            {
                var r4 = SandboxRowInjector.RowExtraPerk;
                if (r4 != null && r4.Toggle != null)
                {
                    bool on = r4.Toggle.isOn;
                    if (!_seen4) { _seen4 = true; _last4 = on; }
                    else if (on != _last4) { _last4 = on; ModToggleRegistry.SetExtraPerk(on); }
                }
            }
            catch (Exception __e) { LogOnce.Warn("ToggleWatcher.Row4", __e); }

            // ★ 面板没了就把静态引用清掉（审查 L19）——
            //   否则下一块面板注入时会误判「已经注入过」。
            try
            {
                if (SandboxRowInjector.RowConstellation == null && _seen1)
                {
                    SandboxRowInjector.ClearRows();
                    _seen1 = _seen2 = _seen3 = _seen4 = false;
                    ModKitLog.Info("NativeSandbox: 面板已销毁，行引用已清空");
                }
            }
            catch (Exception __e) { LogOnce.Warn("ToggleWatcher.ClearRows", __e); }
        }
    }
}
