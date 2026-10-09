using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using DiceVaders.ModKit;

namespace DiceVaders.QoL
{
    /// <summary>
    /// DiceVaders 体验改善包。
    ///
    /// ═══ 设计原则 ═══
    ///   · 每个模块独立开关，默认【全部关闭】—— 不替玩家决定难度
    ///   · 只做「让好东西更容易出现」这类正向加成，**不去拆游戏的负面机制**
    ///   · 全部基于 Ghidra 伪代码实证的 hook 点，不猜
    ///
    /// ═══ v1.1.0 变更：移除「难度机制解除」 ═══
    ///   原先有个模块会拦掉 LockedSlots / Scarcity / CursedShop 等 10 个局内修正
    ///   （Harmony Prefix 挂在 EncounterModel.HasRunMod 上，让这些机制压根不激活）。
    ///   用户反馈：**这直接让游戏没有挑战性了** —— 负面效果本来就是游戏设计的一部分，
    ///   整个模块连同 Patch_HasRunMod 一起删除。
    ///
    /// ═══ 现存模块（默认全关，按需开） ═══
    ///   模块 2 掉落品质   —— 提高高稀有度出现概率（倍率可调）
    ///   模块 3 商店品质   —— 提高商店里高稀有度商品的概率
    ///   模块 4 全解锁     —— 所有神器视为已解锁（对应「专属专长见不到」）
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders QoL", "1.1.1")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.qol";

        internal static ManualLogSource Logger;
        internal static Harmony Harmony;

        // ── 模块 2：掉落品质 ──
        internal static ConfigEntry<bool> BoostRarityChance;
        internal static ConfigEntry<float> RarityChanceMul;

        // ── 模块 3：商店品质 ──
        internal static ConfigEntry<bool> BoostShopRarity;
        internal static ConfigEntry<int> ShopRarityBonus;

        // ── 模块 4：解锁 ──
        internal static ConfigEntry<bool> UnlockAllArtifacts;

        public override void Load()
        {
            Logger = base.Log;
            ModKitLog.Sink      = m => Logger.LogInfo(m);
            ModKitLog.WarnSink  = m => Logger.LogWarning(m);
            ModKitLog.ErrorSink = m => Logger.LogError(m);

            // ═══════ 模块 2：掉落品质 ═══════
            BoostRarityChance = Config.Bind("2-掉落品质", "Enable", false,
                "提升高稀有度出现概率（改动 ContentGetter.GetChanceForRarity 的返回值）。");
            RarityChanceMul = Config.Bind("2-掉落品质", "RarityChanceMultiplier", 1.5f,
                new ConfigDescription(
                    "高稀有度【单档权重】的倍率。1.0=原版；1.5=明显更容易出好东西；2.0=上限。\n" +
                    "★ 再高没有意义：各档权重按传奇、稀有、罕见顺序累加；超过 100% 的区间会被截断。",
                    new AcceptableValueRange<float>(1f, 2f)));

            // ═══════ 模块 3：商店品质 ═══════
            BoostShopRarity = Config.Bind("3-商店品质", "Enable", false,
                "提升商店里出现高稀有度商品的概率。");
            ShopRarityBonus = Config.Bind("3-商店品质", "ShopRarityBonus", 1,
                new ConfigDescription(
                    "加到「商店权重基数 w」上的值（不是稀有度等级）。\n" +
                    "★ 量级说明：w = MIN((幕数-1)*5, 20) + 游戏自身加成；" +
                    "w ≤ 30 时传说档恒为 0%，w 超过 30 才会开始出现。\n" +
                    "  所以 +1 在前期几乎看不出效果、越后期越明显。",
                    new AcceptableValueRange<int>(0, 6)));

            // ═══════ 模块 4：解锁 ═══════
            UnlockAllArtifacts = Config.Bind("4-解锁", "UnlockAllArtifacts", false,
                "让所有神器都视为已解锁（对应「神器解锁不完 / 专属专长见不到」）。");

            Logger.LogInfo("===== DiceVaders QoL v1.1.1 =====");

            // ═══════ 应用补丁 ═══════
            // ★ v1.1 修复（审查 M35）：改成**无条件挂载**，由补丁体自己检查 .Value。
            //
            //   旧实现是「按 Load 时的开关状态决定挂不挂」，于是同一项配置两个方向行为不对称：
            //     开 → 关：补丁已挂、体里早退 → 立即生效 ✓
            //     关 → 开：补丁压根没挂 → **改多少次都不会生效**，必须重启游戏 ✗
            //   现在两个方向都立即生效，配置描述里也不用写「需要重启」。
            Harmony = new Harmony(Guid);
            int patched = 0;

            try { Harmony.PatchAll(typeof(Patch_GetChanceForRarity)); patched++; }
            catch (Exception e) { Logger.LogError("  [模块2] 补丁失败: " + e.GetType().Name + ": " + e.Message); }

            try { Harmony.PatchAll(typeof(Patch_GetShopRarityValue)); patched++; }
            catch (Exception e) { Logger.LogError("  [模块3] 补丁失败: " + e.GetType().Name + ": " + e.Message); }

            try { Harmony.PatchAll(typeof(Patch_IsArtifactUnlocked)); patched++; }
            catch (Exception e) { Logger.LogError("  [模块4] 补丁失败: " + e.GetType().Name + ": " + e.Message); }

            Logger.LogInfo($"已挂载 {patched}/3 个补丁" +
                $"（模块2 掉落品质={On(BoostRarityChance)} 模块3 商店品质={On(BoostShopRarity)} 模块4 全解锁={On(UnlockAllArtifacts)}）");
            Logger.LogInfo("★ 三个模块的开关都是热生效的：改 cfg 后立即起作用，不用重启。");
        }

        /// <summary>把 ConfigEntry 读成「开/关」文字（仅用于日志）。</summary>
        private static string On(ConfigEntry<bool> e) => (e != null && e.Value) ? "开" : "关";
    }

    // ═══════════════════════════════════════════════════════
    // 模块 2：掉落品质
    //
    // ContentGetter.GetChanceForRarity(Rarity, EncounterModel)
    //
    // 当前机器码：GetChanceForRarity 返回单档权重，GetRandomRarity 依序累加。
    // 原版保证高 W 时仍给罕见与普通保底；倍率开启后可能消耗这些保底。
    [HarmonyPatch]
    internal static class Patch_GetChanceForRarity
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("StarVaders.ContentGetter");
            if (t == null) { Plugin.Logger?.LogError("  [模块2] 找不到 StarVaders.ContentGetter"); return null; }
            var m = AccessTools.Method(t, "GetChanceForRarity");
            if (m == null) Plugin.Logger?.LogError("  [模块2] 找不到 ContentGetter.GetChanceForRarity");
            return m;
        }

        /// <summary>
        /// ★ 参数类型必须与目标方法完全一致：真实签名是
        ///     ContentGetter.GetChanceForRarity(Rarity rarity, EncounterModel encounterModel)
        ///   写成 int 会导致 Harmony 找不到匹配的重载、patch 静默失败。
        /// </summary>
        [HarmonyPostfix]
        static void Postfix(StarVaders.Rarity rarity, ref float __result)
        {
            try
            {
                if (Plugin.BoostRarityChance == null || !Plugin.BoostRarityChance.Value) return;
                // 只放大高稀有度权重（2=罕见 / 3=稀有 / 4=传说）；
                // 1=普通 是拿 1-c3 算出来的补数，不在本函数里返回，也不会被改到。
                int r = (int)rarity;
                if (r >= 2 && r <= 4)
                {
                    __result *= Plugin.RarityChanceMul.Value;
                    // 单档权重夹到 0~1；调用方负责累加与截断
                    if (__result > 1f) __result = 1f;
                    if (__result < 0f) __result = 0f;
                }
            }
            catch (Exception __e) { LogOnce.Warn("Patch_GetChanceForRarity.Postfix", __e); }
        }
    }

    // ═══════════════════════════════════════════════════════
    // 模块 3：商店品质
    //
    // 当前 GetRandomShopEntity / GetChanceForRarity 直接读 GetIntValue(ShopRarity)。
    // 在实际输入 getter 加一次 Bonus；不改保存值，不再同时修改聚合 W getter。
    [HarmonyPatch]
    internal static class Patch_GetShopRarityValue
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("StarVaders.EncounterModel");
            if (t == null) { Plugin.Logger?.LogError("  [模块3] 找不到 StarVaders.EncounterModel"); return null; }
            var m = AccessTools.Method(t, "GetIntValue", new[] { typeof(EncounterValue) });
            if (m == null) Plugin.Logger?.LogError("  [模块3] 找不到 EncounterModel.GetIntValue(ShopRarity)");
            return m;
        }

        [HarmonyPostfix]
        static void Postfix(EncounterValue __0, ref int __result)
        {
            try
            {
                if (Plugin.BoostShopRarity == null || !Plugin.BoostShopRarity.Value) return;
                if (__0 != EncounterValue.ShopRarity) return;
                long value = (long)__result + (Plugin.ShopRarityBonus?.Value ?? 0);
                __result = (int)Math.Min(int.MaxValue, Math.Max(int.MinValue, value));
            }
            catch (Exception __e) { LogOnce.Warn("Patch_GetShopRarityValue.Postfix", __e); }
        }
    }

    // ═══════════════════════════════════════════════════════
    // 模块 4：神器全解锁
    //
    // ContentGetter.IsArtifactUnlocked(ArtifactName)
    //   拦法：Prefix 直接返回 true。
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_IsArtifactUnlocked
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("StarVaders.ContentGetter");
            if (t == null) { Plugin.Logger?.LogError("  [模块4] 找不到 StarVaders.ContentGetter"); return null; }
            var m = AccessTools.Method(t, "IsArtifactUnlocked");
            if (m == null) Plugin.Logger?.LogError("  [模块4] 找不到 ContentGetter.IsArtifactUnlocked");
            return m;
        }

        [HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            try
            {
                if (Plugin.UnlockAllArtifacts != null && Plugin.UnlockAllArtifacts.Value)
                {
                    __result = true;
                    return false;
                }
            }
            catch (Exception __e) { LogOnce.Warn("Patch_IsArtifactUnlocked.Prefix", __e); }
            return true;
        }
    }
}
