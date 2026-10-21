# OBSharp.Interop.Native：OB# 的纯 C ABI 转发层。
#
# 这里**没有业务逻辑**，只有三样东西（详见 include/OBSharp.Interop.Native.h 顶部的
# 设计边界说明）：
#   1) 一行式转发（Qt / CEF 的值语义操作）；
#   2) C++ 虚接口必不可少的零判断适配器（CefResourceHandler / QObject 信号 /
#      eventFilter / closeEvent）；
#   3) C 语言做不到的胶水（可变参数 blog()、模块符号查找、线程封送）。
#
# 依赖：只找 Qt6::Widgets 和 CEF。刻意不链接 libobs —— blog() 是运行时从已经加载的
# 模块里取符号（见 src/RuntimeHost.cpp）。URL 映射 / host 校验 / HTTP 组包 /
# scheme 注册去重全在 C#。
#
# 单独配置（不走仓库根 CMakeLists）时至少要给：
#   cmake -S src/OBSharp.Interop.Native -B out/build/native
# 缺 CEF / Qt6 会自动下到 <仓库根>/lib/deps（见仓库根的 cmake/ObSharpDeps.cmake）。

cmake_minimum_required(VERSION 3.20)

# 仓库根。版本文件在根上，必须在 project() 之前读，才能把版本交给 project(VERSION)。
get_filename_component(OBSHARP_REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../.." ABSOLUTE)

# 版本号由 CI 的「生成版本文件」步骤写到仓库根，跟托管侧的 Directory.Version.props
# 用同一份 GitVersion 输出；本地没有这个文件时用占位版本，保证随时能配置。
#
#   OBSHARP_VERSION          完整 semVer（进 obsharp_version() 和文件属性）
#   OBSHARP_VERSION_NUMERIC  major.minor.patch（给 project(VERSION) / Mach-O）
#   OBSHARP_VERSION_FILE     四段数字（Windows 的 FILEVERSION）
set(OBSHARP_VERSION "0.0.0-local")
set(OBSHARP_VERSION_NUMERIC "0.0.0")
set(OBSHARP_VERSION_FILE "0.0.0.0")
include("${OBSHARP_REPO_ROOT}/Directory.Version.cmake" OPTIONAL)

project(OBSharp.Interop.Native VERSION "${OBSHARP_VERSION_NUMERIC}" LANGUAGES CXX)

# Windows 版本资源（src/version.rc.in）需要 RC 语言。放在 add_library 之前启用。
if(WIN32)
  enable_language(RC)
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC)
  # 跟 obs-browser 保持一致：CEF 的 cef_dll_wrapper 是拿静态 CRT 编的，混用 /MD
  # 的话跨 DLL 传 STL 对象 / 释放对方内存会踩坏堆。让 CMake 直接发 /MT、/MTd，
  # 而不是在它默认的 /MD 后面再叠一个（那样会有 D9025 警告）。
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

# CefWidgetSignals.h 里的 Q_OBJECT（接收 QCefWidget 的 titleChanged / urlChanged）
# 需要 moc。
set(CMAKE_AUTOMOC ON)

# 输出目录：out/native/<arch>/<config>/OBSharp.Interop.Native.dll（或 .so/.dylib）。
set(OBSHARP_NATIVE_OUTPUT_DIR
    "${OBSHARP_REPO_ROOT}/out/native"
    CACHE PATH "OBSharp.Interop.Native 的输出目录")

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
  OBSharp.Interop.Native SHARED
  include/OBSharp.Interop.Native.h
  src/Core.cpp
  src/CefWidgetSignals.h
  src/Export.h
  src/FileDialog.cpp
  src/Log.cpp
  src/Log.h
  src/QtCefBridge.cpp
  src/QtCefBridge.h
  src/QtSupport.h
  src/RuntimeHost.cpp
  src/RuntimeHost.h
  src/SchemeHandler.cpp
  src/SchemeHandler.h
  src/Threading.cpp
  src/Threading.h
  src/Widget.cpp
  src/Window.cpp)

# 加 OBSHARP_NATIVE_BUILD 让公开头用 dllexport，否则 Windows 上啥都导不出来。
# 版本串由上面的 Directory.Version.cmake 注入，源码里不写死版本号。
target_compile_definitions(
  OBSharp.Interop.Native
  PRIVATE OBSHARP_NATIVE_BUILD=1
          "OBSHARP_NATIVE_VERSION=\"${OBSHARP_VERSION}\"")

target_include_directories(
  OBSharp.Interop.Native
  PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include" "${CMAKE_CURRENT_SOURCE_DIR}/src")

target_link_libraries(OBSharp.Interop.Native PRIVATE Qt6::Widgets CEF::Wrapper CEF::Library)

# 文件名不动它：Windows 上 CMake 默认不加前缀（OBSharp.Interop.Native.dll），Unix 上
# 默认加 lib（libOBSharp.Interop.Native.so / .dylib）。后者是托管侧认的名字——
# NativeLibraryLoader 在 Unix 上找 lib 前缀那份，OBSharp.Interop.csproj 复制原生库时
# 也按同名复制，所以别设 PREFIX ""。
set_target_properties(
  OBSharp.Interop.Native
  PROPERTIES OUTPUT_NAME "OBSharp.Interop.Native"
             RUNTIME_OUTPUT_DIRECTORY "${OBSHARP_NATIVE_OUTPUT_DIR}/${OBSHARP_NATIVE_ARCH}/$<CONFIG>"
             LIBRARY_OUTPUT_DIRECTORY "${OBSHARP_NATIVE_OUTPUT_DIR}/${OBSHARP_NATIVE_ARCH}/$<CONFIG>")

# Mach-O 的 current_version / compatibility_version 也跟 GitVersion 对齐。
# Linux 上不设 VERSION / SOVERSION：那会把文件名变成 .so.1，托管侧按固定名字找库。
if(APPLE)
  if(OBSHARP_VERSION_NUMERIC MATCHES "^([0-9]+)\\.")
    set(OBSHARP_VERSION_COMPAT "${CMAKE_MATCH_1}.0.0")
  else()
    set(OBSHARP_VERSION_COMPAT "0.0.0")
  endif()

  set_target_properties(
    OBSharp.Interop.Native
    PROPERTIES MACHO_CURRENT_VERSION "${OBSHARP_VERSION_NUMERIC}"
               MACHO_COMPATIBILITY_VERSION "${OBSHARP_VERSION_COMPAT}")
endif()

# Windows 的版本资源：FILEVERSION / ProductVersion 等从同一份版本文件来。
# rc.exe 的 FILEVERSION 要四段用逗号分隔的数字。
if(WIN32)
  if(OBSHARP_VERSION_FILE MATCHES "^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$")
    string(REPLACE "." "," OBSHARP_VERSION_RC "${OBSHARP_VERSION_FILE}")
  else()
    set(OBSHARP_VERSION_RC "0,0,0,0")
  endif()

  configure_file(
    "${CMAKE_CURRENT_SOURCE_DIR}/src/version.rc.in"
    "${CMAKE_CURRENT_BINARY_DIR}/version.rc"
    @ONLY)

  target_sources(OBSharp.Interop.Native PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/version.rc")
endif()

if(UNIX AND NOT APPLE)
  # Linux 上不要任何 DT_RPATH / DT_RUNPATH。
  #
  # CEF::Library 是 IMPORTED 目标，位置在 <CEF_ROOT_DIR>/Release/libcef.so，那不在系统
  # 的隐式链接目录里，CMake 为了「构建树里的产物也能直接跑」会把这条路径写进 RUNPATH。
  # 于是 CI 编出来的库带着 /home/runner/work/... 这种构建机路径，换台机器 ldd 就先去找
  # 一个不存在的目录。
  #
  # 运行时不靠 runpath 指 CEF：本库只会被 dlopen 进 OBS 进程，那时候 obs-browser 早就
  # 把 libcef.so 按 SONAME 加载好了（发行版包的 obs-browser 自己带 RUNPATH
  # /usr/lib64/cef/），dlopen 本库时动态链接器会复用已加载的那份。INSTALL_RPATH 留空是
  # 为了以后 cmake --install 别再塞回来。只关 rpath 生成，SONAME / NEEDED / 导出符号
  # 都不受影响。
  set_target_properties(
    OBSharp.Interop.Native
    PROPERTIES SKIP_BUILD_RPATH TRUE
               BUILD_WITH_INSTALL_RPATH FALSE
               INSTALL_RPATH "")
endif()

if(MSVC)
  # 源码是 UTF-8（无 BOM），注释里有中文，不告诉 MSVC 就会按本地代码页解释。
  #
  # 可复现构建：/Brepro 用内容哈希代替时间戳，/pathmap 把源码绝对路径映射掉。
  # Debug 下 VS 默认开增量链接 + EditAndContinue，/Brepro 跟它们不合（.obj 不写
  # mtime）；发布产物（CI 只编 Release）才需要可复现，所以只在非 Debug 加。
  target_compile_options(
    OBSharp.Interop.Native
    PRIVATE /utf-8
            "$<$<NOT:$<CONFIG:Debug>>:/Brepro>"
            "/pathmap:${OBSHARP_REPO_ROOT}=.")
  target_link_options(
    OBSharp.Interop.Native
    PRIVATE "$<$<NOT:$<CONFIG:Debug>>:/Brepro>"
            /PDBALTPATH:%_PDB%)
elseif(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
  # 去掉 __FILE__ / 调试信息里的绝对路径，换个构建目录不改变产物。
  target_compile_options(
    OBSharp.Interop.Native
    PRIVATE "-ffile-prefix-map=${OBSHARP_REPO_ROOT}=."
            "-fdebug-prefix-map=${OBSHARP_REPO_ROOT}=.")
endif()

if(WIN32)
  # 静态 CRT（/MT、/MTd）在上面用 CMAKE_MSVC_RUNTIME_LIBRARY 设好了。
  target_link_options(OBSharp.Interop.Native PRIVATE /IGNORE:4099)

  # 静态链进 cef_dll_wrapper 之后，这些东西的符号得有人提供。
  target_link_libraries(
    OBSharp.Interop.Native
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

message(STATUS "OBSharp.Interop.Native: CEF ${CEF_VERSION}, output ${OBSHARP_NATIVE_OUTPUT_DIR}")
