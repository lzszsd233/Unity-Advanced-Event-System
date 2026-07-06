using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

// 事件管理器：弱引用订阅、发布及订阅顺序持久化
public static class EventManager
{
    private static readonly Dictionary<string, Dictionary<string, int>> persistedOrders = new();
    private static readonly Dictionary<Type, int> nextOrderSequence = new();

    private static bool orderMapLoaded;

    // 可配置的持久化文件路径（优先级: override > 项目目录 > persistentDataPath）
    private static string orderFilePathOverride;

    // 返回订阅者的方法名列表（按当前顺序）
    public static List<string> GetSubscriberMethodNamesForEvent(Type eventType)
    {
        var result = new List<string>();

        if (subscribers.ContainsKey(eventType))
            foreach (var handler in subscribers[eventType])
                if (handler is IWeakEventHandler weakHandler)
                    result.Add(weakHandler.GetHandlerMethodName());

        return result;
    }

    // 返回指定事件类型的订阅者元数据列表
    public static List<SubscriberMetadata> GetSubscriberMetadataForEvent(Type eventType)
    {
        var result = new List<SubscriberMetadata>();

        if (subscribers.ContainsKey(eventType))
            foreach (var handler in subscribers[eventType])
                if (handler is IWeakEventHandler weakHandler)
                    result.Add(new SubscriberMetadata
                    {
                        MethodName = weakHandler.GetHandlerMethodName(),
                        DeclaringTypeFullName = weakHandler.GetHandlerDeclaringTypeFullName(),
                        DeclaringAssemblyName = weakHandler.GetHandlerDeclaringTypeAssemblyName(),
                        Target = weakHandler.GetTarget(),
                        SubscriberInfo = weakHandler.GetSubscriberInfo(),
                        Order = weakHandler.GetOrder()
                    });

        return result;
    }

    // 设置持久化文件路径（绝对路径）
    public static void SetOrderFilePath(string absolutePath)
    {
        orderFilePathOverride = absolutePath;
        // 下次 EnsureOrderMapLoaded 时将会读取新的路径
        orderMapLoaded = false;
    }

    // 获取持久化文件路径
    private static string GetOrderFilePath()
    {
        // 优先使用覆盖路径
        if (!string.IsNullOrEmpty(orderFilePathOverride))
            return orderFilePathOverride;

#if UNITY_EDITOR
        // 在编辑器中默认将文件放到项目目录下，便于版本控制或手动查看
        try
        {
            var editorPath = Path.Combine(Application.dataPath, "Editor");
            if (!Directory.Exists(editorPath))
                Directory.CreateDirectory(editorPath);
            return Path.Combine(editorPath, "EventSubscriberOrders.json");
        }
        catch
        {
            return Path.Combine(Application.persistentDataPath, "EventSubscriberOrders.json");
        }
#else
        try
        {
            return Path.Combine(Application.persistentDataPath, "EventSubscriberOrders.json");
        }
        catch
        {
            return "EventSubscriberOrders.json";
        }
#endif
    }

    // 确保已加载持久化映射
    private static void EnsureOrderMapLoaded()
    {
        if (orderMapLoaded) return;
        LoadOrderMap();
        orderMapLoaded = true;
    }

    // 从文件加载订阅顺序映射
    private static void LoadOrderMap()
    {
        persistedOrders.Clear();
        nextOrderSequence.Clear();

        var path = GetOrderFilePath();
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var of = JsonUtility.FromJson<OrderFile>(json);
            if (of != null && of.entries != null)
                foreach (var e in of.entries)
                {
                    if (!persistedOrders.TryGetValue(e.EventType, out var map))
                    {
                        map = new Dictionary<string, int>();
                        persistedOrders[e.EventType] = map;
                    }

                    map[e.SubscriberId] = e.Order;
                    // track max order to set next sequence
                    try
                    {
                        var evtType = Type.GetType(e.EventType);
                        if (evtType != null)
                            if (!nextOrderSequence.TryGetValue(evtType, out var cur) || cur <= e.Order)
                                nextOrderSequence[evtType] = e.Order + 1;
                    }
                    catch
                    {
                    }
                }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("加载事件订阅顺序文件失败: " + ex.Message);
        }
    }

    // 将订阅顺序映射保存到文件
    private static void SaveOrderMap()
    {
        try
        {
            var of = new OrderFile();
            foreach (var kv in persistedOrders)
            {
                var evt = kv.Key;
                foreach (var sub in kv.Value)
                    of.entries.Add(new OrderEntry { EventType = evt, SubscriberId = sub.Key, Order = sub.Value });
            }

            var json = JsonUtility.ToJson(of, true);
            var path = GetOrderFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("保存事件订阅顺序文件失败: " + ex.Message);
        }
    }

    // 生成订阅者唯一 ID（用于排序持久化）
    private static string GetSubscriberId(IWeakEventHandler weakHandler)
    {
        if (weakHandler == null) return "(null)";
        var decl = weakHandler.GetHandlerDeclaringTypeFullName() ?? "";
        var method = weakHandler.GetHandlerMethodName() ?? "";
        var target = weakHandler.GetTarget();
        var targetType = target != null ? target.GetType().FullName : "";
        return decl + "|" + method + "|" + targetType;
    }

    // 原子地根据 newOrder 重排指定事件类型的订阅者并保存顺序
    // newOrder: 新列表中每个位置对应旧列表的索引
    public static bool ReorderSubscribers(Type eventType, List<int> newOrder)
    {
        if (eventType == null || newOrder == null)
            return false;

        if (!subscribers.ContainsKey(eventType))
            return false;

        var list = subscribers[eventType];
        if (newOrder.Count != list.Count)
            return false;

        try
        {
            var newList = new List<EventHandlerBase>(list.Count);
            for (var i = 0; i < newOrder.Count; i++)
            {
                var srcIdx = newOrder[i];
                if (srcIdx < 0 || srcIdx >= list.Count)
                    return false; // 索引无效
                newList.Add(list[srcIdx]);
            }

            subscribers[eventType] = newList;
            // 更新统计信息
            subscriptionStats[eventType] = subscribers[eventType].Count;

            // 更新每个 handler 的 Order 并保存到持久化映射
            var eventTypeKey = eventType.FullName;
            if (!persistedOrders.TryGetValue(eventTypeKey, out var map) || map == null)
            {
                map = new Dictionary<string, int>();
                persistedOrders[eventTypeKey] = map;
            }

            for (var i = 0; i < newList.Count; i++)
            {
                var h = newList[i] as IWeakEventHandler;
                if (h != null)
                {
                    h.SetOrder(i);
                    try
                    {
                        var id = GetSubscriberId(h);
                        map[id] = i;
                    }
                    catch
                    {
                    }
                }
            }

            // 更新序列号基准
            nextOrderSequence[eventType] = newList.Count;

            SaveOrderMap();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // 根据各 handler 的 Order 字段对内存列表排序
    public static void ApplyPersistedOrder(Type eventType)
    {
        if (eventType == null) return;
        if (!subscribers.ContainsKey(eventType)) return;

        var list = subscribers[eventType];
        var ordered = list.OrderBy(h =>
        {
            if (h is IWeakEventHandler wh) return wh.GetOrder();
            return int.MaxValue;
        }).ToList();

        subscribers[eventType] = ordered;
        subscriptionStats[eventType] = ordered.Count;
    }

    // 对所有事件类型应用持久化顺序
    public static void ApplyPersistedOrderAll()
    {
        foreach (var eventType in subscribers.Keys.ToList()) ApplyPersistedOrder(eventType);
    }

    // 订阅者元数据 DTO
    public class SubscriberMetadata
    {
        public string MethodName { get; set; }
        public string DeclaringTypeFullName { get; set; }
        public string DeclaringAssemblyName { get; set; }
        public object Target { get; set; }
        public string SubscriberInfo { get; set; }
        public int Order { get; set; }
    }

    // 持久化: 存储 eventType.FullName -> (subscriberId -> order)
    [Serializable]
    private class OrderEntry
    {
        public string EventType;
        public string SubscriberId;
        public int Order;
    }

    [Serializable]
    private class OrderFile
    {
        public List<OrderEntry> entries = new();
    }

    #region 核心事件系统

    private static readonly Dictionary<Type, List<EventHandlerBase>> subscribers = new();

    // 订阅统计（用于调试和可视化）
    private static readonly Dictionary<Type, int> subscriptionStats = new();

    // 订阅事件（弱引用），并维护/恢复订阅顺序
    public static void Subscribe<T>(object subscriber, Action<T> handler) where T : struct
    {
        var eventType = typeof(T);

        if (!subscribers.ContainsKey(eventType))
        {
            subscribers[eventType] = new List<EventHandlerBase>();
            subscriptionStats[eventType] = 0;
        }

        // 创建带弱引用的事件处理器
        var eventHandler = new WeakEventHandler<T>(subscriber, handler);

        // 确保持久化顺序已加载并获取事件类型键
        var eventTypeKey = eventType.FullName;
        EnsureOrderMapLoaded();

        // 根据持久化信息或序列号来分配 Order
        var subscriberId = GetSubscriberId(eventHandler);
        int assignedOrder;
        if (persistedOrders.TryGetValue(eventTypeKey, out var map) && map != null &&
            map.TryGetValue(subscriberId, out assignedOrder))
        {
            // 恢复已有顺序
            eventHandler.SetOrder(assignedOrder);
        }
        else
        {
            // 分配新的顺序号（追加到末尾）
            if (!nextOrderSequence.TryGetValue(eventType, out var seq)) seq = 0;
            assignedOrder = seq;
            eventHandler.SetOrder(assignedOrder);
            nextOrderSequence[eventType] = seq + 1;

            // 保存到持久化地图
            if (!persistedOrders.TryGetValue(eventTypeKey, out var map2) || map2 == null)
            {
                map2 = new Dictionary<string, int>();
                persistedOrders[eventTypeKey] = map2;
            }

            map2[subscriberId] = assignedOrder;
            SaveOrderMap();
        }

        subscribers[eventType].Add(eventHandler);
        subscriptionStats[eventType]++;

        // 应用持久化的顺序（如果有），以保持列表顺序一致
        ApplyPersistedOrder(eventType);

        // 自动清理无效订阅的机制
        CleanupInvalidSubscriptions();
    }

    // 取消对指定事件类型的订阅
    public static void Unsubscribe<T>(object subscriber) where T : struct
    {
        var eventType = typeof(T);

        if (subscribers.ContainsKey(eventType))
        {
            // 在移除 handler 之前，收集将要被移除的订阅者 id，以便从持久化映射中删除
            var toRemoveIds = new List<string>();
            foreach (var handler in subscribers[eventType].ToList())
                if (handler is IWeakEventHandler wh && wh.IsForSubscriber(subscriber))
                    try
                    {
                        toRemoveIds.Add(GetSubscriberId(wh));
                    }
                    catch
                    {
                    }

            var removed = subscribers[eventType].RemoveAll(handler =>
                handler is WeakEventHandler<T> weakHandler && weakHandler.IsForSubscriber(subscriber));

            subscriptionStats[eventType] -= removed;

            // 从持久化映射中移除对应条目并保存
            if (toRemoveIds.Count > 0)
            {
                var key = eventType.FullName;
                if (persistedOrders.TryGetValue(key, out var map) && map != null)
                {
                    foreach (var id in toRemoveIds)
                        if (map.ContainsKey(id))
                            map.Remove(id);
                    SaveOrderMap();
                }
            }
        }
    }

    // 取消订阅者的全部订阅
    public static void UnsubscribeAll(object subscriber)
    {
        // 收集事件类型以避免在遍历时修改集合
        var keys = subscribers.Keys.ToList();
        foreach (var eventType in keys)
        {
            var toRemoveIds = new List<string>();
            foreach (var handler in subscribers[eventType].ToList())
                if (handler is IWeakEventHandler wh && wh.IsForSubscriber(subscriber))
                    try
                    {
                        toRemoveIds.Add(GetSubscriberId(wh));
                    }
                    catch
                    {
                    }

            var removed = subscribers[eventType].RemoveAll(handler =>
                handler is IWeakEventHandler weakHandler && weakHandler.IsForSubscriber(subscriber));

            if (subscriptionStats.ContainsKey(eventType)) subscriptionStats[eventType] -= removed;

            if (toRemoveIds.Count > 0)
            {
                var key = eventType.FullName;
                if (persistedOrders.TryGetValue(key, out var map) && map != null)
                {
                    foreach (var id in toRemoveIds)
                        if (map.ContainsKey(id))
                            map.Remove(id);
                    SaveOrderMap();
                }
            }
        }
    }

    // 发布事件（按 Order 调用订阅者）
    public static void Publish<T>(T eventData) where T : struct
    {
        var eventType = typeof(T);

        if (subscribers.ContainsKey(eventType))
        {
            // 先清理无效订阅
            CleanupInvalidSubscriptions(eventType);

            var handlers = subscribers[eventType];
            // 按 Order 排序后调用（确保持久化排序生效）
            var ordered = handlers
                .Select(h => h as IWeakEventHandler)
                .Where(h => h != null)
                .OrderBy(h => h.GetOrder())
                .ToList();

            foreach (var h in ordered) (h as WeakEventHandler<T>)?.Handle(eventData);
        }
    }

    // 清理无效订阅（可针对单个类型）
    private static void CleanupInvalidSubscriptions(Type specificType = null)
    {
        var typesToCheck = specificType != null ? new List<Type> { specificType } : new List<Type>(subscribers.Keys);

        foreach (var type in typesToCheck)
            if (subscribers.ContainsKey(type))
            {
                subscribers[type].RemoveAll(handler =>
                {
                    if (handler is IWeakEventHandler weakHandler)
                    {
                        var target = weakHandler.GetTarget();
                        // 检查Unity对象是否被销毁
                        if (target is Object unityObj)
                            return unityObj == null;
                        return !weakHandler.IsAlive();
                    }

                    return false;
                });
                subscriptionStats[type] = subscribers[type].Count;
            }
    }

    // 返回订阅统计（会触发一次清理）
    public static Dictionary<Type, int> GetSubscriptionStats()
    {
        CleanupInvalidSubscriptions();
        return new Dictionary<Type, int>(subscriptionStats);
    }

    // 返回指定事件类型的订阅者描述列表
    public static List<string> GetSubscribersForEvent<T>() where T : struct
    {
        var result = new List<string>();
        var eventType = typeof(T);

        if (subscribers.ContainsKey(eventType))
            foreach (var handler in subscribers[eventType])
                if (handler is IWeakEventHandler weakHandler)
                    result.Add(weakHandler.GetSubscriberInfo());

        return result;
    }

    // 返回指定事件类型的订阅者描述列表（非泛型）
    public static List<string> GetSubscribersForEvent(Type eventType)
    {
        var result = new List<string>();

        if (subscribers.ContainsKey(eventType))
            foreach (var handler in subscribers[eventType])
                if (handler is IWeakEventHandler weakHandler)
                    result.Add(weakHandler.GetSubscriberInfo());

        return result;
    }

    // 返回订阅者目标对象（可能为 null）
    public static List<object> GetSubscriberTargetsForEvent(Type eventType)
    {
        var result = new List<object>();

        if (subscribers.ContainsKey(eventType))
            foreach (var handler in subscribers[eventType])
                if (handler is IWeakEventHandler weakHandler)
                    result.Add(weakHandler.GetTarget());

        return result;
    }

    #endregion

    #region 弱引用事件处理器

    public interface IWeakEventHandler
    {
        bool IsAlive();
        bool IsForSubscriber(object subscriber);
        string GetSubscriberInfo();
        object GetTarget();
        string GetHandlerMethodName();
        string GetHandlerDeclaringTypeFullName();
        string GetHandlerDeclaringTypeAssemblyName();
        int GetOrder();
        void SetOrder(int order);
    }

    public abstract class EventHandlerBase
    {
    }

    private class WeakEventHandler<T> : EventHandlerBase, IWeakEventHandler
    {
        private readonly Action<T> handler;
        private readonly string handlerDeclaringTypeAssemblyName;
        private readonly string handlerDeclaringTypeFullName;
        private readonly string handlerMethodName;
        private readonly string subscriberInfo;
        private readonly WeakReference subscriberRef;
        private int order;

        public WeakEventHandler(object subscriber, Action<T> handler)
        {
            subscriberRef = new WeakReference(subscriber);
            this.handler = handler;
            subscriberInfo = $"{subscriber.GetType().Name} . {handler.Method.Name} ()";
            handlerMethodName = handler.Method.Name;
            var decl = handler.Method.DeclaringType;
            handlerDeclaringTypeFullName = decl != null ? decl.FullName : null;
            handlerDeclaringTypeAssemblyName = decl != null ? decl.Assembly.GetName().Name : null;
            order = 0;
        }

        public bool IsAlive()
        {
            return subscriberRef.IsAlive;
        }

        public object GetTarget()
        {
            return subscriberRef.Target;
        }

        public bool IsForSubscriber(object subscriber)
        {
            return subscriberRef.Target == subscriber;
        }

        public string GetSubscriberInfo()
        {
            return subscriberInfo;
        }

        public string GetHandlerMethodName()
        {
            return handlerMethodName;
        }

        public string GetHandlerDeclaringTypeFullName()
        {
            return handlerDeclaringTypeFullName;
        }

        public string GetHandlerDeclaringTypeAssemblyName()
        {
            return handlerDeclaringTypeAssemblyName;
        }

        public int GetOrder()
        {
            return order;
        }

        public void SetOrder(int order)
        {
            this.order = order;
        }

        public void Handle(T eventData)
        {
            if (subscriberRef.IsAlive) handler?.Invoke(eventData);
        }
    }

    #endregion
}