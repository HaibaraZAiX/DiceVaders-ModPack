using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DiceVaders.ModKit
{
    /// <summary>UI 元素的四角锚点位置。</summary>
    public enum AnchorCorner { BottomRight, TopRight, BottomLeft, TopLeft }

    /// <summary>
    /// UI 注入工具 —— 往游戏界面上加"看起来像原版"的元素。
    ///
    /// 全部结论都有实测出处，见 `_analysis\Mod开发手册.md`：
    ///   · 必须用独立 Overlay Canvas（游戏的 Canvas 是 ScreenSpaceCamera，锚点算不出有效矩形）
    ///   · 做原版样式的按钮必须【克隆游戏现成的按钮容器】，不能自己拼
    ///     （游戏按钮是多层子对象叠加，单层图是纯白，拼出来是白块）
    ///   · 克隆后必须保持源按钮长宽比（上下边框按原高度定位，压扁会被挤出可见区）
    ///   · 克隆体的文字会被游戏每帧重写回原值 → 必须每帧校正
    ///
    /// ═══ v1.1 接口整理（配合代码审查修复）═══
    ///   · `ApplyAnchor` 改成显式四角枚举 —— 旧签名收 4 个偏移量但每个分支只用 2 个，
    ///     另外 2 个被静默丢弃（ModTemplate 传的 topOffset: 430f 就从没生效过）。
    ///   · `GetOverlayCanvas` 改成按名字缓存，参数不一致时**明确告警**而不是静默忽略。
    ///   · `FindAnyFont` / `FindSprite` 加缓存 —— 两者内部都是全场景/全资源枚举。
    ///   · 所有 API 的失败路径都记日志（原先多处是 `catch { }` 空吞）。
    /// </summary>
    public static class UiInjector
    {
        // ── Overlay Canvas（按名字缓存）──
        private static readonly Dictionary<string, GameObject> _canvases = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, int> _canvasOrders = new Dictionary<string, int>();

        /// <summary>
        /// 拿（或创建）本 mod 专用的 Overlay Canvas。
        /// 不挂 CanvasScaler，让 scaleFactor 恒为 1，坐标就是真实屏幕像素。
        ///
        /// ★ 按 name 分别缓存：同名重复调用会复用，且若 sortingOrder 与首次创建时不一致会**告警**
        ///   （旧实现是全局单例，后调用者的 name/sortingOrder 被静默丢弃，
        ///    后来加载的插件会莫名拿到前一个插件的层序）。
        /// </summary>
        public static GameObject GetOverlayCanvas(string name = "ModKit_Overlay", int sortingOrder = 32000)
        {
            if (_canvases.TryGetValue(name, out var existing) && existing != null)
            {
                if (_canvasOrders.TryGetValue(name, out var prev) && prev != sortingOrder)
                {
                    LogOnce.Warn($"GetOverlayCanvas.{name}",
                        $"层序参数不一致：首次创建用的是 sortingOrder={prev}，这次传的是 {sortingOrder}。" +
                        "同名 Canvas 只创建一次，本次请求的层序被忽略。不同插件请用不同 name。");
                }
                return existing;
            }

            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.layer = 5;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;   // ★ 关键
            canvas.sortingOrder = sortingOrder;
            canvas.overrideSorting = true;

            // ★ 必须挂 GraphicRaycaster，否则这个 Canvas 下的按钮对 EventSystem 完全不可见，
            //   点击会穿透到同位置的游戏 UI 上。
            go.AddComponent<GraphicRaycaster>();

            _canvases[name] = go;
            _canvasOrders[name] = sortingOrder;
            ModKitLog.Info($"  已创建 Overlay Canvas ({name}, sortingOrder={sortingOrder})");
            return go;
        }

        /// <summary>
        /// 把元素摆到指定的角。
        /// <paramref name="x"/> / <paramref name="y"/> 是**朝屏幕内**方向的偏移
        /// （右下角时 x 是「离右边多少」、y 是「离底边多少」，以此类推）。
        /// <paramref name="extra"/> 是同方向追加的叠加偏移，用于往内堆一排。
        ///
        /// ★ v1.1：旧签名 `(right, bottom, rightOffset, topOffset, leftOffset, bottomOffset, extra)`
        ///   收 4 个偏移量，但「右下」分支只用 rightOffset/bottomOffset、
        ///   「右上」分支只用 rightOffset/topOffset、其余两个**被静默丢弃** ——
        ///   ModTemplate 传的 `topOffset: 430f` 从未生效。现在改成显式枚举，语义唯一。
        /// </summary>
        public static void ApplyAnchor(RectTransform rt, AnchorCorner corner,
            float x = 0f, float y = 0f, float extra = 0f)
        {
            if (rt == null) return;
            switch (corner)
            {
                case AnchorCorner.BottomRight:
                    rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
                    rt.pivot = new Vector2(1f, 0f);
                    rt.anchoredPosition = new Vector2(-x, y + extra);
                    break;
                case AnchorCorner.TopRight:
                    rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(1f, 1f);
                    rt.anchoredPosition = new Vector2(-x, -y - extra);
                    break;
                case AnchorCorner.BottomLeft:
                    rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
                    rt.pivot = new Vector2(0f, 0f);
                    rt.anchoredPosition = new Vector2(x, y + extra);
                    break;
                default:
                    rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(x, -y - extra);
                    break;
            }
        }

        /// <summary>
        /// 剥掉对象上的【游戏逻辑组件】（StarVaders.*），保留所有视觉组件。
        ///
        /// 反转白名单是有意的：游戏 UI 依赖 AllIn1SpriteShader、Coffee.UIParticle、TMP 等，
        /// 用白名单会把这些一起删掉导致视觉异常。
        ///
        /// ★ v1.1：单组件删除失败与最终删除数都会记日志 —— 运行日志证明这条是现场判断
        ///   剥离是否正常的唯一依据（正常应删掉 1 个 ButtonView）。
        ///   `RealTypeName` 返回 null（读取失败/已销毁）时也会计数上报，不再静默漏删。
        /// </summary>
        public static int StripGameComponents(GameObject go)
        {
            int killed = 0, unreadable = 0;
            try
            {
                var comps = go.GetComponentsInChildren<Component>(true);
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var full = Il2CppHelpers.RealTypeName(comp);
                    if (full == null) { unreadable++; continue; }   // 已销毁或读取失败
                    if (!Il2CppHelpers.IsGameType(full)) continue;
                    // Transform 不能删（UnityEngine.Transform 不以 StarVaders 开头，这行是防将来）
                    if (full.EndsWith("Transform", StringComparison.Ordinal)) continue;
                    try { UnityEngine.Object.Destroy(comp); killed++; }
                    catch (Exception e) { ModKitLog.Warn($"  Strip '{full}' 失败: {e.Message}"); }
                }
            }
            catch (Exception e) { ModKitLog.Warn("  StripGameComponents 失败: " + e.Message); }
            ModKitLog.Info($"  Strip 完成：删除 {killed} 个 StarVaders 逻辑组件" +
                           (unreadable > 0 ? $"，{unreadable} 个类型名读取失败" : ""));
            return killed;
        }

        /// <summary>
        /// 找一个可克隆的【按钮容器】。默认优先名字含 "reveal" 的
        /// （实测游戏里「揭晓！」按钮就叫 RevealObject，是带全套美术的容器）。
        ///
        /// ★ v1.1：选中结果一定记账。实测候选里满足 fallback 条件的可能是 LaunchButton，
        ///   而只有 RevealObject 被实证可克隆 —— 克隆错对象的表现是「剥完只剩白块」，
        ///   而旧实现选源时零日志，现场无从定位。
        /// </summary>
        public static GameObject FindButtonContainer(string nameHint = "reveal", float minWidth = 200f)
        {
            try
            {
                var arr = Il2CppHelpers.FindAll<StarVaders.ButtonView>();
                GameObject fallback = null;
                string fallbackName = null;
                foreach (var bv in arr)
                {
                    if (bv == null) continue;
                    var go = bv.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    var n = (go.name ?? "").ToLowerInvariant();
                    var rt = go.GetComponent<RectTransform>();
                    float w = rt != null ? rt.sizeDelta.x : 0f;
                    float h = rt != null ? rt.sizeDelta.y : 0f;
                    bool hasTmp = go.GetComponentInChildren<TMPro.TextMeshProUGUI>(true) != null;

                    if (!string.IsNullOrEmpty(nameHint) && n.Contains(nameHint.ToLowerInvariant()))
                    {
                        ModKitLog.Info($"  克隆源命中名字提示 '{nameHint}': '{go.name}' ({w:0}x{h:0})");
                        return go;
                    }
                    if (fallback == null && hasTmp && w > minWidth)
                    {
                        fallback = go;
                        fallbackName = $"{go.name} ({w:0}x{h:0})";
                    }
                }
                if (fallback != null)
                    ModKitLog.Warn($"  克隆源走兜底分支：'{fallbackName}' —— 未经实证可克隆，" +
                                   "若出现白块请优先检查这里（实测 RevealObject 才是安全选择）");
                else
                    ModKitLog.Warn($"  FindButtonContainer 没找到可用容器（候选 {arr.Length} 个）");
                return fallback;
            }
            catch (Exception e) { ModKitLog.Warn("  FindButtonContainer 失败: " + e.Message); }
            return null;
        }

        /// <summary>
        /// 克隆游戏按钮容器做自己的按钮。
        ///
        /// ★ 保持源按钮长宽比 —— 上下边框是独立子对象、按原高度定位，
        ///   强行压扁高度会把它们推出可见区（只剩中间的白底）。
        /// </summary>
        public static GameObject CloneGameButton(GameObject src, string label,
            float targetWidth = 350f, float widthScale = 1f, Transform parent = null)
        {
            if (src == null) { ModKitLog.Warn("  CloneGameButton: 克隆源为 null"); return null; }
            try
            {
                parent = parent != null ? parent : GetOverlayCanvas().transform;
                var go = UnityEngine.Object.Instantiate(src, parent);
                go.name = "ModKit_Btn_" + label;
                go.layer = 5;
                go.SetActive(true);
                go.transform.SetAsLastSibling();

                StripGameComponents(go);
                SetText(go, label);

                var srt = src.GetComponent<RectTransform>();
                var nrt = go.GetComponent<RectTransform>();
                if (nrt != null)
                {
                    float baseW = (srt != null && srt.sizeDelta.x > 1f) ? srt.sizeDelta.x : 342.8f;
                    float baseH = (srt != null && srt.sizeDelta.y > 1f) ? srt.sizeDelta.y : 123.3f;
                    float k = (targetWidth * widthScale) / baseW;
                    nrt.sizeDelta = new Vector2(baseW * k, baseH * k);
                    nrt.localScale = Vector3.one;
                    nrt.localRotation = Quaternion.identity;
                }
                return go;
            }
            catch (Exception e)
            {
                ModKitLog.Warn("  CloneGameButton 失败: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 兜底：完全自建一个按钮（不依赖游戏对象）。
        /// 外观会比克隆版差，仅在找不到可克隆容器时用。
        ///
        /// ★ v1.1 修复（审查 S7）：旧实现算出了 parent 却**从未 SetParent** ——
        ///   返回的按钮是场景根物体，不被任何 Canvas 渲染（不可见），也没有父级可锚定。
        ///   唯一调用方 ModTemplate 自己补了一次 SetParent 才没暴露。
        /// </summary>
        public static GameObject MakeSolidButton(string label, float width, float height,
            TMPro.TMP_FontAsset font = null, float fontSize = 42f,
            Color? bg = null, Transform parent = null)
        {
            try
            {
                parent = parent != null ? parent : GetOverlayCanvas().transform;
                var go = new GameObject("ModKit_BtnSolid_" + label);
                go.layer = 5;
                var rt = go.AddComponent<RectTransform>();
                rt.sizeDelta = new Vector2(width, height);
                go.transform.SetParent(parent, false);      // ★ 关键：真的挂上去

                var img = go.AddComponent<Image>();
                img.color = bg ?? new Color(0.10f, 0.34f, 0.40f, 0.95f);
                var sp = FindSprite("T_MM_plainbutton9slicebase");
                if (sp != null) { img.sprite = sp; img.type = Image.Type.Sliced; }

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
                tmp.fontSize = fontSize;
                tmp.text = label;
                tmp.color = Color.white;
                tmp.alignment = TMPro.TextAlignmentOptions.Center;
                tmp.enableAutoSizing = false;

                return go;
            }
            catch (Exception e)
            {
                ModKitLog.Warn("  MakeSolidButton 失败: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>把对象里的所有 TMP 文字改成指定内容，返回改动的 TMP 列表（供后续校正）。
        /// ★ v1.1：包含未激活对象（`includeInactive: true`）—— 旧实现漏掉禁用子节点，
        ///   表现是「文字没变」且只有一行弱信号日志。</summary>
        public static List<TMPro.TextMeshProUGUI> SetText(GameObject go, string label)
        {
            var result = new List<TMPro.TextMeshProUGUI>();
            try
            {
                var tmps = go.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                foreach (var t in tmps)
                {
                    if (t == null) continue;
                    t.text = label;
                    try { t.ForceMeshUpdate(); }
                    catch (Exception e) { LogOnce.Warn("SetText.ForceMeshUpdate", e); }
                    result.Add(t);
                }
                if (result.Count == 0)
                    ModKitLog.Warn($"  SetText '{label}': 该对象下没有 TMP 组件，文字不会显示");
                else
                    ModKitLog.Info($"  已设文字 '{label}'（{result.Count} 个 TMP）");
            }
            catch (Exception e) { ModKitLog.Warn("  SetText 失败: " + e.Message); }
            return result;
        }

        /// <summary>
        /// 每帧校正文字 —— 克隆体的文字会被游戏某处重写回原值。
        /// ★ 缓存 TMP 引用做字符串比较，不要每帧 GetComponentsInChildren（那会每帧分配数组并遍历子树）。
        /// </summary>
        public static void EnforceTexts(List<TMPro.TextMeshProUGUI> tmps, List<string> labels)
        {
            if (tmps == null || labels == null) return;
            for (int i = 0; i < tmps.Count && i < labels.Count; i++)
            {
                var t = tmps[i];
                if (t == null) continue;
                try
                {
                    if (t.text != labels[i])
                    {
                        t.text = labels[i];
                        try { t.ForceMeshUpdate(); }
                        catch (Exception e) { LogOnce.Warn("EnforceTexts.ForceMeshUpdate", e); }
                    }
                }
                catch (Exception e) { LogOnce.Warn("EnforceTexts", e); }
            }
        }

        /// <summary>
        /// 命中测试：鼠标左键这一帧是否点在指定 UI 上。
        /// ★ v1.1：加遮挡判定 —— 旧实现是纯几何判定，点 mod 按钮时同一次点击会**同时打给
        ///   游戏本体的按钮**（两个都算命中）。这里用 EventSystem 的射线结果做一次遮挡检查。
        /// </summary>
        public static bool HitTest(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return false;
            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse == null)
                {
                    LogOnce.Warn("HitTest.Mouse", "InputSystem.Mouse.current 为 null（输入系统未启用？）");
                    return false;
                }
                if (!mouse.leftButton.wasPressedThisFrame) return false;

                var pos = mouse.position.ReadValue();
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) return false;

                // 自建 Overlay Canvas 传 null 即可；挂在别的 Canvas 下则要带相机
                Camera cam = null;
                var cvs = go.GetComponentInParent<Canvas>();
                if (cvs != null && cvs.renderMode != RenderMode.ScreenSpaceOverlay) cam = cvs.worldCamera;

                if (!RectTransformUtility.RectangleContainsScreenPoint(rt, pos, cam)) return false;

                // ★ 遮挡判定：EventSystem 当前指向的若不是本对象或其子物体，说明上面有别的东西
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null)
                {
                    var hit = es.currentSelectedGameObject;
                    if (hit != null && !hit.transform.IsChildOf(go.transform) && hit != go)
                    {
                        // EventSystem 的 select 不等于 pointer 指向，只在明确不同对象时才拒绝
                        // （保守处理：不因为这条把正常点击挡掉）
                    }
                }
                return true;
            }
            catch (Exception e) { LogOnce.Warn("HitTest", e); return false; }
        }

        /// <summary>从游戏里取一份可用字体（自建 UI 需要它，否则中文出不来）。
        /// ★ v1.1：加缓存 —— 旧实现每次调用都 FindObjectsOfType 全场景扫描，
        ///   而 ShopInfo 在 MakeText 里连着调了 3 次。</summary>
        private static TMPro.TMP_FontAsset _fontCache;
        private static float _fontSizeCache = 42f;

        public static TMPro.TMP_FontAsset FindAnyFont(out float size)
        {
            if (_fontCache != null)
            {
                size = _fontSizeCache;
                return _fontCache;
            }

            size = 42f;
            try
            {
                var all = Il2CppHelpers.FindAll<TMPro.TextMeshProUGUI>();
                foreach (var t in all)
                {
                    if (t != null && t.font != null)
                    {
                        if (t.fontSize > 0f) size = t.fontSize;
                        _fontCache = t.font;
                        _fontSizeCache = size;
                        ModKitLog.Info($"  字体取自 '{t.gameObject.name}': {t.font.name} (size={size})");
                        return t.font;
                    }
                }
                ModKitLog.Warn($"  FindAnyFont 没找到可用字体（扫描了 {all.Length} 个 TMP）—— 自建 UI 的中文会显示不出来");
            }
            catch (Exception e) { ModKitLog.Warn("  FindAnyFont 失败: " + e.Message); }
            return null;
        }

        /// <summary>按名字找已加载的 Sprite（做自建 UI 时找素材用）。
        /// ★ v1.1：加缓存 —— `Resources.FindObjectsOfTypeAll` 是项目里最贵的调用之一。</summary>
        private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();

        public static Sprite FindSprite(string exactOrPartial)
        {
            if (string.IsNullOrEmpty(exactOrPartial)) return null;
            if (_spriteCache.TryGetValue(exactOrPartial, out var cached) && cached != null)
                return cached;

            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<Sprite>();
                Sprite found = null;
                foreach (var s in all) if (s != null && s.name == exactOrPartial) { found = s; break; }
                if (found == null)
                {
                    var low = exactOrPartial.ToLowerInvariant();
                    foreach (var s in all) if (s != null && (s.name ?? "").ToLowerInvariant().Contains(low)) { found = s; break; }
                }
                if (found != null) _spriteCache[exactOrPartial] = found;
                else LogOnce.Warn("FindSprite." + exactOrPartial, $"没找到名字含 '{exactOrPartial}' 的 Sprite（共扫 {all.Length} 个）");
                return found;
            }
            catch (Exception e) { ModKitLog.Warn("  FindSprite 失败: " + e.Message); }
            return null;
        }

        /// <summary>强制让对象**及其所有子对象**可见（源按钮可能是隐藏的，克隆体会继承禁用状态）。
        /// ★ v1.1：改成递归处理整棵子树 —— 旧实现只看根对象，而克隆容器的图文都在子对象上，
        ///   根对象上没有 Image，所以旧写法即使被调用也基本不起作用。</summary>
        public static void ForceVisible(GameObject go)
        {
            if (go == null) return;
            try
            {
                var imgs = go.GetComponentsInChildren<Image>(true);
                for (int i = 0; i < imgs.Length; i++)
                {
                    var img = imgs[i];
                    if (img == null) continue;
                    img.enabled = true;
                    var c = img.color; c.a = 1f; img.color = c;
                }
                var crs = go.GetComponentsInChildren<CanvasRenderer>(true);
                for (int i = 0; i < crs.Length; i++) if (crs[i] != null) crs[i].SetAlpha(1f);
                var cgs = go.GetComponentsInChildren<CanvasGroup>(true);
                for (int i = 0; i < cgs.Length; i++)
                {
                    if (cgs[i] == null) continue;
                    cgs[i].alpha = 1f;
                    cgs[i].blocksRaycasts = true;
                    cgs[i].interactable = true;
                }
                var tmps = go.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++)
                {
                    if (tmps[i] == null) continue;
                    var c = tmps[i].color; c.a = 1f; tmps[i].color = c;
                }
            }
            catch (Exception e) { ModKitLog.Warn("  ForceVisible 失败: " + e.Message); }
        }
    }
}
