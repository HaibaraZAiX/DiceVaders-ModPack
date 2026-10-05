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
    /// </summary>
    public static class ModKitLog
    {
        public static Action<string> Sink;

        public static void Info(string msg) => Sink?.Invoke(msg);
        public static void Debug(string msg) => Sink?.Invoke("[DBG] " + msg);
    }

    /// <summary>
    /// IL2CPP / interop 层的通用工具。这些方法都是踩过坑之后才写对的，直接用别再自己试。
    /// </summary>
    public static class Il2CppHelpers
    {
        /// <summary>
        /// 拿组件的【真实 IL2CPP 类型名】。
        ///
        /// ★ 为什么必须这样：interop 下 `comp.GetType()` 对所有组件都返回 `UnityEngine.Component`，
        ///   想判断"这个组件是不是游戏脚本"只能走 IL2CPP 原生 API。
        /// </summary>
        public static string RealTypeName(Component comp)
        {
            try
            {
                var cls = IL2CPP.il2cpp_object_get_class(comp.Pointer);
                var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(cls));
                var nm = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls));
                if (string.IsNullOrEmpty(nm)) return "<unknown>";
                return string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;
            }
            catch (Exception e) { return "<err:" + e.Message + ">"; }
        }

        /// <summary>是否是游戏自身的脚本（StarVaders 命名空间）。</summary>
        public static bool IsGameType(string fullName)
            => !string.IsNullOrEmpty(fullName) && fullName.StartsWith("StarVaders");

        // ── 带时间缓存的 FindObjectOfType ──
        // FindObjectOfType 每次都会遍历整个场景；在 Update 里调用就是每秒几十上百次全场景扫描。
        // 场景切换的感知延迟远小于一帧，所以 0.25 秒缓存完全够用。
        private class LookupCache<T> where T : UnityEngine.Object
        {
            public T Value;
            public float At = -99f;
        }

        private static readonly Dictionary<Type, object> _caches = new Dictionary<Type, object>();
        private const float DefaultInterval = 0.25f;

        public static T FindCached<T>(float interval = DefaultInterval) where T : UnityEngine.Object
        {
            var key = typeof(T);
            if (!_caches.TryGetValue(key, out var boxed))
            {
                boxed = new LookupCache<T>();
                _caches[key] = boxed;
            }
            var cache = (LookupCache<T>)boxed;

            float now = Time.realtimeSinceStartup;
            // Unity 的 == 重载会把已销毁对象判为 null，所以这里能正确失效
            if (cache.Value != null && now - cache.At < interval) return cache.Value;

            try { cache.Value = UnityEngine.Object.FindObjectOfType<T>(); }
            catch { cache.Value = null; }
            cache.At = now;
            return cache.Value;
        }

        /// <summary>主动让缓存失效（例如刚进新场景时）。</summary>
        public static void InvalidateCache()
        {
            foreach (var kv in _caches)
            {
                var f = kv.Value.GetType().GetField("At");
                f?.SetValue(kv.Value, -99f);
            }
        }

        /// <summary>遍历场景里所有某类型的对象，带异常保护（interop 下偶发抛错）。</summary>
        public static T[] FindAll<T>() where T : UnityEngine.Object
        {
            try { return UnityEngine.Object.FindObjectsOfType<T>(); }
            catch (Exception e) { ModKitLog.Info($"FindObjectsOfType<{typeof(T).Name}> 失败: {e.Message}"); return new T[0]; }
        }

        /// <summary>Safe getter：读游戏字段时包一层，避免单个属性抛异常打断整段逻辑。</summary>
        public static T Safe<T>(Func<T> f, T fallback = default)
        {
            try { return f(); } catch { return fallback; }
        }

        /// <summary>Safe action：执行可能抛错的游戏调用。</summary>
        public static bool SafeDo(Action a, string tag = null)
        {
            try { a(); return true; }
            catch (Exception e) { if (tag != null) ModKitLog.Info($"  [{tag}] {e.GetType().Name}: {e.Message}"); return false; }
        }
    }
}
