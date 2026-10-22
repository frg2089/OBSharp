cmake_minimum_required(VERSION 3.20)

# ---------------------------------------------------------------------------
# Project version
# Directory.Version.cmake 由 CI 生成；本地缺失时使用占位版本。
# ---------------------------------------------------------------------------

get_filename_component(
  OBSHARP_REPO_ROOT
  "${CMAKE_CURRENT_SOURCE_DIR}/../.."
  ABSOLUTE
)

set(OBSHARP_VERSION "0.0.0-local")
set(OBSHARP_VERSION_NUMERIC "0.0.0")
set(OBSHARP_VERSION_FILE "0.0.0.0")
include("${OBSHARP_REPO_ROOT}/Directory.Version.cmake" OPTIONAL)

project(
  OBSharp.Interop.Native
  VERSION "${OBSHARP_VERSION_NUMERIC}"
  LANGUAGES CXX
)

if(WIN32)
  enable_language(RC)
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# CEF/widgetsignals.h 中的 Q_OBJECT 需要 moc。
set(CMAKE_AUTOMOC ON)

# cef_dll_wrapper 使用静态 CRT；保持与 obs-browser 的配置一致。
# 不要在默认 /MD 后追加 /MT，否则会产生 D9025 冲突警告。
if(MSVC)
  # Deterministic 的硬要求：
  #   * /ZI（EditAndContinue）与 /Brepro、/experimental:deterministic 互斥
  #     （D8016），所以 Debug 也不能用它；
  #   * /Zi 的共享编译器 PDB 会让链接器产出的 PDB 每次都不同（哪怕 DLL 一致）。
  # /Z7 把调试信息放进 .obj，链接器直接产出 PDB —— DLL 和 PDB 都可复现。
  set(CMAKE_MSVC_DEBUG_INFORMATION_FORMAT "Embedded")
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

# ---------------------------------------------------------------------------
# Dependencies
# ---------------------------------------------------------------------------

# 从仓库根目录构建时可能已经 include；单独配置本目录时也需要它。
include("${OBSHARP_REPO_ROOT}/cmake/ObSharpDeps.cmake")

find_package(Qt6 REQUIRED COMPONENTS Widgets)

# 复用 obs-studio 的 FindCEF.cmake，与 obs-browser 使用相同的 CEF_ROOT_DIR。
if(NOT TARGET CEF::Library)
  list(APPEND CMAKE_MODULE_PATH
    "${OBSHARP_REPO_ROOT}/lib/obs-studio/cmake/finders"
  )
  find_package(CEF REQUIRED)
endif()

# ---------------------------------------------------------------------------
# Output layout
# ---------------------------------------------------------------------------

if(OBSHARP_TARGET_ARCH)
  # ObSharpDeps.cmake 已按生成器 / 目标平台计算架构。
  set(OBSHARP_NATIVE_ARCH "${OBSHARP_TARGET_ARCH}")
elseif(CMAKE_SIZEOF_VOID_P EQUAL 8)
  set(OBSHARP_NATIVE_ARCH "x64")
else()
  set(OBSHARP_NATIVE_ARCH "x86")
endif()

set(
  OBSHARP_NATIVE_OUTPUT_DIR
  "${OBSHARP_REPO_ROOT}/out/native"
  CACHE PATH "OBSharp.Interop.Native 的输出目录"
)

# ---------------------------------------------------------------------------
# Native library
# ---------------------------------------------------------------------------

add_library(
  OBSharp.Interop.Native
  SHARED
  src/host.cpp
  src/host.h
  src/logger.cpp
  src/logger.h
  src/obsharp.cpp
  src/obsharp.h
  src/CEF/qcef.cpp
  src/CEF/qcef.h
  src/CEF/scheme.cpp
  src/CEF/scheme.h
  src/CEF/widgetsignals.h
  src/QT/application.cpp
  src/QT/application.h
  src/QT/filepicker.cpp
  src/QT/filepicker.h
  src/QT/qt.h
  src/QT/thread.cpp
  src/QT/thread.h
  src/QT/widget.cpp
  src/QT/widget.h
  src/QT/window.cpp
  src/QT/window.h
)

target_compile_definitions(
  OBSharp.Interop.Native
  PRIVATE
    "OBSHARP_VERSION=\"${OBSHARP_VERSION}\""
)

# panel/browser-panel.hpp 直接引用 obs-browser 的面板接口；它 include 的
# obs-module.h / util/util.hpp 来自 libobs。只是头文件引用，不需要链 libobs ——
# 本库不调用 obs 的任何函数（browser-panel.hpp 里那几个 static inline 帮手不用）。
#
# libobs 的 obsconfig.h 是 obs-studio 构建期的生成物，源码树里只有 obsconfig.h.in；
# obs-config.h 又非要它不可。这里按 obs-studio 的 Windows 默认值生成一份最小的，
# 纯粹为了编译通过 —— 本库不调用 libobs，这些值不会被用到。
set(OBS_DATA_PATH "../../data")
set(OBS_PLUGIN_PATH "../../lib/obs-plugins/64bit")
set(OBS_PLUGIN_DESTINATION "obs-plugins/64bit")
set(OBS_RELEASE_CANDIDATE 0)
set(OBS_BETA 0)
configure_file(
  "${OBSHARP_REPO_ROOT}/lib/obs-studio/libobs/obsconfig.h.in"
  "${CMAKE_CURRENT_BINARY_DIR}/include/obsconfig.h"
  @ONLY
)

target_include_directories(
  OBSharp.Interop.Native
  PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_BINARY_DIR}/include"
    "${OBSHARP_REPO_ROOT}/lib/obs-browser/panel"
    "${OBSHARP_REPO_ROOT}/lib/obs-studio/libobs"
)

target_link_libraries(
  OBSharp.Interop.Native
  PRIVATE
    Qt6::Widgets
    CEF::Wrapper
    CEF::Library
)

# Windows 不加 lib 前缀；Unix 保留默认 lib 前缀，供托管侧加载器使用。
set_target_properties(
  OBSharp.Interop.Native
  PROPERTIES
    OUTPUT_NAME "OBSharp.Interop.Native"
    RUNTIME_OUTPUT_DIRECTORY "${OBSHARP_NATIVE_OUTPUT_DIR}/${OBSHARP_NATIVE_ARCH}/$<CONFIG>"
    LIBRARY_OUTPUT_DIRECTORY "${OBSHARP_NATIVE_OUTPUT_DIR}/${OBSHARP_NATIVE_ARCH}/$<CONFIG>"
)

# ---------------------------------------------------------------------------
# Platform-specific settings
# ---------------------------------------------------------------------------

if(WIN32)
  # Windows 版本资源。
  if(OBSHARP_VERSION_FILE MATCHES "^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$")
    string(REPLACE "." "," OBSHARP_VERSION_RC "${OBSHARP_VERSION_FILE}")
  else()
    set(OBSHARP_VERSION_RC "0,0,0,0")
  endif()

  configure_file(
    "${CMAKE_CURRENT_SOURCE_DIR}/src/version.rc.in"
    "${CMAKE_CURRENT_BINARY_DIR}/version.rc"
    @ONLY
  )

  target_sources(
    OBSharp.Interop.Native
    PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/version.rc"
  )

  # cef_dll_wrapper 静态链接后需要的 Windows 系统库。
  target_link_libraries(
    OBSharp.Interop.Native
    PRIVATE
      comctl32
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
      ws2_32
  )

  target_link_options(OBSharp.Interop.Native PRIVATE /IGNORE:4099)

elseif(APPLE)
  # Mach-O 版本号与 GitVersion 生成的版本保持一致。
  if(OBSHARP_VERSION_NUMERIC MATCHES "^([0-9]+)\\.")
    set(OBSHARP_VERSION_COMPAT "${CMAKE_MATCH_1}.0.0")
  else()
    set(OBSHARP_VERSION_COMPAT "0.0.0")
  endif()

  set_target_properties(
    OBSharp.Interop.Native
    PROPERTIES
      MACHO_CURRENT_VERSION "${OBSHARP_VERSION_NUMERIC}"
      MACHO_COMPATIBILITY_VERSION "${OBSHARP_VERSION_COMPAT}"
  )

elseif(UNIX)
  # 不把构建机上的 CEF 路径写入 DT_RPATH / DT_RUNPATH。
  # 本库由 OBS 进程加载；预期 obs-browser 已加载同 SONAME 的 libcef.so。
  set_target_properties(
    OBSharp.Interop.Native
    PROPERTIES
      SKIP_BUILD_RPATH TRUE
      BUILD_WITH_INSTALL_RPATH FALSE
      INSTALL_RPATH ""
  )
endif()

# ---------------------------------------------------------------------------
# Reproducible build / compiler options
# ---------------------------------------------------------------------------

if(MSVC)
  # 源码为 UTF-8；所有配置都做可复现构建（Deterministic）：
  #   /Brepro + /experimental:deterministic —— 时间戳换成内容哈希；
  #   /pathmap —— 不把构建机的绝对路径烧进调试信息；
  #   /INCREMENTAL:NO —— 增量链接会盖掉 /Brepro。
  target_compile_options(
    OBSharp.Interop.Native
    PRIVATE
      /utf-8
      /Brepro
      /experimental:deterministic
      "/pathmap:${OBSHARP_REPO_ROOT}=."
  )

  target_link_options(
    OBSharp.Interop.Native
    PRIVATE
      /Brepro
      /INCREMENTAL:NO
      /PDBALTPATH:%_PDB%
  )

elseif(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
  # 避免将仓库绝对路径写入 __FILE__ 和调试信息。
  target_compile_options(
    OBSharp.Interop.Native
    PRIVATE
      "-ffile-prefix-map=${OBSHARP_REPO_ROOT}=."
      "-fdebug-prefix-map=${OBSHARP_REPO_ROOT}=."
  )
endif()

message(
  STATUS
  "OBSharp.Interop.Native: CEF ${CEF_VERSION}, output ${OBSHARP_NATIVE_OUTPUT_DIR}"
)
