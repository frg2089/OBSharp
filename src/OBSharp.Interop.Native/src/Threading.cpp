#include "Threading.h"

#include "Export.h"
#include "Log.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QMetaObject>
#include <QtCore/QThread>

namespace obsharp {

bool IsUiThread() {
  QCoreApplication* app = QCoreApplication::instance();
  if (!app)
    return false;

  return QThread::currentThread() == app->thread();
}

bool RunOnUiThread(void (*fn)(void* user), void* user) {
  if (!fn)
    return false;

  // 已经在主线程：直调。这里刻意不走事件循环 —— 主线程上如果事件循环还没转起来
  // （或者正卡在别的阻塞调用里），投递出去的任务可能永远不执行，等于自锁。
  if (IsUiThread()) {
    fn(user);
    return true;
  }

  QCoreApplication* app = QCoreApplication::instance();
  if (!app) {
    LogF(OBSHARP_LOG_WARNING, "cannot reach the Qt main thread: the Qt application is gone");
    return false;
  }

  // BlockingQueuedConnection：投递出去并等目标线程跑完才返回。公开 ABI 承诺
  // 「函数返回时 fn 已经跑完」，靠的就是这个连接类型。
  const bool posted =
    QMetaObject::invokeMethod(app, [fn, user]() { fn(user); }, Qt::BlockingQueuedConnection);

  if (!posted)
    LogF(OBSHARP_LOG_WARNING, "failed to post an action to the Qt main thread");

  return posted;
}

} // namespace obsharp
