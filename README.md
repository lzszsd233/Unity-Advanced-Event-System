# Unity Event Drive System with Visual Editor

这是一个为 Unity 开发的事件分发系统，主要用于解耦游戏各模块之间的业务逻辑。系统在底层处理了 Unity 场景对象生命周期引起的内存残留问题，支持事件执行顺序的持久化配置，并包含一个用于运行时监测和调试的编辑器窗口。

---

## 解决的实际问题

* **场景对象销毁后的隐式内存泄漏**：当一个 `MonoBehaviour` 脚本订阅了全局静态事件后被销毁（`Destroy`），如果未手动取消订阅，C# 托管层引用依然存活，导致垃圾回收器（GC）无法释放内存。同时，当事件再次触发时，会因为访问已销毁的 C++ 底层对象而抛出 `NullReferenceException`（即 Unity 的 Fake Null 现象）。
* **事件分发的无序性与时序冲突**：Unity 原生的事件或委托分发无法显式约束多个订阅者的执行先后顺序。在处理如状态机切换、UI 与数据层同步等对时序敏感的业务时，乱序执行极易引发难以复现的逻辑 Bug。
* **运行时状态的黑盒调试成本**：传统的事件系统在项目运行时缺乏直观的观测手段，无法确切获知当前特定事件被哪些对象的哪些方法所订阅，给断点排查和逻辑梳理带来困难。

---

## 核心实现原理

### 弱引用管理与内存安全
系统核心存储采用 `WeakReference` 持有订阅者目标实例，避免事件管理器对业务对象产生强引用。
在发布事件（`Publish`）与执行垃圾清理（`CleanupInvalidSubscriptions`）时，系统不仅检查标准 C# 弱引用的存活状态（`IsAlive`），还会针对 Unity 引擎特性，显式进行 `target is Object unityObj && unityObj == null` 判定。这样可以准确识别出在 Unity 场景中已被销毁但托管层仍残存的“伪空”对象。系统在每次执行订阅和发布时会惰性触发列表扫描，自动剔除这些失效引用，业务层即使漏写反注册代码也不会引发内存泄漏。

### 事件执行顺序持久化
为了保障事件分发的确定性，系统引入了基于本地 JSON 配置文件的优先级调度机制。
在订阅阶段，系统通过反射提取订阅者方法的声明类全名（Declaring Type Full Name）、方法名以及所属程序集名，组合生成唯一的字符串 `SubscriberId` 作为持久化索引。系统在初始化时会读取项目目录下的 `EventSubscriberOrders.json` 配置文件。在分发事件前，根据配置中的 `Order` 权重对订阅者列表执行排序，确保事件严格按照预设的时序依次调用。

### 编辑器源码追踪定位
配套的 Editor 工具集成了源码层面的文件流扫描定位功能。
当开发者在编辑器窗口中请求查看某个订阅方法的源码时，后台逻辑会逐行读取对应的 C# 脚本文件。为了防止干扰，系统内部实现了一个简易的状态机，在读取文件流时自动识别并过滤单行注释（`//`）与块注释（`/* ... */`）。随后，系统采用严格匹配（标准方法声明签名）与宽松匹配（方法名调用级正则）的双重正则算法计算目标代码行号，最终调用 `AssetDatabase.OpenAsset` 唤醒外部 IDE 并将光标直接定位到具体的代码行。

---

## 使用示例

### 1. 定义事件数据结构
建议将事件定义为 `struct`，以避免频繁广播事件时在托管堆产生不必要的 GC Memory 碎片：

```csharp
namespace EventsNamespace
{
    public struct PlayerHurtEvent
    {
        public int PlayerId;
        public float DamageAmount;
        public string AttackerName;
    }
}
```

### 2. 订阅与反订阅
业务类无需继承任何特定基类，直接传入自身实例与回调函数即可：

```csharp
using UnityEngine;
using EventsNamespace;

public class UIManager : MonoBehaviour
{
    private void OnEnable()
    {
        // 订阅事件，底层自动检索并应用历史时序配置
        EventManager.Subscribe<PlayerHurtEvent>(this, OnPlayerHurt);
    }

    private void OnDisable()
    {
        // 主动反注册。若忘记编写此函数，底层的弱引用机制会进行隐式清理
        EventManager.Unsubscribe<PlayerHurtEvent>(this);
    }

    private void OnPlayerHurt(PlayerHurtEvent evt)
    {
        Debug.Log($"UI 收到通知: 玩家 {evt.PlayerId} 受到 {evt.DamageAmount} 点伤害。");
    }
}
```

### 3. 发布事件

```csharp
using UnityEngine;
using EventsNamespace;

public class EnemyAI : MonoBehaviour
{
    public void ExecuteAttack()
    {
        var data = new PlayerHurtEvent
        {
            PlayerId = 0,
            DamageAmount = 25.0f,
            AllocatedName = "Orc_Warrior"
        };

        // 广播事件，订阅者将按 JSON 配置文件中的顺序被调用
        EventManager.Publish(data);
    }
}
```

---

## 编辑器窗口功能

通过 Unity 菜单栏 `Tools -> 事件系统监视器` 即可打开配套的调试窗口：

1. **运行时订阅树状视图**：自动检索指定命名空间下的所有事件类型，以折叠树的形式渲染当前运行时的全部订阅关系，直观展示方法名称、类全称、所属程序集。
2. **基于 ReorderableList 的优先级调整**：支持在 UI 上直接通过鼠标拖拽订阅者来改变其调用顺序，松开鼠标后系统自动原子化重排，并实时回写至本地的 JSON 配置文件。
3. **场景对象定位 (Ping)**：对于继承自 `UnityEngine.Object` 的订阅者，点击面板上的定位按钮，可在 Hierarchy 层级窗口中高亮选中对应的场景实例对象。
4. **源码行一键跳转**：点击打开按钮，后台自动启动文件流扫描并过滤注释，直接唤醒外部 IDE（如 VS 或 Rider）并将光标精确定位到对应的 C# 处理方法行。

---

## 性能与兼容性

* **GC Alloc**：事件传参采用结构体，且内部核心容器支持大小预分配，在持续发布事件期间实现 0 托管堆分配。
* **IO 损耗**：持久化写入配置文件引入了变更触发判定，仅在编辑器内手动调整顺序时触发磁盘 IO。运行时环境纯基于内存字典检索，无磁盘开销。
* **环境要求**：兼容 Unity 2021.3 LTS 及以上版本，完整支持 IL2CPP 编译后端。
