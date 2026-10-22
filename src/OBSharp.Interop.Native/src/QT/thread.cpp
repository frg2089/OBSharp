#include "thread.h"

#include "logger.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QMetaObject>
#include <QtCore/QThread>

namespace OBSharp {
  bool obsharp_is_ui_thread(void) {
    auto const app = QCoreApplication::instance();
    return app && app->thread() == QThread::currentThread();
  }

  bool obsharp_ui_invoke(obsharp_ui_invoke_fn fn, void* ptr) {
    if (!fn)
      return false;

    auto const app = QCoreApplication::instance();
    if (!app) {
      Log(OBSHARP_LOG_WARNING, "QCoreApplication instance is not available.");
      return false;
    }

    if (app->thread() == QThread::currentThread()) {
      fn(ptr);
      return true;
    }

    // BlockingQueuedConnection：一直等到 fn 在主线程上跑完才返回。
    //
    // 调用方普遍假设「这个函数返回时事情已经办完」（比如调完就去读共享状态），
    // 用 QueuedConnection 会让它变成「已经排上队」，两者的返回时机完全不同。
    // 死锁不成问题：上面已经把「本来就在主线程」的情况直接放行了。
    auto const posted = QMetaObject::invokeMethod(app, [fn, ptr]() {
      fn(ptr);
      }, Qt::BlockingQueuedConnection);

    if (!posted)
      Log(OBSHARP_LOG_WARNING, "failed to post an action to the Qt main thread");

    return posted;
  }
}