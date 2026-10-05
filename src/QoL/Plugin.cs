using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace DiceVaders.QoL
{
    /// <summary>
    /// DiceVaders 体验改善包 —— 针对 Steam 差评里最集中的痛点做的可开关模块。
    ///
    /// 设计原则：
    ///   · 每个模块独立开关，默认只开最保守的几项，不替玩家决定难度
    ///   · 只拦「明确恶心人」的机制，不碰数值平衡的核心
    ///   · 全部基于 Ghidra 伪代码实证的 hook 点，不猜
    ///
    /// 差评 → 模块对应：
    ///   "直接 ban 2 个神器位"        → 模块 1（解除 LockedSlots）
    ///   "资源非常吃紧"               → 模块 1（解除 Scarcity）
    ///   "商店充斥滥竽充数"           → 模块 1 + 3
    ///   "关键遗物获取非常看运气"      → 模块 2（掉落品质）
    ///   "10 小时没见过专属专长"      → 模块 4（解锁）
    /// </summary>
    [BepInPlugin(Guid, "DiceVaders QoL", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "dicevaders.qol";

        internal static ManualLogSource Logger;
        internal static Harmony Harmony;

        // ── 模块 1：难度机制解除 ──
        internal static ConfigEntry<bool> DisableBadRunMods;
        internal static ConfigEntry<string> DisabledRunModNames;

        // ── 模块 2：掉落品质 ──
        internal static ConfigEntry<bool> BoostRarityChance;
        internal static ConfigEntry<float> RarityChanceMul;

        // ── 模块 3：商店品质 ──
        internal static ConfigEntry<bool> BoostShopRarity;
        internal static ConfigEntry<int> ShopRarityBonus;

        // ── 模块 4：解锁 ──
        internal static ConfigEntry<bool> UnlockAllArtifacts;

        /// <summary>要拦掉的 RunMod 名字集合（从配置解析）。</summary>
        internal static HashSet<string> DisabledRunMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public override void Load()
        {
            Logger = base.Log;

            // ═══════ 模块 1：难度机制解除 ═══════
            DisableBadRunMods = Config.Bind("1-难度机制解除", "Enable", true,
                "拦掉下面列出的「局内修正」（RunMod）。这些是本作差评最集中的机制，例如 ban 神器位、资源稀缺、商店塞诅咒。");
            DisabledRunModNames = Config.Bind("1-难度机制解除", "DisabledRunMods",
                "LockedSlots,Scarcity,CursedShop,BannedMiniDice,BannedShield,BannedBudge,BannedChrono,BannedShopToken,Heartless,Jammed",
                "要拦掉的 RunMod 名称，逗号分隔。可用的名字见下方说明。\n" +
                "恶心机制类：LockedSlots(ban神器位) Scarcity(资源稀缺) CursedShop(商店塞诅咒) Heartless Jammed PermaCursed\n" +
                "禁用类：BannedMiniDice BannedShield BannedBudge BannedChrono BannedShopToken\n" +
                "变形类：WarpedOnlyMod CrampedGridMod LightspeedMod AutorollerMod PowerlessMod CataclysmMod BlackCatMod AstralOnlyMod\n" +
                "留空 = 不拦任何机制。");

            // ═══════ 模块 2：掉落品质 ═══════
            BoostRarityChance = Config.Bind("2-掉落品质", "Enable", false,
                "提升高稀有度出现概率（改动 ContentGetter.GetChanceForRarity 的返回值）。");
            RarityChanceMul = Config.Bind("2-掉落品质", "RarityChanceMultiplier", 1.5f,
                new ConfigDescription("稀有度概率倍率。1.0=原版；1.5=明显更容易出好东西；3.0=很夸张。",
                    new AcceptableValueRange<float>(1f, 5f)));

            // ═══════ 模块 3：商店品质 ═══════
            BoostShopRarity = Config.Bind("3-商店品质", "Enable", false,
                "提升商店里出现高稀有度商品的概率。");
            ShopRarityBonus = Config.Bind("3-商店品质", "ShopRarityBonus", 1,
                new ConfigDescription("商店稀有度等级加成（加到 GetCurrentShopRarity 的返回值上）。",
                    new AcceptableValueRange<int>(0, 4)));

            // ═══════ 模块 4：解锁 ═══════
            UnlockAllArtifacts = Config.Bind("4-解锁", "UnlockAllArtifacts", false,
                "让所有神器都视为已解锁（对应「神器解锁不完 / 专属专长见不到」）。");

            ParseDisabledRunMods();

            Logger.LogInfo("===== DiceVaders QoL v1.0.0 =====");

            // ═══════ 应用补丁 ═══════
            Harmony = new Harmony(Guid);
            int patched = 0;

            if (DisableBadRunMods.Value)
            {
                try { Harmony.PatchAll(typeof(Patch_HasRunMod)); patched++; Logger.LogInfo("  [模块1] 难度机制解除 已启用"); }
                catch (Exception e) { Logger.LogError("  [模块1] 补丁失败: " + e.Message); }
            }

            if (BoostRarityChance.Value)
            {
                try { Harmony.PatchAll(typeof(Patch_GetChanceForRarity)); patched++; Logger.LogInfo("  [模块2] 掉落品质 已启用"); }
                catch (Exception e) { Logger.LogError("  [模块2] 补丁失败: " + e.Message); }
            }

            if (BoostShopRarity.Value)
            {
                try { Harmony.PatchAll(typeof(Patch_GetCurrentShopRarity)); patched++; Logger.LogInfo("  [模块3] 商店品质 已启用"); }
                catch (Exception e) { Logger.LogError("  [模块3] 补丁失败: " + e.Message); }
            }

            if (UnlockAllArtifacts.Value)
            {
                try { Harmony.PatchAll(typeof(Patch_IsArtifactUnlocked)); patched++; Logger.LogInfo("  [模块4] 全解锁 已启用"); }
                catch (Exception e) { Logger.LogError("  [模块4] 补丁失败: " + e.Message); }
            }

            Logger.LogInfo($"已应用 {patched} 个模块。");
        }

        private static void ParseDisabledRunMods()
        {
            DisabledRunMods.Clear();
            var raw = DisabledRunModNames.Value ?? "";
            foreach (var part in raw.Split(','))
            {
                var s = part.Trim();
                if (s.Length > 0) DisabledRunMods.Add(s);
            }
            Logger.LogInfo($"  将拦掉 {DisabledRunMods.Count} 个 RunMod: {string.Join(" | ", DisabledRunMods)}");
        }

        internal static void ReParse() => ParseDisabledRunMods();
    }

    // ═══════════════════════════════════════════════════════
    // 模块 1：难度机制解除
    //
    // EncounterModel.HasRunMod(RunModName) —— RVA 0x1D30910
    //   伪代码：
    //     var mods = this.CurrentRunMods;             // EncounterModel + 0x68
    //     if (mods.Contains(ChallengeExtraMod=900))
    //         if (runMod == 1|2|3|5) { ...额外激活... }
    //     return mods.Contains(runMod);
    //   拦法：Prefix 直接改返回值 + 跳过原方法。
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_HasRunMod
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            // 用名字定位，避免 nameof 在某些 interop 版本下拿不到
            var t = AccessTools.TypeByName("StarVaders.EncounterModel");
            if (t == null) { Plugin.Logger?.LogError("  找不到 StarVaders.EncounterModel"); return null; }
            return AccessTools.Method(t, "HasRunMod");
        }

        [HarmonyPrefix]
        static bool Prefix(StarVaders.RunModName runModName, ref bool __result)
        {
            try
            {
                if (Plugin.DisableBadRunMods != null && Plugin.DisableBadRunMods.Value)
                {
                    var name = runModName.ToString();
                    if (Plugin.DisabledRunMods.Contains(name))
                    {
                        __result = false;   // 视为「没有这个修正」
                        return false;       // 跳过原方法
                    }
                }
            }
            catch { }
            return true;   // 其余照原逻辑
        }
    }

    // ═══════════════════════════════════════════════════════
    // 模块 2：掉落品质
    //
    // ContentGetter.GetChanceForRarity(int rarity, EncounterModel) —— RVA 0x1CFC450
    //   伪代码：
    //     iVar3 = MIN((天数-1)*5, 20) + 修正值;
    //     p4 = iVar3*K1 - C1;  p3 = iVar3*K2 + C2;  p2 = iVar3*K3 + C3;
    //     p1 = 100 - (p2+p3+p4);
    //   拦法：Postfix 把结果乘倍率（并夹到 100）。
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_GetChanceForRarity
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("StarVaders.ContentGetter");
            if (t == null) { Plugin.Logger?.LogError("  找不到 StarVaders.ContentGetter"); return null; }
            return AccessTools.Method(t, "GetChanceForRarity");
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
                // 只放大高稀有度（2/3/4）；稀有度 1 是补数，放大会把它压成负数
                int r = (int)rarity;
                if (r >= 2 && r <= 4)
                {
                    __result *= Plugin.RarityChanceMul.Value;
                    // ★ 返回值是 0~1 的归一化概率（伪代码末尾夹紧用的常量 DAT_183a24e60 = 1.0），
                    //   不是百分比 —— 上限写 100 等于没有夹紧。
                    if (__result > 1f) __result = 1f;
                }
            }
            catch { }
        }
    }

    // ═══════════════════════════════════════════════════════
    // 模块 3：商店品质
    //
    // EncounterModel.GetCurrentShopRarity() —— RVA 0x1D31110
    //   拦法：Postfix 加上加成值。
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_GetCurrentShopRarity
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("StarVaders.EncounterModel");
            if (t == null) return null;
            return AccessTools.Method(t, "GetCurrentShopRarity");
        }

        [HarmonyPostfix]
        static void Postfix(ref int __result)
        {
            try
            {
                if (Plugin.BoostShopRarity == null || !Plugin.BoostShopRarity.Value) return;
                __result += Plugin.ShopRarityBonus.Value;
            }
            catch { }
        }
    }

    // ═══════════════════════════════════════════════════════
    // 模块 4：神器全解锁
    //
    // ContentGetter.IsArtifactUnlocked(ArtifactName) —— RVA 0x1CFEF10
    //   拦法：Prefix 直接返回 true。
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_IsArtifactUnlocked
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("StarVaders.ContentGetter");
            if (t == null) return null;
            return AccessTools.Method(t, "IsArtifactUnlocked");
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
            catch { }
            return true;
        }
    }
}
