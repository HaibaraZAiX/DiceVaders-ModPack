using System;
using BepInEx.Configuration;

namespace DiceVaders.ModKit
{
    /// <summary>
    /// 跨插件的功能开关注册表。
    ///
    /// 为什么需要它：
    ///   游戏原生的「沙盒设置」面板里要新增三行开关，但这三行控制的功能分别属于
    ///   三个不同的插件（星座刷新 / 资源补充 / 概率显示）。设置面板的注入代码
    ///   不该直接依赖那三个插件，所以这里放一层间接层：
    ///
    ///     功能插件 Load() 时把自己的 ConfigEntry 挂进来；
    ///     NativeSandbox 注入的三行开关读写这些 ConfigEntry。
    ///
    /// ★ 所有字段可能是 null（对应插件没装 / 还没加载完），调用方必须判空。
    /// </summary>
    public static class ModToggleRegistry
    {
        /// <summary>星座刷新开关（由 ConstellationTool 注册）。</summary>
        public static ConfigEntry<bool> ConstellationReroll;

        /// <summary>每回合自动补充时空点/挪移（由 ResourceButtons 注册）。</summary>
        public static ConfigEntry<bool> AutoResource;
        public static ConfigEntry<bool> AutoResourceChrono;
        public static ConfigEntry<bool> AutoResourceBudge;
        public static ConfigEntry<int> ChronoAmount;
        public static ConfigEntry<int> BudgeAmount;

        /// <summary>稀有度概率显示（由 ShopInfo 注册）。</summary>
        public static ConfigEntry<bool> ProbabilityDisplay;

        /// <summary>开局额外专长（由 ExtraPerk 注册）。</summary>
        public static ConfigEntry<bool> ExtraPerk;

        /// <summary>改变开关后同步一次日志。</summary>
        public static Action<string> Log;

        private static void Say(string msg) { try { Log?.Invoke(msg); } catch (Exception __e) { LogOnce.Warn("ModToggleRegistry.Say:40", __e); } }

        // ── 三个开关的统一读写（供设置面板注入的行调用）──

        public static bool GetConstellationReroll()
        {
            return ConstellationReroll != null && ConstellationReroll.Value;
        }

        public static void SetConstellationReroll(bool v)
        {
            if (ConstellationReroll == null) { Say("ModToggles: 星座刷新未注册"); return; }
            ConstellationReroll.Value = v;
            Say($"ModToggles: 星座刷新 -> {v}");
        }

        public static bool GetAutoResource()
        {
            return AutoResource != null && AutoResource.Value;
        }

        public static void SetAutoResource(bool v)
        {
            if (AutoResource == null) { Say("ModToggles: 资源补充未注册"); return; }
            AutoResource.Value = v;
            Say($"ModToggles: 资源补充 -> {v}");
        }

        public static bool GetProbabilityDisplay()
        {
            return ProbabilityDisplay != null && ProbabilityDisplay.Value;
        }

        public static void SetProbabilityDisplay(bool v)
        {
            if (ProbabilityDisplay == null) { Say("ModToggles: 概率显示未注册"); return; }
            ProbabilityDisplay.Value = v;
            Say($"ModToggles: 概率显示 -> {v}");
        }

        public static bool GetExtraPerk()
        {
            return ExtraPerk != null && ExtraPerk.Value;
        }

        public static void SetExtraPerk(bool v)
        {
            if (ExtraPerk == null) { Say("ModToggles: 额外专长未注册"); return; }
            ExtraPerk.Value = v;
            Say($"ModToggles: 额外专长 -> {v}");
        }
    }
}
