# ObSharpDeps.cmake —— 构建 OBSharp.Interop.Native 之前，准备 CEF / Qt6 / SIMDe。
#
# 不写死版本，直接读 lib/obs-studio 的依赖清单：
#   * 版本 / 地址 / SHA-256 / 修订号 ← CMakePresets.json 里名为 "dependencies" 的
#     预设 → vendor["obsproject.com/obs-studio"].dependencies
#   * 压缩包命名模板 / 解压目录名    ← cmake/<平台>/buildspec.cmake
# 占位符替换（VERSION / ARCH / _REVISION）对照
# lib/obs-studio/cmake/common/buildspec_common.cmake 写成，版本随子模块走。
#
# 用法：在主 CMakeLists 里（find_package(CEF / Qt6) 之前）include 一次：
#   include("${CMAKE_CURRENT_SOURCE_DIR}/cmake/ObSharpDeps.cmake")
#
# 可调变量：
#   OBSHARP_FETCH_DEPS  ON/OFF，缺依赖时是否自动下载（默认 ON）
#   OBSHARP_SIMDE_ROOT  已有 SIMDe 源码根目录（里面应包含 simde/ 子目录）
#   OBSHARP_SIMDE_VERSION  自动获取的 SIMDe 版本（默认 v0.8.2）
#   OBSHARP_DEPS_DIR    下载 / 解压的落点（默认 <仓库根>/lib/deps）
#   CEF_ROOT_DIR        已经给了就不再动 CEF
#   CMAKE_PREFIX_PATH   里面已经有 Qt6 就不再下 Qt6
#   QT_HOST_PATH        Windows arm64 交叉编译时指向 x64 那份 Qt6（默认自动下）

include_guard(GLOBAL)

get_filename_component(_obsharp_root "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)

option(OBSHARP_FETCH_DEPS "缺少 CEF / Qt6 时自动下载到 OBSHARP_DEPS_DIR" ON)
set(OBSHARP_DEPS_DIR "${_obsharp_root}/lib/deps" CACHE PATH "OBSharp 原生依赖（CEF / Qt6）的落点")

# ---------------------------------------------------------------------------
# 平台 → 清单里的键名 / 文件名词干
# ---------------------------------------------------------------------------

if(WIN32)
  set(_obsharp_os windows)
elseif(APPLE)
  set(_obsharp_os macos)
else()
  set(_obsharp_os linux)
endif()

# 架构标签（x64 / arm64 / x86）：VS 生成器看 CMAKE_VS_PLATFORM_NAME（跨架构编译也准），
# macOS 看 CMAKE_OSX_ARCHITECTURES，其余（Ninja 等）靠 project() 之后的指针宽度/处理器名判。
if(CMAKE_VS_PLATFORM_NAME)
  if(CMAKE_VS_PLATFORM_NAME STREQUAL "Win32")
    set(_obsharp_arch x86)
  else()
    string(TOLOWER "${CMAKE_VS_PLATFORM_NAME}" _obsharp_arch)
  endif()
elseif(APPLE AND CMAKE_OSX_ARCHITECTURES)
  list(LENGTH CMAKE_OSX_ARCHITECTURES _obsharp_osx_arch_count)
  if(_obsharp_osx_arch_count GREATER 1)
    message(
      FATAL_ERROR
        "CEF 只提供单架构的 macOS 预编译包，撑不起 universal（CMAKE_OSX_ARCHITECTURES=${CMAKE_OSX_ARCHITECTURES}）。"
        "请一次只给一个架构，例如 -DCMAKE_OSX_ARCHITECTURES=arm64。"
    )
  endif()
  if(CMAKE_OSX_ARCHITECTURES STREQUAL "x86_64")
    set(_obsharp_arch x64)
  else()
    set(_obsharp_arch "${CMAKE_OSX_ARCHITECTURES}")
  endif()
elseif(CMAKE_SIZEOF_VOID_P EQUAL 4)
  set(_obsharp_arch x86)
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|ARM64|aarch64")
  set(_obsharp_arch arm64)
else()
  set(_obsharp_arch x64)
endif()

# 给上层（src/OBSharp.Interop.Native）用，省得输出目录那边再判一遍架构。
set(OBSHARP_TARGET_ARCH "${_obsharp_arch}")

# 依赖名 → 清单平台键（如 windows-x64）+ 文件名里 ARCH 的取值（macOS 的 qt6 是 universal）。
# ARCH <arch>：按别的架构解析，而不是当前目标架构。Windows arm64 交叉编译时要把 x64
# 那份 Qt6 也拉下来当 host tools，就靠这个。
function(_obsharp_dep_platform dependency out_key out_arch)
  cmake_parse_arguments(PARSE_ARGV 3 _ODP "" "ARCH" "")

  set(_arch_tag "${_obsharp_arch}")
  if(_ODP_ARCH)
    set(_arch_tag "${_ODP_ARCH}")
  endif()

  if(_obsharp_os STREQUAL "windows")
    set(key "windows-${_arch_tag}")
    set(arch "${_arch_tag}")
  elseif(_obsharp_os STREQUAL "macos")
    if(_arch_tag STREQUAL "x64")
      set(mac x86_64)
    else()
      set(mac "${_arch_tag}")
    endif()
    if(dependency STREQUAL "cef")
      set(key "macos-${mac}")
      set(arch "${mac}")
    else()
      set(key "macos-universal")
      set(arch "universal")
    endif()
  else()
    if(_arch_tag STREQUAL "x64")
      set(lin x86_64)
    elseif(_arch_tag STREQUAL "arm64")
      set(lin aarch64)
    else()
      set(lin "${_arch_tag}")
    endif()
    set(key "ubuntu-${lin}")
    set(arch "${lin}")
  endif()

  set(${out_key} "${key}" PARENT_SCOPE)
  set(${out_arch} "${arch}" PARENT_SCOPE)
endfunction()

# ---------------------------------------------------------------------------
# 读 obs-studio 的清单
# ---------------------------------------------------------------------------

set(_obsharp_manifest "${_obsharp_root}/lib/obs-studio/CMakePresets.json")
if(NOT EXISTS "${_obsharp_manifest}")
  message(
    FATAL_ERROR
      "找不到 ${_obsharp_manifest}。先拉子模块：git submodule update --init lib/obs-studio")
endif()

file(READ "${_obsharp_manifest}" _obsharp_presets)

string(JSON _obsharp_preset_count ERROR_VARIABLE _obsharp_error LENGTH "${_obsharp_presets}"
       configurePresets)
if(_obsharp_error)
  message(FATAL_ERROR "${_obsharp_manifest} 解析失败：${_obsharp_error}")
endif()

set(_obsharp_preset_index -1)
if(_obsharp_preset_count GREATER 0)
  math(EXPR _obsharp_last "${_obsharp_preset_count} - 1")
  foreach(_index RANGE 0 ${_obsharp_last})
    string(JSON _obsharp_name GET "${_obsharp_presets}" configurePresets ${_index} name)
    if(_obsharp_name STREQUAL "dependencies")
      set(_obsharp_preset_index ${_index})
      break()
    endif()
  endforeach()
endif()

if(_obsharp_preset_index LESS 0)
  message(FATAL_ERROR "${_obsharp_manifest} 里没有名为 dependencies 的 configurePresets。")
endif()

set(_obsharp_data_path
    "configurePresets"
    "${_obsharp_preset_index}"
    "vendor"
    "obsproject.com/obs-studio"
    "dependencies")

# 压缩包命名模板：<平台>/buildspec.cmake 里的 set(<依赖>_filename "…") / set(<依赖>_destination "…")。
set(_obsharp_buildspec "${_obsharp_root}/lib/obs-studio/cmake/${_obsharp_os}/buildspec.cmake")
if(EXISTS "${_obsharp_buildspec}")
  file(STRINGS "${_obsharp_buildspec}" _obsharp_template_lines REGEX
       "^[ \t]*set\\([A-Za-z0-9_]+_(filename|destination)[ \t]")
  foreach(_line IN LISTS _obsharp_template_lines)
    string(REGEX MATCH "set\\(([A-Za-z0-9_]+)_(filename|destination)[ \t]+\"([^\"]*)\"" _match
           "${_line}")
    if(_match)
      set("_obsharp_template_${CMAKE_MATCH_1}_${CMAKE_MATCH_2}" "${CMAKE_MATCH_3}")
    endif()
  endforeach()
elseif(_obsharp_os STREQUAL "linux")
  # Linux 没有 buildspec.cmake：Qt6 用发行版包，CEF 命名抄
  # lib/obs-studio/.github/scripts/utils.zsh/setup_ubuntu。
  set(_obsharp_template_cef_filename "cef_binary_VERSION_linux_ARCH_REVISION.tar.xz")
  set(_obsharp_template_cef_destination "cef_binary_VERSION_linux_ARCH")
endif()

# ---------------------------------------------------------------------------
# 解析一个依赖的版本信息 → URL / SHA-256 / 压缩包名 / 解压目录 / 标志文件（不下载）
# ---------------------------------------------------------------------------

function(_obsharp_resolve dependency out_url out_hash out_file out_destination out_sentinel)
  cmake_parse_arguments(PARSE_ARGV 5 _OR "" "ARCH" "")

  if(_OR_ARCH)
    _obsharp_dep_platform("${dependency}" _key _arch ARCH "${_OR_ARCH}")
  else()
    _obsharp_dep_platform("${dependency}" _key _arch)
  endif()

  string(JSON _version GET "${_obsharp_presets}" ${_obsharp_data_path} "${dependency}" version)
  string(JSON _base_url GET "${_obsharp_presets}" ${_obsharp_data_path} "${dependency}" baseUrl)
  string(JSON _label ERROR_VARIABLE _label_error GET "${_obsharp_presets}" ${_obsharp_data_path}
         "${dependency}" label)
  if(_label_error)
    set(_label "${dependency}")
  endif()

  string(JSON _hash ERROR_VARIABLE _hash_error GET "${_obsharp_presets}" ${_obsharp_data_path}
         "${dependency}" hashes "${_key}")
  if(_hash_error)
    message(
      FATAL_ERROR
        "obs-studio 没有为 ${_label} 提供 ${_key} 的预编译包（清单里没有这个哈希）。")
  endif()

  string(JSON _revision ERROR_VARIABLE _revision_error GET "${_obsharp_presets}"
         ${_obsharp_data_path} "${dependency}" revision "${_key}")
  if(_revision_error)
    set(_revision "")
  endif()

  if(NOT DEFINED _obsharp_template_${dependency}_filename)
    message(FATAL_ERROR "没有 ${dependency} 在 ${_obsharp_os} 上的文件名模板。")
  endif()
  if(NOT DEFINED _obsharp_template_${dependency}_destination)
    message(FATAL_ERROR "没有 ${dependency} 在 ${_obsharp_os} 上的解压目录模板。")
  endif()

  string(REPLACE "VERSION" "${_version}" _file "${_obsharp_template_${dependency}_filename}")
  string(REPLACE "ARCH" "${_arch}" _file "${_file}")
  string(REPLACE "VERSION" "${_version}" _destination
         "${_obsharp_template_${dependency}_destination}")
  string(REPLACE "ARCH" "${_arch}" _destination "${_destination}")

  if(_revision)
    string(REPLACE "_REVISION" "_v${_revision}" _file "${_file}")
    string(REPLACE "-REVISION" "-v${_revision}" _file "${_file}")
  else()
    string(REPLACE "_REVISION" "" _file "${_file}")
    string(REPLACE "-REVISION" "" _file "${_file}")
  endif()

  # buildspec_common.cmake：cef 直接从 baseUrl 取，prebuilt / qt6 走 baseUrl/<version>/。
  if(dependency STREQUAL "cef")
    set(_url "${_base_url}/${_file}")
    set(_sentinel "include/cef_version.h") # FindCEF.cmake 也认这个
  else()
    set(_url "${_base_url}/${_version}/${_file}")
    set(_sentinel "lib/cmake/Qt6/Qt6Config.cmake")
  endif()

  set(${out_url} "${_url}" PARENT_SCOPE)
  set(${out_hash} "${_hash}" PARENT_SCOPE)
  set(${out_file} "${_file}" PARENT_SCOPE)
  set(${out_destination} "${_destination}" PARENT_SCOPE)
  set(${out_sentinel} "${_sentinel}" PARENT_SCOPE)
endfunction()

# 依赖版本在本入口统一定义；下载 / 校验 / 解压写在各依赖自己的 .cmake 里。
set(OBSHARP_SIMDE_VERSION "v0.8.2" CACHE STRING "自动获取的 SIMDe Git tag")
set(OBSHARP_SIMDE_ROOT "" CACHE PATH "已有的 SIMDe 源码根目录（包含 simde/ 子目录）")

include("${CMAKE_CURRENT_LIST_DIR}/cef.cmake")
include("${CMAKE_CURRENT_LIST_DIR}/qt6.cmake")
include("${CMAKE_CURRENT_LIST_DIR}/simde.cmake")
