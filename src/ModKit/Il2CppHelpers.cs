using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace DiceVaders.ModKit
{
    /// <summary>
    /// ModKit 的日志出口。宿主插件在 Load() 里挂上自己的 logger，
    /// 这样 ModKit 不依赖任何具体插件，又能把日志写进 BepInEx 控制台。
    ///
    /// 设计要点（v1.1 修复）：
    ///   · **日志本身绝不能抛异常** —— 本类大量在 catch 块内被调用，
    ///     若 sink 抛异常会把正在处理的原始异常顶掉，排查线索彻底失真。
    ///     所以每次投递都包在 try/catch 里。
    ///   · **Sink 为 null 时回落到 UnityEngine.Debug** —— 否则「宿主没挂 logger」
    ///     会表现为「零日志」，和「没有异常」无法区分。
    ///   · **分级别** —— Warn/Error 走独立出口，宿主映射到 Logger.LogWarning/LogError，
    ///     这样 BepInEx 日志里能按级别过滤，而不是错误全埋在 Info 里。
    /// </summary>
    public static class ModKitLog
    {
        /// <summary>通用输出（宿主映射到 Logger.LogInfo）。</summary>
        public static Action<string> Sink;
        /// <summary>警告级输出（宿主可映射到 Logger.LogWarning；未挂则回落 Sink）。</summary>
        public static Action<string> WarnSink;
        /// <summary>错误级输出（宿主可映射到 Logger.LogError；未挂则回落 Sink）。</summary>
        public static Action<string> ErrorSink;

        public static void Info(string msg) => Emit(Sink, msg);
        public static void Debug(string msg) => Emit(Sink, "[DBG] " + msg);
        public static void Warn(string msg) => Emit(WarnSink ?? Sink, msg);
        public static void Error(string msg) => Emit(ErrorSink ?? Sink, msg);

        private static void Emit(Action<string> channel, string msg)
        {
            try
            {
                if (channel != null) { channel(msg); return; }
                // 没有 sink：回落到 Unity 自己的日志，保证不静默
                UnityEngine.Debug.Log("[ModKit] " + msg);
            }
            catch
            {
                // 故意空吞：日志投递失败不能反过来打断调用方，
                // 更不能把 catch 块里正在处理的原始异常顶掉。
            }
        }
    }

    /// <summary>
    /// 「每个 tag 只记一次」的异常日志 —— 用来替代遍布全项目的 `catch { }` 空吞。
    ///
    /// 为什么要去重而不是直接记日志：
    ///   这些 catch 大多位于 Update() 等每帧路径上，直接记日志会把日志刷爆 ——
    ///   这正是它们当初被写成空吞的原因。按 tag 去重后：同一个故障点只报一次，
    ///   既留下线索，又不产生噪音。
    ///
    /// 用法：
    ///   catch (Exception e) { LogOnce.Warn("ShopInfo.Update", e); }
    ///
    /// ★ 与 ModKitLog 一样，只在 Unity 主线程调用。
    /// </summary>
    public static class LogOnce
    {
        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object _lock = new object();

        public static void Warn(string tag, Exception e)
        {
            Warn(tag, e == null ? "(null exception)" : e.GetType().Name + ": " + e.Message);
        }

        public static void Warn(string tag, string message)
        {
            bool first;
            lock (_lock) { first = _seen.Add(tag); }
            if (!first) return;
            ModKitLog.Warn($"[{tag}] {message}");
        }

        /// <summary>该 tag 是否已经报过（供调用方自行决定要不要做重活）。</summary>
        public static bool AlreadyWarned(string tag)
        {
            lock (_lock) { return _seen.Contains(tag); }
        }

        /// <summary>清空去重表。换局时调用，让新一局的问题能重新被记录。</summary>
        public static void Reset()
        {
            lock (_lock) { _seen.Clear(); }
        }
    }

    /// <summary>
    /// IL2CPP / interop 层的通用工具。这些方法都是踩过坑之后才写对的，直接用别再自己试。
    /// </summary>
    public static class Il2CppHelpers
    {
        /// <summary>
        /// 拿组件的【真实 IL2CPP 类型名】。失败返回 <c>null</c>（不再返回不透明的 "&lt;err:...&gt;"）。
        ///
        /// ★ 为什么必须这样：interop 下 `comp.GetType()` 对所有组件都返回 `UnityEngine.Component`，
        ///   想判断"这个组件是不是游戏脚本"只能走 IL2CPP 原生 API。
        ///
        /// ★ v1.1 修复：旧实现失败时返回 "&lt;err:...&gt;"，与成功路径的返回值不可区分 ——
        ///   调用方 `IsGameType()` 会判 false，于是**真实游戏组件被静默漏删**（克隆体带着游戏脚本跑），
        ///   而且全程零日志。现在失败返回 null 并主动报告，让调用方能区分
        ///   「不是游戏类型」和「读取失败」这两种完全不同的情况。
        ///
        /// ★ 前置守卫：`comp.Pointer == IntPtr.Zero`（对象已销毁/悬空）时
        ///   il2cpp_object_get_class 会在 native 侧访问违例 ——
        ///   .NET 6 起 AccessViolationException 默认不可捕获，进程直接终止。
        /// </summary>
        public static string RealTypeName(Component comp)
        {
            if (comp == null) return null;

            IntPtr ptr;
            try { ptr = comp.Pointer; }
            catch (Exception e) { LogOnce.Warn("RealTypeName.Pointer", e); return null; }
            if (ptr == IntPtr.Zero) return null;   // 已销毁，不算错误，静默返回

            try
            {
                var cls = IL2CPP.il2cpp_object_get_class(ptr);
                if (cls == IntPtr.Zero) return null;
                // ★ IL2CPP 元数据是 UTF-8，不是系统 ANSI 代码页
                var ns = Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_namespace(cls));
                var nm = Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(cls));
                if (string.IsNullOrEmpty(nm)) return null;
                return string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;
            }
            catch (Exception e)
            {
                LogOnce.Warn("RealTypeName", e);
                return null;
            }
        }

        /// <summary>是否是游戏自身的脚本（StarVaders 命名空间）。
        /// ★ v1.1：改用序数比较，并显式带上命名空间分隔点 —— 否则 "StarVadersTwo" 也会被判成游戏类型，
        ///   而调用方拿到 true 之后是**直接 Destroy**，误判方向不可逆。</summary>
        public static bool IsGameType(string fullName)
            => !string.IsNullOrEmpty(fullName)
               && fullName.StartsWith("StarVaders.", StringComparison.Ordinal);

        // ── 带时间缓存的 FindObjectOfType ──
        // FindObjectOfType 每次都会遍历整个场景；在 Update 里调用就是每秒几十上百次全场景扫描。
        // 场景切换的感知延迟远小于一帧，所以 0.25 秒缓存完全够用。
        private abstract class LookupCacheBase
        {
            public abstract void Invalidate();
        }

        private class LookupCache<T> : LookupCacheBase where T : UnityEngine.Object
        {
            public T Value;
            public float At = -99f;
            public override void Invalidate() { At = -99f; }
        }

        private static readonly Dictionary<Type, LookupCacheBase> _caches = new Dictionary<Type, LookupCacheBase>();
        private const float DefaultInterval = 0.5f;
        /// <summary>找不到时的负缓存时长 —— 避免 miss 时每帧全场景扫描。</summary>
        private const float MissInterval = 0.25f;

        public static T FindCached<T>(float interval = DefaultInterval) where T : UnityEngine.Object
        {
            var key = typeof(T);
            if (!_caches.TryGetValue(key, out var cache))
            {
                cache = new LookupCache<T>();
                _caches[key] = cache;
            }
            var c = (LookupCache<T>)cache;

            float now = Time.realtimeSinceStartup;
            // Unity 的 == 重载会把已销毁对象判为 null，所以这里能正确失效。
            // ★ v1.1：命中与 miss 用不同 TTL —— 旧实现里 `Value != null` 前置条件
            //   导致「找不到」时每帧都重新全场景扫描。
            float ttl = (c.Value != null) ? interval : MissInterval;
            if (now - c.At < ttl) return c.Value;

            try
            {
                c.Value = UnityEngine.Object.FindFirstObjectByType<T>();
                c.At = now;
            }
            catch (Exception e)
            {
                // ★ v1.1：旧实现是空 catch + 无条件推进 At，
                //   把「瞬时失败」固化成 0.5 秒的空结果，且日志零线索。
                LogOnce.Warn("FindCached<" + typeof(T).Name + ">", e);
                c.Value = null;
                c.At = now;   // 失败也退避，避免每帧重试刷屏
            }
            return c.Value;
        }

        /// <summary>
        /// 主动让缓存失效（换局/换场景时调用）。
        /// ★ v1.1：改用非泛型基类多态，替代原来的「反射硬编码字段名 At」——
        ///   旧写法一旦字段改名就会静默变成空操作，而这是「换局残留」的唯一清理入口。
        /// ★ 仅限 Unity 主线程调用。
        /// </summary>
        public static void InvalidateCache()
        {
            foreach (var kv in _caches) kv.Value.Invalidate();
        }

        /// <summary>
        /// 换局钩子 —— 各插件在检测到 <c>EncounterController.Pointer</c> 变化时调用一次。
        ///
        /// 统一做三件事，替代过去「每个插件各自零散地清一半」的做法：
        ///   · 查找缓存失效（避免新局仍拿到旧局的实例）
        ///   · LogOnce 去重表复位（让新一局的问题能重新被记录）
        ///
        /// ★ v1.1 修复（审查 M2 / X5）：`InvalidateCache` 此前**全项目零调用** ——
        ///   换局残留治理实际从未接线，只靠 Unity 的 == 重载兜住「对象被销毁」这一种情况。
        /// </summary>
        public static void OnRunChanged()
        {
            InvalidateCache();
            LogOnce.Reset();
            ModKitLog.Info("  [ModKit] 换局：查找缓存已失效，日志去重表已复位");
        }

        private static IntPtr _lastRunPtr = IntPtr.Zero;

        /// <summary>
        /// 每帧调一次即可的轻量换局检测 —— 给不想自己维护指针字段的插件用。
        /// 内部只在 <c>EncounterController</c> 指针**变化**时触发一次 <see cref="OnRunChanged"/>。
        ///
        /// 已经有自己换局检测的插件（ConstellationTool / ExtraRewardOption）直接用
        /// <see cref="OnRunChanged"/> 就好，**不要两个都调**，否则会重复触发。
        /// </summary>
        public static void PollRunChange()
        {
            IntPtr cur = IntPtr.Zero;
            try
            {
                var ec = FindCached<StarVaders.EncounterController>();
                if (ec != null) cur = ec.Pointer;
            }
            catch (Exception e) { LogOnce.Warn("PollRunChange", e); return; }

            if (cur == IntPtr.Zero) return;
            if (_lastRunPtr != IntPtr.Zero && cur != _lastRunPtr) OnRunChanged();
            _lastRunPtr = cur;
        }

        /// <summary>遍历场景里所有某类型的对象，带异常保护（interop 下偶发抛错）。</summary>
        public static T[] FindAll<T>() where T : UnityEngine.Object
        {
            try { return UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None); }
            catch (Exception e)
            {
                ModKitLog.Warn($"FindObjectsByType<{typeof(T).Name}> 失败: {e.GetType().Name}: {e.Message}");
                return new T[0];
            }
        }

        /// <summary>
        /// Safe getter：读游戏字段时包一层，避免单个属性抛异常打断整段逻辑。
        /// ★ v1.1：增加 tag —— 旧实现完全静默，会把 IL2CPP 的类型不匹配（如装箱类型不对）
        ///   无痕转成 default，调用方拿着错误值继续往下跑，把可定位的异常变成不可定位的远端崩溃。
        ///   涉及数值写入/任务引擎参数的读取**务必传 tag**。
        /// </summary>
        public static T Safe<T>(Func<T> f, T fallback = default, string tag = null)
        {
            try { return f(); }
            catch (Exception e)
            {
                if (tag != null) LogOnce.Warn(tag, e);
                return fallback;
            }
        }

        /// <summary>Safe action：执行可能抛错的游戏调用。</summary>
        public static bool SafeDo(Action a, string tag = null)
        {
            try { a(); return true; }
            catch (Exception e)
            {
                if (tag != null) LogOnce.Warn(tag, e);
                return false;
            }
        }
    }
}
