using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using UnityEngine;

namespace DiceVaders.ShopInfo
{
    /// <summary>
    /// 商店出货概率实时面板。
    ///
    /// 为什么做这个：游戏里「商店稀有度」只是一个数字，玩家看不出它到底意味着什么。
    /// 实测（Ghidra 伪代码 + 从 GameAssembly.dll 读出的浮点常量）证明它确实在驱动
    /// 各档位物品的出现概率，只是游戏没把它换算出来给玩家看。
    ///
    /// ═══ 概率公式（全部实证，非推测）═══
    ///
    /// EncounterModel.GetCurrentShopRarity()  (RVA 0x1D31110)：
    ///     w = MIN((ActNumber - 1) * 5, 20) + GetIntValue(ShopRarity)
    ///                                        ↑ EncounterValue 18      ↑ EncounterValue 11
    ///   这个 w 就是「权重基数」。
    ///
    /// ContentGetter.GetRandomShopEntity()  (RVA 0x1CFC7F0)：
    ///     fLegendary = w * 0.001 - 0.03      ← 常量取自 VA 0x183A24B2C / 0x183A24BA0
    ///     fRare      = w * 0.002 + 0.03      ← VA 0x183A24B30 / 0x183A24BA0
    ///     fUncommon  = w * 0.015 + 0.1       ← VA 0x183A24B6C / 0x183A24C00
    ///     r = Random.NextDouble();
    ///     r < fLegendary                  → 传说
    ///     r < fLegendary + fRare          → 稀有
    ///     r < fLegendary + fRare + fUncommon → 罕见
    ///     else                            → 普通
    ///
    /// ContentGetter.GetChanceForRarity()  (RVA 0x1CFC450) —— 神器用同一组曲线，
    /// 但「普通」档是补数：0.91 - (其余三档之和)，上限常量取自 VA 0x183A24E00 = 0.91。
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders ShopInfo", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.shopinfo";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<bool> ShowPanel;
        internal static ConfigEntry<float> OffsetX;
        internal static ConfigEntry<float> RightOffsetX;
        internal static ConfigEntry<float> OffsetY;
        internal static ConfigEntry<float> FontSize;
        internal static ConfigEntry<bool> ShowArtifactProbs;
        internal static ConfigEntry<bool> ShowPoolSummary;
        internal static ConfigEntry<bool> ShowHiddenValues;
        internal static ConfigEntry<bool> ShowAstral;
        internal static ConfigEntry<float> ExtraOffsetY;
        internal static ConfigEntry<bool> LogOnRarityChange;

        public override void Load()
        {
            Logger = base.Log;
            ModKitLog.Sink      = m => Logger.LogInfo(m);
            ModKitLog.WarnSink  = m => Logger.LogWarning(m);
            ModKitLog.ErrorSink = m => Logger.LogError(m);

            ShowPanel = Config.Bind("1-显示", "ShowPanel", true, "显示商店出货概率面板。");
            OffsetX = Config.Bind("1-显示", "OffsetX", 520f,
                new ConfigDescription("左列距屏幕左边缘像素。", new AcceptableValueRange<float>(0f, 900f)));
            RightOffsetX = Config.Bind("1-显示", "RightOffsetX", 560f,
                new ConfigDescription("右列距屏幕右边缘像素（默认 560，落在「发射！」按钮左边）。",
                    new AcceptableValueRange<float>(0f, 900f)));
            OffsetY = Config.Bind("1-显示", "OffsetY", 280f,
                new ConfigDescription("两列距屏幕底部像素。", new AcceptableValueRange<float>(0f, 900f)));
            FontSize = Config.Bind("1-显示", "FontSize", 15f,
                new ConfigDescription("字号。", new AcceptableValueRange<float>(8f, 40f)));
            ShowArtifactProbs = Config.Bind("1-显示", "ShowArtifactProbs", true,
                "在右侧显示神器物品的稀有度概率（与左侧商品棋子共用同一套曲线）。\n" +
                "★ 关掉只影响「神器物品」这一块；下面的「星界」行是独立开关，仍会显示。");
            ShowPoolSummary = Config.Bind("1-显示", "ShowPoolSummary", false,
                "【未实现 · 保留项】本局神器池统计。当前代码没有消费这个开关，改它不会有任何效果。");

            // ── 隐藏数值：游戏从不在界面上显示、但实际影响战斗的那些 EncounterValue ══
            // 对应差评里骂的 BOSS 机制（兽化 / 怒气 / 蜂群倍率 / 血祭 等）。
            ShowHiddenValues = Config.Bind("1-显示", "ShowHiddenValues", false,
                "显示隐藏的对局数值（全局倍率 / 怒气 / 兽化回合 / 蜂群倍率 / 献祭% / 增益% / 最终BOSS血量 / 跳过量）。\n" +
                "★ 只在数值非零时显示，全为零时这一块会整体隐藏，不占地方。");
            ExtraOffsetY = Config.Bind("1-显示", "HiddenValuesOffsetY", 420f,
                new ConfigDescription(
                    "隐藏数值块距屏幕底部像素。\n" +
                    "★ 它是单独一块、pivot 同为 (0,0) 也是向上生长，所以必须**比 OffsetY 大足够多**，" +
                    "否则会和左列概率块叠字（旧默认 300 / OffsetY 280 就是叠的）。",
                    new AcceptableValueRange<float>(0f, 1200f)));
            ShowAstral = Config.Bind("1-显示", "ShowAstral", true,
                "在右列底部显示「星界」出现概率。\n" +
                "★ 星界是独立的一次掷骰（在选秀格上判定），不占四档概率之和，所以单独列出。\n" +
                "   依据：DraftChoice.Initialize 的 astralChance 参数，DraftPanel.CreateNewDraft 传常量 0.02。");
            LogOnRarityChange = Config.Bind("2-调试", "LogOnRarityChange", true,
                "权重基数变化时在日志里打印一行，方便对照游戏内数值。");

            ModToggleRegistry.ProbabilityDisplay = ShowPanel;
            ModToggleRegistry.Log = m => Logger.LogInfo(m);

            Logger.LogInfo("===== DiceVaders ShopInfo v1.0.0 =====");

            ClassInjector.RegisterTypeInIl2Cpp<InfoPanel>();
            var go = new GameObject("DiceVaders_ShopInfo");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<InfoPanel>();
        }
    }

    public class InfoPanel : MonoBehaviour
    {
        public InfoPanel(IntPtr ptr) : base(ptr) { }

        // ═══ 概率常量（从 GameAssembly.dll 实测读出）═══
        private const float K_LEGENDARY = 0.001f;   // VA 0x183A24B2C
        private const float K_RARE = 0.002f;        // VA 0x183A24B30
        private const float K_UNCOMMON = 0.015f;    // VA 0x183A24B6C
        private const float C_A = 0.03f;            // VA 0x183A24BA0
        private const float C_B = 0.1f;             // VA 0x183A24C00

        /// <summary>
        /// 星界（Astral）出现概率 —— 独立于上面四档的一次掷骰。
        ///
        /// 实证：DraftPanel.CreateNewDraft 调用 DraftChoice.Initialize 时传常量 0.02；
        ///       DraftChoice 里做 Random.NextDouble() &lt; astralChance 判定，命中后
        ///       把稀有度写成 11（Rarity.Astral）。难度为 Tutorial(7) 时永不触发。
        /// </summary>
        private const float ASTRAL_CHANCE = 0.02f;

        private GameObject _canvas;
        private TMPro.TextMeshProUGUI _textLeft;    // 左下：商品棋子概率
        private TMPro.TextMeshProUGUI _textRight;   // 右下：神器物品概率
        private TMPro.TextMeshProUGUI _textExtra;   // 隐藏数值（只在非零时显示）
        private float _nextRefresh;
        private int _lastW = int.MinValue;

        // ★ v1.1：布局参数与内容签名的「上次已应用值」缓存 ——
        //   避免每帧写 anchoredPosition（标脏布局）与每 0.2 秒重建富文本。
        private float _apX = float.NaN, _apY = float.NaN, _apRX = float.NaN;
        private float _apEY = float.NaN, _apFS = float.NaN;
        private string _lastSig;

        private void Start() { BuildUI(); }

        /// <summary>
        /// 建两个竖排小文本框，分别贴屏幕左边缘与右边缘（垂直居中）。
        /// 左：商品棋子各稀有度概率；右：神器各稀有度概率。
        /// </summary>
        private void BuildUI()
        {
            try
            {
                _canvas = UiInjector.GetOverlayCanvas("DiceVaders_ShopInfoCanvas", 31000);
                if (_canvas == null) { Plugin.Logger?.LogInfo("ShopInfo: Canvas 创建失败"); return; }

                _textLeft = MakeText("InfoLeft", anchoredLeft: true);
                _textRight = MakeText("InfoRight", anchoredLeft: false);
                _textExtra = MakeText("InfoExtra", anchoredLeft: true);

                Plugin.Logger?.LogInfo("ShopInfo: 左右竖排文本已创建");
            }
            catch (Exception e) { Plugin.Logger?.LogInfo("ShopInfo BuildUI 失败: " + e.Message); }
        }

        /// <summary>
        /// 建一个竖排文本框。
        /// 左列锚在屏幕【左下角】、右列锚在【右下角】，pivot 同为 (x,0) —— 文本各自向上生长。
        /// OffsetX 控制左列距左边缘，RightOffsetX 控制右列距右边缘；OffsetY 是两列距底部的共同高度。
        /// </summary>
        private TMPro.TextMeshProUGUI MakeText(string name, bool anchoredLeft)
        {
            var go = new GameObject(name);
            go.layer = 5;
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            float ax = anchoredLeft ? 0f : 1f;
            rt.anchorMin = new Vector2(ax, 0f);
            rt.anchorMax = new Vector2(ax, 0f);
            rt.pivot = new Vector2(ax, 0f);
            rt.sizeDelta = new Vector2(240f, 200f);

            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            float fs = 15f;
            var font = UiInjector.FindAnyFont(out fs);
            if (font != null) t.font = font;
            t.fontSize = Plugin.FontSize.Value;
            t.color = new Color(0.88f, 0.94f, 1f, 0.92f);
            t.alignment = anchoredLeft ? TMPro.TextAlignmentOptions.Left : TMPro.TextAlignmentOptions.Right;
            t.richText = true;
            try { t.enableAutoSizing = false; } catch (Exception __e) { LogOnce.Warn("InfoPanel.MakeText:183", __e); }
            t.text = "";
            return t;
        }

        private void Update()
        {
            try
            {
                // ★ 换局收尾（查找缓存失效 + LogOnce 去重表复位）—— 统一走 ModKit。
                Il2CppHelpers.PollRunChange();

                if (Plugin.ShowPanel != null && !Plugin.ShowPanel.Value)
                {
                    ClearAll();
                    return;
                }

                // ── 布局：只在参数真的变化时才写 ──
                // ★ v1.1 修复（审查 L6）：旧实现每帧无条件写三个 anchoredPosition
                //   （每帧标脏布局）并每帧读 9 次 ConfigEntry。参数不变就不该动。
                float ox = Plugin.OffsetX?.Value ?? 520f;
                float oy = Plugin.OffsetY?.Value ?? 280f;
                float rx = Plugin.RightOffsetX?.Value ?? 560f;
                float ey = Plugin.ExtraOffsetY?.Value ?? 420f;
                float fs = Plugin.FontSize?.Value ?? 15f;

                if (ox != _apX || oy != _apY || rx != _apRX || ey != _apEY || fs != _apFS)
                {
                    _apX = ox; _apY = oy; _apRX = rx; _apEY = ey; _apFS = fs;
                    ApplyLayout(ox, oy, rx, ey, fs);
                }

                // 每 0.2 秒刷新一次即可（数值不会每帧变）
                if (Time.realtimeSinceStartup < _nextRefresh) return;
                _nextRefresh = Time.realtimeSinceStartup + 0.2f;
                Refresh();
            }
            catch (Exception __e) { LogOnce.Warn("InfoPanel.Update", __e); }
        }

        /// <summary>把三个文本框摆到配置指定的位置与字号。</summary>
        private void ApplyLayout(float ox, float oy, float rx, float ey, float fs)
        {
            try
            {
                if (_textLeft != null)
                {
                    var rt = _textLeft.rectTransform;
                    if (rt != null) rt.anchoredPosition = new Vector2(ox, oy);
                    if (Math.Abs(_textLeft.fontSize - fs) > 0.01f) _textLeft.fontSize = fs;
                }
                if (_textRight != null)
                {
                    var rt = _textRight.rectTransform;
                    if (rt != null) rt.anchoredPosition = new Vector2(-rx, oy);
                    if (Math.Abs(_textRight.fontSize - fs) > 0.01f) _textRight.fontSize = fs;
                }
                if (_textExtra != null)
                {
                    var rt = _textExtra.rectTransform;
                    if (rt != null) rt.anchoredPosition = new Vector2(ox, ey);
                    if (Math.Abs(_textExtra.fontSize - fs) > 0.01f) _textExtra.fontSize = fs;
                }
            }
            catch (Exception __e) { LogOnce.Warn("InfoPanel.ApplyLayout", __e); }
        }

        private void Refresh()
        {
            // ★ 星座界面有自己的按钮（刷新 / 刷新全部），概率列会和它们重叠 —— 那里整块隐藏。
            //   实测截图：右列正好压在「刷新」按钮上。
            bool constellationShowing = false;
            try { constellationShowing = StarVaders.ConstellationController.IsShowing; }
            catch (Exception __e) { LogOnce.Warn("InfoPanel.IsShowing", __e); }
            if (constellationShowing) { ClearAll(); return; }

            var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
            if (ec == null) { ClearAll(); return; }

            StarVaders.EncounterModel em = null;
            try { em = ec.EncounterModel; } catch (Exception __e) { LogOnce.Warn("InfoPanel.EncounterModel", __e); }
            if (em == null) { ClearAll(); return; }

            // ★ v1.1 修复（审查 M10）：取不到权重基数时**不要拿 0 继续算**。
            //   旧实现 w 保持 0 照常渲染，会画出一组格式合法但完全错误的概率。
            int w = 0;
            try { w = em.GetCurrentShopRarity(); }
            catch (Exception __e)
            {
                LogOnce.Warn("InfoPanel.GetCurrentShopRarity", __e);
                ClearAll();
                return;
            }

            bool showArtifact = Plugin.ShowArtifactProbs?.Value ?? true;
            bool showAstral = Plugin.ShowAstral?.Value ?? true;
            bool showHidden = Plugin.ShowHiddenValues?.Value ?? false;

            // ★ v1.1 修复（审查 L7）：内容没变就别重建富文本。
            //   SetText 内部虽然会拦掉真正相同的字符串，但富文本拼接本身每 0.2 秒
            //   都在分配字符串。隐藏数值块打开时它的内容可能随时变，所以那种情况不跳过。
            string sig = $"{w}|{showArtifact}|{showAstral}|{showHidden}|{constellationShowing}";
            if (sig == _lastSig && !showHidden) return;
            _lastSig = sig;

            if (w != _lastW)
            {
                _lastW = w;
                if (Plugin.LogOnRarityChange != null && Plugin.LogOnRarityChange.Value)
                    Plugin.Logger?.LogInfo($"[ShopInfo] 权重基数 W 变为 {w}");
            }

            // ── 概率：按【累积分布】算（游戏是 r<c1→传说; r<c2→稀有; r<c3→罕见; else→普通）──
            float f16 = w * K_LEGENDARY - C_A;
            float f15 = w * K_RARE + C_A;
            float f5 = w * K_UNCOMMON + C_B;

            float c1 = Clamp01(f16);
            float c2 = Clamp01(f16 + f15);
            float c3 = Clamp01(f16 + f15 + f5);
            if (c2 < c1) c2 = c1;
            if (c3 < c2) c3 = c2;

            float pLeg = c1;
            float pRare = c2 - c1;
            float pUnc = c3 - c2;
            float pCom = 1f - c3;   // ★ 已验证：W=635 → 60.5/39.5/0/0，与游戏内实测一致

            // 概率四行（左右共用同一套曲线与配色 —— 配色取自 ContentGetter.GetRarityHex）
            string Rows() =>
                $"<color=#E58D07>传说</color> {pLeg * 100f:0.0}%\n" +
                $"<color=#CA47BA>稀有</color> {pRare * 100f:0.0}%\n" +
                $"<color=#1ACB68>罕见</color> {pUnc * 100f:0.0}%\n" +
                $"<color=#767D5A>普通</color> {pCom * 100f:0.0}%";

            // 星界：独立掷骰，不占上面四档之和
            string astralLine = showAstral
                ? $"\n<color=#C01E20>星界</color> {ASTRAL_CHANCE * 100f:0.0}%"
                : "";

            // ── 左：商品棋子 ──
            string leftText = "<size=85%><color=#9FB4C7>商品棋子</color></size>\n" + Rows() + astralLine;

            // ── 右：神器物品 ──
            // ★ v1.1 修复（审查 M9）：关掉 ShowArtifactProbs 时**不再把整列抹空** ——
            //   旧实现连带把星界行也一起抹掉了，而星界是独立开关控制的。
            string rightText;
            if (showArtifact)
                rightText = "<size=85%><color=#9FB4C7>神器物品</color></size>\n" + Rows() + astralLine;
            else
                rightText = astralLine.TrimStart('\n');

            SetText(_textLeft, leftText);
            SetText(_textRight, rightText);

            // ── 隐藏数值块（只在非零时才有内容）──
            SetText(_textExtra, showHidden ? BuildHiddenValues(em) : "");
        }

        private void ClearAll()
        {
            SetText(_textLeft, "");
            SetText(_textRight, "");
            SetText(_textExtra, "");
        }

        /// <summary>
        /// 隐藏数值 —— 游戏界面从不显示、但实际参与战斗计算的那些 EncounterValue。
        ///
        /// 对应差评里被骂得最凶的 BOSS 机制（兽化 / 怒气 / 蜂群倍率 / 血祭）。
        /// 数据来源：EncounterModel.GetIntValue(EncounterValue)  RVA 0x1D2FA40
        ///
        /// ★ 只在数值非零时才拼进字符串 —— 平时这一块完全空白，不占视觉空间。
        /// ★ 方法只接 IL2CPP 类型（EncounterModel），返回 string，避免 Il2CppInterop 拒注册。
        /// </summary>
        private string BuildHiddenValues(StarVaders.EncounterModel em)
        {
            string body =
                HiddenLine(em, EncounterValue.GlobalMult, "全局倍率", "#FFB800") +
                HiddenLine(em, EncounterValue.HiveMindMult, "蜂群倍率", "#C77DFF") +
                HiddenLine(em, EncounterValue.Rage, "怒气", "#FF7043") +
                HiddenLine(em, EncounterValue.BeastTurn, "兽化回合", "#FF7043") +
                HiddenLine(em, EncounterValue.SacrificePercent, "献祭", "#EF5350") +
                HiddenLine(em, EncounterValue.BoostPercent, "增益", "#66BB6A") +
                HiddenLine(em, EncounterValue.CurrentSkipAmount, "跳过量", "#9FB4C7") +
                HiddenLine(em, EncounterValue.FinalBossHP, "BOSS血量", "#EF5350");

            if (body.Length == 0) return "";
            return "<size=85%><color=#7E8C99>隐藏数值</color></size>\n" + body;
        }

        /// <summary>
        /// 取一个 EncounterValue，非零则返回一行文本，否则返回空串。
        ///
        /// ★ 方法签名只出现 IL2CPP 类型（EncounterModel / EncounterValue）与 string。
        ///   早先的版本用 StringBuilder 当参数，被 Il2CppInterop 拒绝注册：
        ///   "has unsupported parameter System.Text.StringBuilder" —— 换成返回 string 就好了。
        ///
        /// ★ EncounterValue 在【全局命名空间】（不在 StarVaders 下）。
        /// ★ 用 EncounterValueTypeConfig.IsInt() 先判类型 —— 这些值里既有 int 也有 BigDouble 存法，
        ///   对 BigDouble 存法调用 GetIntValue 会抛异常。
        /// </summary>
        private string HiddenLine(StarVaders.EncounterModel em, EncounterValue key, string label, string color)
        {
            try
            {
                if (!EncounterValueTypeConfig.IsInt(key)) return "";
                int v = em.GetIntValue(key);
                if (v == 0) return "";
                return $"<color={color}>{label}</color> {v}\n";
            }
            catch { return ""; }
        }


        // ★ 用两个无参/单 string 参数的小方法分别写左右文本：
        //   把 TextMeshProUGUI 当参数传同样有被 Il2CppInterop 拒注册的风险。
        private void SetText(TMPro.TextMeshProUGUI t, string s)
        {
            try
            {
                if (t == null) return;
                if (t.text != s) { t.text = s; try { t.ForceMeshUpdate(); } catch (Exception __e) { LogOnce.Warn("InfoPanel.SetText.ForceMeshUpdate", __e); } }
            }
            catch (Exception __e) { LogOnce.Warn("InfoPanel.SetText", __e); }
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
