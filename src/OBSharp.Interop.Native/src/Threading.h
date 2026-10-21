#pragma once

// Threading.h：Qt 主线程判定与封送。
//
// 这一层存在的唯一理由是「C# 碰不到 Qt 的线程模型」：
//   * Qt 要求所有 QWidget 操作都在主线程（GUI 线程）上；
//   * C# 回调可能从任意 .NET 线程来（线程池 / Task 延续）；
//   * 所以需要一个「判定 + 切过去并等它跑完」的胶水。
//
// 「什么时候该切」是 C# 的判断：托管侧 NativeApi.InvokeUI 会先问
// obsharp_is_ui_thread，是主线程就直调，不是才投递。这里只提供机制。

namespace obsharp {

// 当前线程是不是 Qt 主线程。
//
// 主线程取自 QCoreApplication::instance()->thread()：没有 Qt 应用时返回 false
// （进程还没到那一步，或者正在退出）。
bool IsUiThread();

// 在 Qt 主线程上同步执行 fn：已经在主线程时直接执行，否则用
// Qt::BlockingQueuedConnection 投递并等它跑完。
//
// 返回 false 表示没跑成（fn 为空、或者从非主线程调用时 Qt 应用已经不在了 /
// 事件循环没在跑）。
//
// 注意：fn 内部**不要**再反向调用会阻塞等待调用方线程的东西，否则自锁 ——
// 调用方线程此刻正卡在 BlockingQueuedConnection 上等 fn 返回。
bool RunOnUiThread(void (*fn)(void* user), void* user);

} // namespace obsharp
