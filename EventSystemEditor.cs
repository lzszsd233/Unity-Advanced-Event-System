#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System;
using System.Reflection;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditorInternal;

// 事件系统编辑器窗口：显示指定命名空间的事件及订阅详情
public class EventSystemEditor : EditorWindow
{
    private Vector2 scrollPosition;
    private string eventsNamespace = "EventsNamespace"; // Events命名空间
    private Dictionary<Type, bool> eventFoldoutStates = new Dictionary<Type, bool>();
    private List<Type> eventTypes = new List<Type>();
    private string searchFilter = "";
    // 缓存用于支持可拖拽重排
    private Dictionary<Type, List<EventManager.SubscriberMetadata>> subscriberMetaCache = new Dictionary<Type, List<EventManager.SubscriberMetadata>>();
    private Dictionary<Type, ReorderableList> reorderableLists = new Dictionary<Type, ReorderableList>();
    private Dictionary<Type, List<string>> previousOrderCache = new Dictionary<Type, List<string>>();

    [MenuItem("Tools/事件系统监视器")]
    public static void ShowWindow()
    {
        GetWindow<EventSystemEditor>("事件系统监视器");
    }

    // 初始化窗口：查找事件类型并注册刷新
    private void OnEnable()
    {
        // 查找指定命名空间中的所有事件类型
        FindEventTypesInNamespace();

        // 初始化折叠状态
        foreach (var type in eventTypes)
        {
            if (!eventFoldoutStates.ContainsKey(type))
            {
                eventFoldoutStates[type] = false;
            }
        }

        // 定期刷新界面
        EditorApplication.delayCall += DelayedRepaint;
    }

    // 取消注册刷新
    private void OnDisable()
    {
        EditorApplication.delayCall -= DelayedRepaint;
    }

    // 绘制窗口 UI
    private void OnGUI()
    {
        GUILayout.Label("事件订阅监控", EditorStyles.boldLabel);

        // 命名空间设置和搜索
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("事件命名空间:", GUILayout.Width(100));
        EditorGUILayout.LabelField(eventsNamespace, GUILayout.Width(200));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        searchFilter = EditorGUILayout.TextField("搜索事件", searchFilter, GUILayout.Width(250));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("刷新订阅信息"))
        {
            RefreshSubscriptionInfo();
        }

        if (GUILayout.Button("清理无效订阅"))
        {
            EventManager.GetSubscriptionStats(); // 触发清理
            Repaint();
        }
        EditorGUILayout.EndHorizontal();

        // 显示事件类型数量信息
        var stats = EventManager.GetSubscriptionStats();
        int totalSubscriptions = 0;
        int relevantSubscriptions = 0;

        foreach (var kvp in stats)
        {
            totalSubscriptions += kvp.Value;
            if (eventTypes.Contains(kvp.Key))
            {
                relevantSubscriptions += kvp.Value;
            }
        }

        EditorGUILayout.LabelField(
            $"事件数量: {eventTypes.Count}, 订阅数: {relevantSubscriptions} (总订阅: {totalSubscriptions})",
            EditorStyles.helpBox
        );

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        // 显示事件类型和订阅详情
        foreach (var eventType in eventTypes)
        {
            // 应用搜索过滤
            if (!string.IsNullOrEmpty(searchFilter) &&
                !eventType.Name.ToLower().Contains(searchFilter.ToLower()))
            {
                continue;
            }

            // 获取该事件的订阅数量
            int subscriptionCount = stats.ContainsKey(eventType) ? stats[eventType] : 0;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // 事件类型标题行
            EditorGUILayout.BeginHorizontal();

            // 折叠按钮
            bool foldout = eventFoldoutStates.ContainsKey(eventType) && eventFoldoutStates[eventType];

            foldout = EditorGUILayout.Foldout(foldout, "", true);
            eventFoldoutStates[eventType] = foldout;

            // 事件类型名称和订阅数量
            GUIStyle typeStyle = new GUIStyle(EditorStyles.boldLabel);
            typeStyle.normal.textColor = subscriptionCount > 0 ? Color.white : Color.gray;
            typeStyle.alignment = TextAnchor.MiddleLeft;

            EditorGUILayout.LabelField(eventType.Name, typeStyle, GUILayout.Width(180), GUILayout.ExpandWidth(false));
            EditorGUILayout.LabelField($"{subscriptionCount} 订阅", EditorStyles.label, GUILayout.Width(80), GUILayout.ExpandWidth(false));

            EditorGUILayout.EndHorizontal();

            // 显示订阅详情
            if (foldout)
            {
                EditorGUI.indentLevel++;

                // 显示事件类型的字段信息
                EditorGUILayout.Space();

                FieldInfo[] fields = eventType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                if (fields.Length == 0)
                    EditorGUILayout.LabelField("无参数", EditorStyles.miniBoldLabel);
                else
                    EditorGUILayout.LabelField("事件参数:", EditorStyles.miniBoldLabel);
                foreach (FieldInfo field in fields)
                {
                    var prettyType = GetPrettyTypeName(field.FieldType);
                    EditorGUILayout.LabelField($"  {prettyType}   {field.Name}", EditorStyles.miniLabel);
                }

                // 显示订阅者信息
                if (subscriptionCount > 0)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("订阅者详情:", EditorStyles.miniBoldLabel);
                    // 获取并缓存订阅者元数据（保持一个可变的列表引用供 ReorderableList 使用）
                    List<EventManager.SubscriberMetadata> cachedList;
                    if (!subscriberMetaCache.TryGetValue(eventType, out cachedList) || cachedList == null)
                    {
                        cachedList = EventManager.GetSubscriberMetadataForEvent(eventType);
                        subscriberMetaCache[eventType] = cachedList;
                        // 初始化 previousOrderCache
                        previousOrderCache[eventType] = cachedList.Select(m => GetSubscriberId(m)).ToList();
                    }

                    // 如果数组长度不一致（例如订阅变更），刷新缓存内容
                    var fresh = EventManager.GetSubscriberMetadataForEvent(eventType);
                    if (fresh.Count != cachedList.Count)
                    {
                        cachedList.Clear();
                        cachedList.AddRange(fresh);
                        previousOrderCache[eventType] = cachedList.Select(m => GetSubscriberId(m)).ToList();
                        // 清理掉已有的 ReorderableList，让它在下一步重新构建
                        reorderableLists.Remove(eventType);
                    }

                    // 创建或获取 ReorderableList
                    ReorderableList rl;
                    if (!reorderableLists.TryGetValue(eventType, out rl) || rl == null)
                    {
                        rl = new ReorderableList(cachedList, typeof(EventManager.SubscriberMetadata), true, false, false, false);

                        rl.drawHeaderCallback = (Rect rect) =>
                        {
                            EditorGUI.LabelField(rect, "订阅方法（可拖拽调整调用顺序）");
                        };

                        // 计算元素高度以支持换行显示
                        rl.elementHeightCallback = (int index) =>
                        {
                            var meta = cachedList[index];
                            var text = meta != null ? meta.SubscriberInfo : "(unknown)";
                            var style = new GUIStyle(EditorStyles.label) { wordWrap = true };
                            float labelWidth = EditorGUIUtility.currentViewWidth - 260; // 预留按钮宽度与内边距
                            if (labelWidth < 100) labelWidth = 100;
                            var h = style.CalcHeight(new GUIContent(text), labelWidth);
                            return (int)h + 8;
                        };

                        rl.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
                        {
                            var meta = cachedList[index];
                            var subscriberText = meta != null ? meta.SubscriberInfo : "(unknown)";

                            var labelStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };

                            var labelRect = new Rect(rect.x + 10, rect.y + 2, rect.width - 120, rect.height - 4);
                            EditorGUI.LabelField(labelRect, "  " + subscriberText, labelStyle);

                            var pingRect = new Rect(rect.x + rect.width - 110, rect.y + 2, 50, EditorGUIUtility.singleLineHeight);
                            var openRect = new Rect(rect.x + rect.width - 55, rect.y + 2, 50, EditorGUIUtility.singleLineHeight);

                            bool isUnityObject = meta != null && meta.Target is UnityEngine.Object && meta.Target != null;
                            GUI.enabled = isUnityObject;
                            if (GUI.Button(pingRect, "定位"))
                            {
                                if (isUnityObject)
                                {
                                    EditorGUIUtility.PingObject((UnityEngine.Object)meta.Target);
                                    UnityEditor.Selection.activeObject = (UnityEngine.Object)meta.Target;
                                }
                            }

                            if (GUI.Button(openRect, "打开"))
                            {
                                OpenScriptForSubscriberMetadata(meta);
                            }
                            GUI.enabled = true;
                        };

                        rl.onChangedCallback = (ReorderableList list) =>
                        {
                            try
                            {
                                // 重新计算新顺序对应的原索引列表
                                var newIds = cachedList.Select(m => GetSubscriberId(m)).ToList();
                                var oldIds = previousOrderCache.ContainsKey(eventType) ? previousOrderCache[eventType] : new List<string>();

                                var newOrder = new List<int>();
                                for (int i = 0; i < newIds.Count; i++)
                                {
                                    var id = newIds[i];
                                    int oldIdx = oldIds.IndexOf(id);
                                    if (oldIdx < 0)
                                    {
                                        // 未找到，尝试按照方法名+声明类型匹配
                                        oldIdx = oldIds.FindIndex(s => s.Split('|')[0] == id.Split('|')[0]);
                                    }
                                    if (oldIdx < 0) oldIdx = i; // 保守处理
                                    newOrder.Add(oldIdx);
                                }

                                // 调用 EventManager 进行原子重排
                                bool ok = EventManager.ReorderSubscribers(eventType, newOrder);
                                if (!ok)
                                {
                                    Debug.LogWarning($"无法重排事件 {eventType.Name} 的订阅者：EventManager.ReorderSubscribers 返回失败");
                                }
                                else
                                {
                                    // 更新 previousOrderCache
                                    previousOrderCache[eventType] = newIds;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"重排订阅者时出错: {ex.Message}");
                            }
                        };

                        reorderableLists[eventType] = rl;
                    }

                    // 绘制 ReorderableList
                    reorderableLists[eventType].DoLayoutList();
                }
                else
                {
                    EditorGUILayout.LabelField("  暂无订阅", EditorStyles.miniLabel);
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndScrollView();
    }

    // 将 Type 转为可读名称（支持泛型/数组）
    private string GetPrettyTypeName(Type t)
    {
        if (t == null) return "null";
        if (t.IsArray)
        {
            return GetPrettyTypeName(t.GetElementType()) + "[]";
        }

        if (t.IsGenericType)
        {
            var genericDef = t.GetGenericTypeDefinition();
            var name = genericDef.Name;
            var backtick = name.IndexOf('`');
            if (backtick > 0) name = name.Substring(0, backtick);
            var args = t.GetGenericArguments().Select(a => GetPrettyTypeName(a));
            return $"{name}<{string.Join(", ", args)}>";
        }

        return t.Name;
    }

    // 查找指定命名空间中的事件结构体类型
    private void FindEventTypesInNamespace()
    {
        // 保留旧的折叠状态以便在重新扫描后恢复
        var oldFoldoutStates = new Dictionary<Type, bool>(eventFoldoutStates);

        eventTypes.Clear();
        eventFoldoutStates.Clear();

        if (string.IsNullOrEmpty(eventsNamespace))
        {
            Debug.LogWarning("事件命名空间未设置");
            return;
        }

        // 获取所有程序集
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                // 获取所有类型
                foreach (var type in assembly.GetTypes())
                {
                    // 只选择结构体（值类型）且在指定命名空间中
                    if (type.IsValueType &&
                        !type.IsEnum &&
                        !type.IsPrimitive &&
                        type.Namespace == eventsNamespace)
                    {
                        eventTypes.Add(type);
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // 忽略无法加载的程序集
            }
        }

        // 按名称排序
        eventTypes = eventTypes.OrderBy(t => t.Name).ToList();

        // 恢复折叠状态：如果之前存在该类型的状态则恢复，否则默认 false
        foreach (var type in eventTypes)
        {
            if (oldFoldoutStates.TryGetValue(type, out bool state))
            {
                eventFoldoutStates[type] = state;
            }
            else
            {
                eventFoldoutStates[type] = false;
            }
        }

        Debug.Log($"在命名空间 '{eventsNamespace}' 中找到 {eventTypes.Count} 个事件类型");
    }

    // 刷新订阅信息并清理缓存
    private void RefreshSubscriptionInfo()
    {
        FindEventTypesInNamespace();

        // 清理编辑器缓存，以便反映运行时的最新订阅状态
        subscriberMetaCache.Clear();
        reorderableLists.Clear();
        previousOrderCache.Clear();

        Repaint();
    }

    // 生成订阅者稳定 ID（用于比较顺序）
    private string GetSubscriberId(EventManager.SubscriberMetadata meta)
    {
        if (meta == null) return "(null)";
        var baseId = meta.SubscriberInfo ?? (meta.DeclaringTypeFullName + ":" + meta.MethodName);
        if (meta.Target is UnityEngine.Object uo && uo != null)
        {
            try { return baseId + "|" + uo.GetInstanceID().ToString(); } catch { }
        }
        return baseId + "|" + (meta.DeclaringTypeFullName ?? "") + ":" + (meta.MethodName ?? "");
    }

    // 打开目标对象对应脚本，若提供 methodName 则尝试定位到方法行
    private void OpenScriptForTarget(object target, string methodName = null)
    {
        if (target is UnityEngine.Object uobj && uobj != null)
        {
            var t = uobj.GetType();

            // 优先使用更精确的 MonoScript 定位：
            // 1) 如果是 MonoBehaviour / ScriptableObject，使用公开 API
            // 2) 反射调用内部 MonoScript.FromType(Type)（若可用）
            // 3) 回退到按类型名在 AssetDatabase 中查找
            MonoScript script = null;
            try
            {
                if (uobj is MonoBehaviour mb)
                {
                    script = MonoScript.FromMonoBehaviour(mb);
                }
                else if (uobj is ScriptableObject so)
                {
                    script = MonoScript.FromScriptableObject(so);
                }

                // 如果仍未找到，尝试通过反射调用内部 FromType(Type)
                if (script == null)
                {
                    try
                    {
                        var mi = typeof(MonoScript).GetMethod("FromType", BindingFlags.Static | BindingFlags.NonPublic);
                        if (mi != null)
                        {
                            var maybe = mi.Invoke(null, new object[] { t });
                            script = maybe as MonoScript;
                        }
                    }
                    catch { /* 忽略反射失败 */ }
                }

                // 最后回退到按类型名查找 MonoScript 资源
                if (script == null)
                {
                    var guids = AssetDatabase.FindAssets($"t:MonoScript {t.Name}");
                    if (guids != null && guids.Length > 0)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                        script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                    }
                }

                if (script != null)
                {
                    // 如果传入方法名，尝试定位到方法所在行
                    if (!string.IsNullOrEmpty(methodName))
                    {
                        TryOpenScriptAtMethod(script, methodName);
                        return;
                    }

                    AssetDatabase.OpenAsset(script);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"搜索/打开脚本时出错: {ex.Message}");
            }

            // 未找到脚本时，选中对象，提示用户手动查找
            UnityEditor.Selection.activeObject = uobj;
            Debug.LogWarning($"未找到脚本文件：{t.Name}，已选中对象以便手动查看。");
        }
        else
        {
            Debug.LogWarning("无法打开订阅者脚本：目标不是 UnityEngine.Object 或已被回收");
        }
    }

    // 使用元数据尝试定位并打开订阅者脚本，定位方法优先
    private void OpenScriptForSubscriberMetadata(EventManager.SubscriberMetadata meta)
    {
        if (meta == null)
            return;

        // 首先尝试使用声明类型信息定位 Type
        if (!string.IsNullOrEmpty(meta.DeclaringTypeFullName))
        {
            Type targetType = null;

            // 尝试使用装配限定名获取
            if (!string.IsNullOrEmpty(meta.DeclaringAssemblyName))
            {
                var assemblyQualified = meta.DeclaringTypeFullName + ", " + meta.DeclaringAssemblyName;
                targetType = Type.GetType(assemblyQualified);
            }

            // 如果未找到，搜索已加载程序集
            if (targetType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(meta.DeclaringAssemblyName) && asm.GetName().Name != meta.DeclaringAssemblyName)
                            continue;

                        var t = asm.GetType(meta.DeclaringTypeFullName);
                        if (t != null)
                        {
                            targetType = t;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (targetType != null)
            {
                // 通过反射调用内部 MonoScript.FromType(Type)
                try
                {
                    var mi = typeof(MonoScript).GetMethod("FromType", BindingFlags.Static | BindingFlags.NonPublic);
                    if (mi != null)
                    {
                        var maybe = mi.Invoke(null, new object[] { targetType });
                        var script = maybe as MonoScript;
                        if (script != null)
                        {
                            if (!string.IsNullOrEmpty(meta.MethodName))
                            {
                                TryOpenScriptAtMethod(script, meta.MethodName);
                                return;
                            }
                            AssetDatabase.OpenAsset(script);
                            return;
                        }
                    }
                }
                catch { /* 忽略反射失败，回退 */ }
            }
        }

        // 回退：如果有目标 Unity 对象，使用以前的逻辑
        if (meta.Target is UnityEngine.Object uobj && uobj != null)
        {
            OpenScriptForTarget(uobj, meta.MethodName);
            return;
        }

        // 最后回退：按声明类型名在项目中查找 MonoScript
        if (!string.IsNullOrEmpty(meta.DeclaringTypeFullName))
        {
            var typeName = meta.DeclaringTypeFullName.Split('.').Last();
            var guids = AssetDatabase.FindAssets($"t:MonoScript {typeName}");
            if (guids != null && guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script != null)
                {
                    if (!string.IsNullOrEmpty(meta.MethodName))
                    {
                        TryOpenScriptAtMethod(script, meta.MethodName);
                        return;
                    }
                    AssetDatabase.OpenAsset(script);
                    return;
                }
            }
        }

        Debug.LogWarning($"无法定位订阅者脚本：{meta.SubscriberInfo}");
    }

    // 打开脚本并尝试跳转到方法定义行（基于文本搜索）
    private void TryOpenScriptAtMethod(MonoScript script, string methodName)
    {
        try
        {
            var path = AssetDatabase.GetAssetPath(script);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                AssetDatabase.OpenAsset(script);
                return;
            }

            var lines = File.ReadAllLines(path);

            // 跳过注释块并使用正则匹配方法声明，提高准确性
            bool inBlockComment = false;
            int foundLine = -1;

            // 更严格的正则：匹配返回类型 + 方法名 + '('，例如: public void MethodName(...)
            string strictPattern = @"\b(?:public|private|protected|internal|static|virtual|override|async|sealed|extern|unsafe|new|protected internal|private protected)\b[^{;\n]*\b" + Regex.Escape(methodName) + @"\s*(?:<[^>]*>)?\s*\(";
            var strictRegex = new Regex(strictPattern);

            // 宽松匹配：仅匹配 methodName(...)
            string loosePattern = Regex.Escape(methodName) + @"\s*(?:<[^>]*>)?\s*\(";
            var looseRegex = new Regex(loosePattern);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // 处理块注释开始/结束
                if (!inBlockComment)
                {
                    int startIdx = line.IndexOf("/*");
                    int endIdx = line.IndexOf("*/");
                    if (startIdx >= 0 && (endIdx < 0 || endIdx < startIdx))
                    {
                        inBlockComment = true;
                    }
                }

                if (inBlockComment)
                {
                    if (line.Contains("*/"))
                    {
                        inBlockComment = false;
                    }
                    continue;
                }

                // 移除行注释
                int idx = line.IndexOf("//");
                if (idx >= 0)
                    line = line.Substring(0, idx);

                // 跳过空行
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // 先尝试严格匹配（方法声明），再宽松匹配
                if (strictRegex.IsMatch(line))
                {
                    foundLine = i;
                    break;
                }
            }

            // 如果没有严格匹配，再做一次宽松匹配扫描
            if (foundLine < 0)
            {
                inBlockComment = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!inBlockComment)
                    {
                        int startIdx = line.IndexOf("/*");
                        int endIdx = line.IndexOf("*/");
                        if (startIdx >= 0 && (endIdx < 0 || endIdx < startIdx))
                        {
                            inBlockComment = true;
                        }
                    }
                    if (inBlockComment)
                    {
                        if (line.Contains("*/"))
                        {
                            inBlockComment = false;
                        }
                        continue;
                    }

                    int idx = line.IndexOf("//");
                    if (idx >= 0)
                        line = line.Substring(0, idx);

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (looseRegex.IsMatch(line))
                    {
                        foundLine = i;
                        break;
                    }
                }
            }

            if (foundLine >= 0)
            {
                AssetDatabase.OpenAsset(script, foundLine + 1);
            }
            else
            {
                AssetDatabase.OpenAsset(script);
                Debug.LogWarning($"未能精确定位方法 '{methodName}'，已打开脚本顶部。请检查方法签名或重载。");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"尝试定位方法时出错: {ex.Message}");
            AssetDatabase.OpenAsset(script);
        }
    }

    // 延迟重绘并持续注册以实现实时监控
    private void DelayedRepaint()
    {
        Repaint();

        // 重新注册以实现持续监控
        if (this) // 检查窗口是否仍然存在
        {
            EditorApplication.delayCall += DelayedRepaint;
        }
    }
}
#endif