using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using DiceVaders.ModKit;
using UnityEngine;

namespace DiceVaders.ResourceButtons
{
    /// <summary>
    /// 无限时空点 / 挪移。
    ///
    /// ═══ 行为（v2.0）═══
    /// 每次数量一减少，就自动回到目标值（默认 10）—— 也就是「用不完」。
    /// 不做回合检测、不做时间窗口、不做稳定判定：
    ///   前两版试过「回合开始补满」和「钉 N 秒」，都被游戏自身的重置/消费节奏打乱
    ///   （用户实测：用掉后补回 10 又立刻被消耗 1 变 9）。
    ///   用户最终要求的就是最简单直接的「减少即补回」。
    ///
    /// ═══ 写入路径 ═══
    /// EncounterModel.SetValue(EncounterValue, Il2CppSystem.Object)
    ///   EncounterValue.ChronoToken = 3   （时空点）
    ///   EncounterValue.BudgeToken  = 2   （挪移）
    ///   EncounterController.EncounterModel 在 +0xC8
    ///
    /// ★ Il2CppSystem.Object 自带 int 隐式转换运算符（见 Il2Cppmscorlib），直接传 int 即可。
    /// ★ 注入类的方法签名里不能出现托管类型（StringBuilder / List&lt;T&gt; / 委托），
    ///   会被 Il2CppInterop 拒绝注册。
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders Infinite Resources", "2.0.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.resourcebuttons";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> GiveChrono;
        internal static ConfigEntry<bool> GiveBudge;
        internal static ConfigEntry<int> ChronoAmount;
        internal static ConfigEntry<int> BudgeAmount;
        internal static ConfigEntry<bool> CapAbove;
        internal static ConfigEntry<float> PollSeconds;
        internal static ConfigEntry<bool> LogOnRefill;

        public override void Load()
        {
            Logger = Log;
            ModKitLog.Sink      = m => Logger.LogInfo(m);
            ModKitLog.WarnSink  = m => Logger.LogWarning(m);
            ModKitLog.ErrorSink = m => Logger.LogError(m);

            Enabled = Config.Bind("1-开关", "Enabled", true,
                "开启无限资源。也可以在游戏的「沙盒设置」面板里切换。");
            GiveChrono = Config.Bind("1-开关", "GiveChrono", true,
                "无限时空点（ChronoToken）。");
            GiveBudge = Config.Bind("1-开关", "GiveBudge", true,
                "无限挪移（BudgeToken）。");

            ChronoAmount = Config.Bind("2-数量", "ChronoAmount", 10,
                new ConfigDescription("时空点维持的目标值。",
                    new AcceptableValueRange<int>(1, 999)));
            BudgeAmount = Config.Bind("2-数量", "BudgeAmount", 10,
                new ConfigDescription("挪移维持的目标值。",
                    new AcceptableValueRange<int>(1, 999)));

            CapAbove = Config.Bind("2-数量", "CapAbove", true,
                "目标值以上也压回目标（默认开）。\n" +
                "关掉的话只在「减少到低于目标」时补，已经超过目标时不动它。");

            PollSeconds = Config.Bind("3-调试", "PollSeconds", 0.05f,
                new ConfigDescription(
                    "轮询间隔（秒）。越小反应越快，越大越省。\n" +
                    "默认 0.05 = 每秒查 20 次，花掉的资源几乎瞬间回满。",
                    new AcceptableValueRange<float>(0f, 1f)));

            LogOnRefill = Config.Bind("3-调试", "LogOnRefill", true,
                "每次补回写一行日志。默认开 —— 出问题时读日志就能定位。");

            Logger.LogInfo("===== DiceVaders InfiniteResources v2.0.0 =====");

            ModToggleRegistry.AutoResource = Enabled;
            ModToggleRegistry.AutoResourceChrono = GiveChrono;
            ModToggleRegistry.AutoResourceBudge = GiveBudge;
            ModToggleRegistry.ChronoAmount = ChronoAmount;
            ModToggleRegistry.BudgeAmount = BudgeAmount;
            ModToggleRegistry.Log = m => Logger.LogInfo(m);

            ClassInjector.RegisterTypeInIl2Cpp<ResourceKeeper>();
            var go = new GameObject("DiceVaders_InfiniteResources");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<ResourceKeeper>();
        }
    }

    public class ResourceKeeper : MonoBehaviour
    {
        public ResourceKeeper(IntPtr ptr) : base(ptr) { }

        private float _nextPoll;
        private string _lastState = "";

        private void Update()
        {
            if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextPoll) return;
            _nextPoll = now + (Plugin.PollSeconds != null ? Plugin.PollSeconds.Value : 0.05f);

            // ★ 换局收尾（查找缓存失效 + LogOnce 去重表复位）—— 统一走 ModKit，
            //   不在这里自己维护指针字段。Idempotent，每轮调一次即可。
            Il2CppHelpers.PollRunChange();

            var ec = Il2CppHelpers.FindCached<StarVaders.EncounterController>(0.5f);
            if (ec == null) { _lastState = ""; return; }

            var em = Il2CppHelpers.Safe(() => ec.EncounterModel);
            if (em == null) return;

            bool chronoOk = true, budgeOk = true;

            if (Plugin.GiveChrono != null && Plugin.GiveChrono.Value)
                chronoOk = Keep(em, EncounterValue.ChronoToken, Plugin.ChronoAmount.Value, "时空点");

            if (Plugin.GiveBudge != null && Plugin.GiveBudge.Value)
                budgeOk = Keep(em, EncounterValue.BudgeToken, Plugin.BudgeAmount.Value, "挪移");

            // 取不到值时打一次状态，避免刷屏
            if (!chronoOk || !budgeOk)
            {
                string st = (chronoOk ? "ok" : "fail") + "/" + (budgeOk ? "ok" : "fail");
                if (st != _lastState)
                {
                    _lastState = st;
                    Plugin.Logger?.LogInfo($"InfiniteResources: 写入状态 {st}");
                }
            }
        }

        /// <summary>
        /// 维持一个资源值在目标上。
        /// 返回 true = 值已等于目标（或本次已成功写回）。
        ///
        /// ★ 方法只接 IL2CPP 类型 + string + int —— 注入类的方法签名里不能出现托管类型。
        /// </summary>
        private bool Keep(StarVaders.EncounterModel em, EncounterValue key, int target, string label)
        {
            try
            {
                int cur = em.GetIntValue(key);

                bool needFill = Plugin.CapAbove == null || Plugin.CapAbove.Value
                    ? cur != target          // 高于目标也压回
                    : cur < target;          // 只在低于目标时补

                if (!needFill) return true;

                em.SetValue(key, target);
                int after = em.GetIntValue(key);

                if (Plugin.LogOnRefill != null && Plugin.LogOnRefill.Value)
                    Plugin.Logger?.LogInfo($"InfiniteResources: {label} {cur} -> {after}（目标 {target}）");

                return after == target;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo($"InfiniteResources {label} 失败: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }
    }
}
