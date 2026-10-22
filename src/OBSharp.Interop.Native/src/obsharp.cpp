#include "obsharp.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QThread>

namespace OBSharp
{
  const char kVersion[] = OBSHARP_VERSION;

  // 获取 OBSharp 版本号
  OBSHARP_API const char* OBSHARP_CALL obsharp_get_version(void) {
    return kVersion;
  }

}