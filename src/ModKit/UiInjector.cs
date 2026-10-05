using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DiceVaders.ModKit
{
    /// <summary>
    /// UI 注入工具 —— 往游戏界面上加"看起来像原版"的元素。
    ///
    /// 全部结论都有实测出处，见 `_analysis\Mod开发手册.md`：
    ///   · 必须用独立 Overlay Canvas（游戏的 Canvas 是 ScreenSpaceCamera，锚点算不出有效矩形）
    ///   · 做原版样式的按钮必须【克隆游戏现成的按钮容器】，不能自己拼
    ///     （游戏按钮是多层子对象叠加，单层图是纯白，拼出来是白块）
    ///   · 克隆后必须保持源按钮长宽比（上下边框按原高度定位，压扁会被挤出可见区）
    ///   · 克隆体的文字会被游戏每帧重写回原值 → 必须每帧校正
    /// </summary>
    public static class UiInjector
    {
        private static GameObject _canvas;

        /// <summary>
        /// 拿（或创建）本 mod 专用的 Overlay Canvas。
        /// 不挂 CanvasScaler，让 scaleFactor 恒为 1，坐标就是真实屏幕像素。
        /// </summary>
        public static GameObject GetOverlayCanvas(string name = "ModKit_Overlay", int sortingOrder = 32000)
        {
            if (_canvas != null) return _canvas;
            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;   // ★ 关键
            canvas.sortingOrder = sortingOrder;
            canvas.overrideSorting = true;

            _canvas = go;
            ModKitLog.Info($"  已创建 Overlay Canvas ({name}, sortingOrder={sortingOrder})");
            return go;
        }

        /// <summary>把按钮摆到四角之一。extra 是沿排列方向的叠加偏移（右下/左下往上叠，右上往下叠）。</summary>
        public static void ApplyAnchor(RectTransform rt, bool right, bool bottom,
            float rightOffset, float topOffset, float leftOffset, float bottomOffset, float extra = 0f)
        {
            if (right && bottom)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(1f, 0f);
                rt.anchoredPosition = new Vector2(-rightOffset, bottomOffset + extra);
            }
            else if (right)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-rightOffset, -topOffset - extra);
            }
            else
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
                rt.pivot = new Vector2(0f, 0f);
                rt.anchoredPosition = new Vector2(leftOffset, bottomOffset + extra);
            }
        }

        /// <summary>
        /// 剥掉对象上的【游戏逻辑组件】（StarVaders.*），保留所有视觉组件。
        ///
        /// 反转白名单是有意的：游戏 UI 依赖 AllIn1SpriteShader、Coffee.UIParticle、TMP 等，
        /// 用白名单会把这些一起删掉导致视觉异常。
        /// </summary>
        public static int StripGameComponents(GameObject go)
        {
            int killed = 0;
            try
            {
                var comps = go.GetComponentsInChildren<Component>(true);
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var full = Il2CppHelpers.RealTypeName(comp);
                    if (!Il2CppHelpers.IsGameType(full)) continue;
                    // Transform 不能删（虽然 UnityEngine.Transform 不以 StarVaders 开头，
                    // 这行是防将来出现 StarVaders.XXXTransform 的保险）
                    if (full.EndsWith("Transform")) continue;
                    try { UnityEngine.Object.Destroy(comp); killed++; } catch { }
                }
            }
            catch (Exception e) { ModKitLog.Info("  StripGameComponents 失败: " + e.Message); }
            return killed;
        }

        /// <summary>
        /// 找一个可克隆的【按钮容器】。默认优先名字含 "reveal" 的
        /// （实测游戏里「揭晓！」按钮就叫 RevealObject，是带全套美术的容器）。
        /// </summary>
        public static GameObject FindButtonContainer(string nameHint = "reveal", float minWidth = 200f)
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
                    float w = rt != null ? rt.sizeDelta.x : 0f;
                    bool hasTmp = go.GetComponentInChildren<TMPro.TextMeshProUGUI>() != null;

                    if (!string.IsNullOrEmpty(nameHint) && n.Contains(nameHint.ToLowerInvariant())) return go;
                    if (fallback == null && hasTmp && w > minWidth) fallback = go;
                }
                return fallback;
            }
            catch (Exception e) { ModKitLog.Info("  FindButtonContainer 失败: " + e.Message); }
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
            if (src == null) return null;
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
                ModKitLog.Info("  CloneGameButton 失败: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 兜底：完全自建一个按钮（不依赖游戏对象）。
        /// 外观会比克隆版差，仅在找不到可克隆容器时用。
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
                try { tmp.enableAutoSizing = false; } catch { }

                tgo.transform.SetParent(go.transform, false);
                return go;
            }
            catch (Exception e)
            {
                ModKitLog.Info("  MakeSolidButton 失败: " + e.Message);
                return null;
            }
        }

        /// <summary>把对象里的所有 TMP 文字改成指定内容，返回改动的 TMP 列表（供后续校正）。</summary>
        public static List<TMPro.TextMeshProUGUI> SetText(GameObject go, string label)
        {
            var result = new List<TMPro.TextMeshProUGUI>();
            try
            {
                var tmps = go.GetComponentsInChildren<TMPro.TextMeshProUGUI>();
                foreach (var t in tmps)
                {
                    if (t == null) continue;
                    t.text = label;
                    try { t.ForceMeshUpdate(); } catch { }
                    result.Add(t);
                }
                ModKitLog.Info($"  已设文字 '{label}'（{result.Count} 个 TMP）");
            }
            catch (Exception e) { ModKitLog.Info("  SetText 失败: " + e.Message); }
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
                        try { t.ForceMeshUpdate(); } catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>命中测试：鼠标左键这一帧是否点在指定 UI 上。</summary>
        public static bool HitTest(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return false;
            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return false;

                var pos = mouse.position.ReadValue();
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) return false;

                // 自建 Overlay Canvas 传 null 即可；挂在别的 Canvas 下则要带相机
                Camera cam = null;
                var cvs = go.GetComponentInParent<Canvas>();
                if (cvs != null && cvs.renderMode != RenderMode.ScreenSpaceOverlay) cam = cvs.worldCamera;

                return RectTransformUtility.RectangleContainsScreenPoint(rt, pos, cam);
            }
            catch { return false; }
        }

        /// <summary>从游戏里取一份可用字体（自建 UI 需要它，否则中文出不来）。</summary>
        public static TMPro.TMP_FontAsset FindAnyFont(out float size)
        {
            size = 42f;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
                foreach (var t in all)
                {
                    if (t != null && t.font != null)
                    {
                        if (t.fontSize > 0f) size = t.fontSize;
                        return t.font;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>按名字找已加载的 Sprite（做自建 UI 时找素材用）。</summary>
        public static Sprite FindSprite(string exactOrPartial)
        {
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<Sprite>();
                if (string.IsNullOrEmpty(exactOrPartial)) return null;
                foreach (var s in all) if (s != null && s.name == exactOrPartial) return s;
                var low = exactOrPartial.ToLowerInvariant();
                foreach (var s in all) if (s != null && (s.name ?? "").ToLowerInvariant().Contains(low)) return s;
            }
            catch { }
            return null;
        }

        /// <summary>强制让对象可见（源按钮可能是隐藏的，克隆体会继承禁用状态）。</summary>
        public static void ForceVisible(GameObject go)
        {
            try
            {
                var img = go.GetComponent<Image>();
                if (img != null) { img.enabled = true; var c = img.color; c.a = 1f; img.color = c; }
                var cr = go.GetComponent<CanvasRenderer>();
                if (cr != null) cr.SetAlpha(1f);
                var cg = go.GetComponent<CanvasGroup>();
                if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; }
            }
            catch (Exception e) { ModKitLog.Info("  ForceVisible 失败: " + e.Message); }
        }
    }
}
