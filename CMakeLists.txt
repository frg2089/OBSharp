# 顶层 CMake 项目：只负责把原生子项目拉进来。
#
# 构建原生库需要 CEF 和 OBS 用的那份 Qt6。缺了的话 cmake/ObSharpDeps.cmake 会按
# obs-studio 自己的依赖清单自动下到 lib/deps/，所以通常不用手填路径；想用现成的
# 就照旧给 CEF_ROOT_DIR / CMAKE_PREFIX_PATH。
#
# 例：
#   cmake --preset win-x64
#   cmake --build --preset win-x64-release

cmake_minimum_required(VERSION 3.20)

# 如果支持，请为 MSVC 编译器启用热重载。
if(POLICY CMP0141)
  cmake_policy(SET CMP0141 NEW)
  set(CMAKE_MSVC_DEBUG_INFORMATION_FORMAT
      "$<IF:$<AND:$<C_COMPILER_ID:MSVC>,$<CXX_COMPILER_ID:MSVC>>,$<$<CONFIG:Debug,RelWithDebInfo>:EditAndContinue>,$<$<CONFIG:Debug,RelWithDebInfo>:ProgramDatabase>>")
endif()

project("OBSharp" LANGUAGES C CXX)

# 缺 CEF / Qt6 就按 obs-studio 的清单下到 lib/deps（版本随 lib/obs-studio 走）。
include("${CMAKE_CURRENT_SOURCE_DIR}/cmake/ObSharpDeps.cmake")

add_subdirectory("src/OBSharp.Interop.Native")
