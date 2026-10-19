# OBSharp.Interop.Browser.Native: 复用 obs-browser 已经初始化好的 CEF、OBS 自带的 Qt6，
# 产出 OB# 宿主（OBS 进程）里可以直接 P/Invoke 的原生动态库。
#
# 依赖：只找 Qt6::Widgets 和 CEF。刻意不链接 libobs —— blog /
# obs_get_module / obs_get_module_lib / obs_get_nix_platform 都是运行时从已经
# 加载的模块里取符号（见 src/RuntimeHost.cpp）。
#
# 单独配置（不走仓库根 CMakeLists）时至少要给：
#   cmake -S src/OBSharp.Interop.Browser.Native -B out/build/native
# 缺 CEF / Qt6 会自动下到 <仓库根>/lib/deps（见仓库根的 cmake/ObSharpDeps.cmake）。

cmake_minimum_required(VERSION 3.20)

project(OBSharp.Interop.Browser.Native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC)
  # 跟 obs-browser 保持一致：CEF 的 cef_dll_wrapper 是拿静态 CRT 编的，混用 /MD
  # 的话跨 DLL 传 std::string / 释放内存会踩坏堆。让 CMake 直接发 /MT、/MTd，
  # 而不是在它默认的 /MD 后面再叠一个（那样会有 D9025 警告）。
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

# Q_OBJECT（BrowserEventSink）需要 moc。
set(CMAKE_AUTOMOC ON)

get_filename_component(OBSHARP_REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../.." ABSOLUTE)

# 输出目录：out/native/<arch>/<config>/OBSharp.Interop.Browser.Native.dll（或 .so/.dylib）。
set(OBSHARP_NATIVE_OUTPUT_DIR
    "${OBSHARP_REPO_ROOT}/out/native"
    CACHE PATH "OBSharp.Interop.Browser.Native 的输出目录")

# 缺 CEF / Qt6 就按 obs-studio 的清单下到 lib/deps（从仓库根构建时已经 include 过，
# include_guard 会跳过；写在这里是为了单独配置本目录时也能自动准备）。
include("${OBSHARP_REPO_ROOT}/cmake/ObSharpDeps.cmake")

# 输出目录里的架构标签用 ObSharpDeps.cmake 算好的那份：VS 生成器看
# CMAKE_VS_PLATFORM_NAME（跨架构编译也准），macOS 看 CMAKE_OSX_ARCHITECTURES，
# 其余靠指针宽度 / 处理器名。没拿到就退回按指针宽度判。
if(OBSHARP_TARGET_ARCH)
  set(OBSHARP_NATIVE_ARCH "${OBSHARP_TARGET_ARCH}")
elseif(CMAKE_SIZEOF_VOID_P EQUAL 8)
  set(OBSHARP_NATIVE_ARCH "x64")
else()
  set(OBSHARP_NATIVE_ARCH "x86")
endif()

find_package(Qt6 REQUIRED COMPONENTS Widgets)

# 复用 obs-studio 自带的 FindCEF.cmake，参数（CEF_ROOT_DIR）跟 obs-browser 完全一致。
if(NOT TARGET CEF::Library)
  list(APPEND CMAKE_MODULE_PATH "${OBSHARP_REPO_ROOT}/lib/obs-studio/cmake/finders")
  find_package(CEF REQUIRED)
endif()

add_library(
  OBSharp.Interop.Browser.Native SHARED
  include/OBSharp.Interop.Browser.Native.h
  src/Export.h
  src/BrowserWidget.h
  src/BrowserWidget.cpp
  src/HostApi.h
  src/HostApi.cpp
  src/KestrelClient.h
  src/KestrelClient.cpp
  src/Log.h
  src/Log.cpp
  src/OBSharp.Interop.Browser.Native.cpp
  src/RuntimeHost.h
  src/RuntimeHost.cpp
  src/SchemeHandler.h
  src/SchemeHandler.cpp
  src/Window.h
  src/Window.cpp)

# 加 OBSHARP_BROWSER_NATIVE_BUILD 让公开头用 dllexport，否则 Windows 上啥都导不出来。
target_compile_definitions(OBSharp.Interop.Browser.Native PRIVATE OBSHARP_BROWSER_NATIVE_BUILD=1)

target_include_directories(
  OBSharp.Interop.Browser.Native
  PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include" "${CMAKE_CURRENT_SOURCE_DIR}/src")

target_link_libraries(OBSharp.Interop.Browser.Native PRIVATE Qt6::Widgets CEF::Wrapper CEF::Library)

# 文件名不动它：Windows 上 CMake 默认不加前缀（OBSharp.Interop.Browser.Native.dll），Unix 上
# 默认加 lib（libOBSharp.Interop.Browser.Native.so / .dylib）。后者是托管侧认的名字——
# NativeLibraryLoader 在 Unix 上找 lib 前缀那份，OBSharp.Interop.csproj 复制原生库时
# 也按同名复制，所以别设 PREFIX ""。
set_target_properties(
  OBSharp.Interop.Browser.Native
  PROPERTIES OUTPUT_NAME "OBSharp.Interop.Browser.Native"
             RUNTIME_OUTPUT_DIRECTORY "${OBSHARP_NATIVE_OUTPUT_DIR}/${OBSHARP_NATIVE_ARCH}/$<CONFIG>"
             LIBRARY_OUTPUT_DIRECTORY "${OBSHARP_NATIVE_OUTPUT_DIR}/${OBSHARP_NATIVE_ARCH}/$<CONFIG>")

if(MSVC)
  # 源码是 UTF-8（无 BOM），注释里有中文，不告诉 MSVC 就会按本地代码页解释。
  target_compile_options(OBSharp.Interop.Browser.Native PRIVATE /utf-8)
endif()

if(WIN32)
  # 静态 CRT（/MT、/MTd）在上面用 CMAKE_MSVC_RUNTIME_LIBRARY 设好了。
  target_link_options(OBSharp.Interop.Browser.Native PRIVATE /IGNORE:4099)

  # 静态链进 cef_dll_wrapper 之后，这些东西的符号得有人提供。
  target_link_libraries(
    OBSharp.Interop.Browser.Native
    PRIVATE comctl32
            gdi32
            imm32
            ole32
            oleaut32
            rpcrt4
            shell32
            shlwapi
            user32
            userenv
            uuid
            version
            winmm
            winspool
            ws2_32)
endif()

message(STATUS "OBSharp.Interop.Browser.Native: CEF ${CEF_VERSION}, output ${OBSHARP_NATIVE_OUTPUT_DIR}")
