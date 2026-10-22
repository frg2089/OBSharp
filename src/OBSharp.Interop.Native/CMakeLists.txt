cmake_minimum_required(VERSION 3.20)

# CMP0141（3.25+）决定 /Z7、/Zi 这类调试格式开关从哪来：OLD 行为把它们塞进
# CMAKE_<LANG>_FLAGS_<CONFIG>（Debug 默认就是 /Zi），并完全无视下面的
# CMAKE_MSVC_DEBUG_INFORMATION_FORMAT 抽象。必须在第一次 project() 之前定成 NEW，
# 否则 /Zi 会悄悄回来（共享编译器 PDB，破坏可复现构建）。
if(POLICY CMP0141)
  cmake_policy(SET CMP0141 NEW)
endif()

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
  #   * /Zi 的共享编译器 PDB 会被编译器并发写、路径还会烧进 obj，
  #     让 obj / DLL / PDB 每次都不同（哪怕源码一致）。
  # /Z7 把调试信息放进 .obj：obj 保持逐字节可复现，调试符号由链接器汇总进 PDB
  # （见下面的 /DEBUG:FULL，Debug / Release 都产出），一点不少。
  set(CMAKE_MSVC_DEBUG_INFORMATION_FORMAT "Embedded")
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

  if(NOT POLICY CMP0141)
    # CMake < 3.25 没有 MSVC_DEBUG_INFORMATION_FORMAT 这层抽象，调试格式开关
    # 直接躺在 CMAKE_<LANG>_FLAGS_<CONFIG> 里（Debug 默认 /Zi）。手工统一成 /Z7。
    foreach(_obsharp_config DEBUG MINSIZEREL RELEASE RELWITHDEBINFO)
      string(REGEX REPLACE "/Z[iI7]" "" _obsharp_cxx_flags
                           "${CMAKE_CXX_FLAGS_${_obsharp_config}}")
      string(STRIP "${_obsharp_cxx_flags}" _obsharp_cxx_flags)
      set(CMAKE_CXX_FLAGS_${_obsharp_config} "${_obsharp_cxx_flags} /Z7")
    endforeach()
  endif()
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
  # 目标级再钉一次 /Z7：根 CMakeLists 为了热重载把 MSVC_DEBUG_INFORMATION_FORMAT
  # 设成 EditAndContinue（/ZI），这里明确覆盖，不让 /ZI 漏进本目标。
  set_target_properties(
    OBSharp.Interop.Native
    PROPERTIES
      MSVC_DEBUG_INFORMATION_FORMAT "Embedded"
  )

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

  # 调试符号：/Z7 的 obj（含调试信息）+ /DEBUG:FULL → 链接器汇总出完整 PDB，
  # 落在 DLL 旁边，Debug / Release 都产出（裸 /DEBUG 等价 /DEBUG:FULL，
  # VS 2026 已删除 /DEBUG:FASTLINK，别用）。
  # /DEBUG 会把 /OPT 默认值翻成 NOREF / NOICF，所以非 Debug 配置显式写回
  # /OPT:REF /OPT:ICF（必须排在 /DEBUG 之后），保持原来 Release 的优化程度。
  # /PDBALTPATH:%_PDB% 让 DLL 里只嵌 PDB 文件名（不带构建机路径），配合 /Brepro
  # DLL 依旧逐字节可复现；PDB 只承诺符号完整、可随产物分发，不承诺跨机器一致。
  target_link_options(
    OBSharp.Interop.Native
    PRIVATE
      /DEBUG:FULL
      $<$<NOT:$<CONFIG:Debug>>:/OPT:REF>
      $<$<NOT:$<CONFIG:Debug>>:/OPT:ICF>
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
