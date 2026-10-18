# ObSharpDeps.cmake —— 构建 OBSharp.Interop.Browser.Native 之前，把 CEF / Qt6 拉到本地。
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

# 给上层（src/OBSharp.Interop.Browser.Native）用，省得输出目录那边再判一遍架构。
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
# 解析一个依赖 → URL / SHA-256 / 压缩包名 / 解压目录 / 标志文件
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

# ---------------------------------------------------------------------------
# 下载 + 校验 + 解压
# ---------------------------------------------------------------------------

function(_obsharp_ensure dependency out_dir)
  cmake_parse_arguments(PARSE_ARGV 2 _OE "" "ARCH" "")

  if(_OE_ARCH)
    _obsharp_resolve("${dependency}" _url _hash _file _destination _sentinel ARCH "${_OE_ARCH}")
  else()
    _obsharp_resolve("${dependency}" _url _hash _file _destination _sentinel)
  endif()

  set(_destination_dir "${OBSHARP_DEPS_DIR}/${_destination}")
  if(EXISTS "${_destination_dir}/${_sentinel}")
    message(STATUS "OBSharp: ${dependency} 已就绪（${_destination_dir}）")
    set(${out_dir} "${_destination_dir}" PARENT_SCOPE)
    return()
  endif()

  if(NOT OBSHARP_FETCH_DEPS)
    message(
      FATAL_ERROR
        "缺少 ${dependency}（${_destination_dir}）。\n"
        "把 OBSHARP_FETCH_DEPS 设成 ON 允许自动下载，或用 CEF_ROOT_DIR / CMAKE_PREFIX_PATH 指定现成的。"
    )
  endif()

  file(MAKE_DIRECTORY "${OBSHARP_DEPS_DIR}")
  set(_archive "${OBSHARP_DEPS_DIR}/${_file}")

  set(_need_download TRUE)
  if(EXISTS "${_archive}")
    file(SHA256 "${_archive}" _actual_hash)
    string(TOLOWER "${_actual_hash}" _actual_hash)
    string(TOLOWER "${_hash}" _expected_hash)
    if(_actual_hash STREQUAL _expected_hash)
      set(_need_download FALSE)
      message(STATUS "OBSharp: ${_file} 已在缓存里，跳过下载")
    endif()
  endif()

  if(_need_download)
    message(STATUS "OBSharp: 下载 ${_url}")
    # SHOW_PROGRESS 从 4.0 起是默认行为，旧版本才需要这个开关。
    set(_obsharp_download_args STATUS _download_status TLS_VERIFY ON)
    if(CMAKE_VERSION VERSION_LESS 4.0)
      list(APPEND _obsharp_download_args SHOW_PROGRESS)
    endif()
    file(DOWNLOAD "${_url}" "${_archive}" ${_obsharp_download_args})
    list(GET _download_status 0 _download_code)
    if(NOT _download_code EQUAL 0)
      list(GET _download_status 1 _download_message)
      file(REMOVE "${_archive}")
      message(FATAL_ERROR "下载 ${_url} 失败：${_download_message}")
    endif()

    file(SHA256 "${_archive}" _actual_hash)
    string(TOLOWER "${_actual_hash}" _actual_hash)
    string(TOLOWER "${_hash}" _expected_hash)
    if(NOT _actual_hash STREQUAL _expected_hash)
      file(REMOVE "${_archive}")
      message(FATAL_ERROR "${_file} 校验失败：期望 SHA-256 ${_hash}，实际 ${_actual_hash}。已删除。")
    endif()
  endif()

  message(STATUS "OBSharp: 解压 ${_file} → ${_destination_dir}")
  # 先解到临时目录，再按「标志文件落在第几层」决定要不要把外面那层搬上来：
  # Windows 的 CEF zip、以及 obs-deps 的三个平台包都是平的；Linux 的 CEF tar.xz
  # 多包了一层（obs-studio 自己的 setup_ubuntu 是用 tar --strip-components 1 解的）。
  set(_unpack_dir "${_destination_dir}.unpack")
  file(REMOVE_RECURSE "${_unpack_dir}")
  file(ARCHIVE_EXTRACT INPUT "${_archive}" DESTINATION "${_unpack_dir}")

  if(EXISTS "${_unpack_dir}/${_sentinel}")
    set(_payload "${_unpack_dir}")
  else()
    file(GLOB _nested RELATIVE "${_unpack_dir}" "${_unpack_dir}/*/${_sentinel}")
    list(LENGTH _nested _nested_count)
    if(NOT _nested_count EQUAL 1)
      message(
        FATAL_ERROR
          "解压后没在 ${_unpack_dir} 里找到 ${_sentinel}（找到 ${_nested_count} 个候选）；"
          "obs-studio 的包结构可能变了。"
      )
    endif()
    list(GET _nested 0 _nested_first)
    # 形如 cef_binary_.../include/cef_version.h，取第一段就是外层目录名。
    string(REGEX REPLACE "/.*$" "" _nested_root "${_nested_first}")
    set(_payload "${_unpack_dir}/${_nested_root}")
  endif()

  file(REMOVE_RECURSE "${_destination_dir}")
  file(RENAME "${_payload}" "${_destination_dir}")
  file(REMOVE_RECURSE "${_unpack_dir}")

  if(NOT EXISTS "${_destination_dir}/${_sentinel}")
    message(
      FATAL_ERROR
        "解压后没找到 ${_destination_dir}/${_sentinel}；obs-studio 的包结构可能变了。")
  endif()

  set(${out_dir} "${_destination_dir}" PARENT_SCOPE)
endfunction()

# ---------------------------------------------------------------------------
# 编排
# ---------------------------------------------------------------------------

# CEF：CEF_ROOT_DIR 指向的目录里真有一份 CEF 才不动（不能用「变量有没有值」判断，
# 因为这个缓存变量本身就是本模块写的，重启 configure 时也是有值的）。
if(CEF_ROOT_DIR AND EXISTS "${CEF_ROOT_DIR}/include/cef_version.h")
  message(STATUS "OBSharp: 用已有的 CEF_ROOT_DIR=${CEF_ROOT_DIR}")
else()
  if(CEF_ROOT_DIR)
    message(STATUS "OBSharp: CEF_ROOT_DIR=${CEF_ROOT_DIR} 里没有 CEF，改用 lib/deps")
  endif()
  _obsharp_ensure(cef _obsharp_cef_dir)
  set(CEF_ROOT_DIR "${_obsharp_cef_dir}" CACHE PATH "CEF 根目录（由 cmake/ObSharpDeps.cmake 设置）" FORCE)
endif()

# Qt6：CMAKE_PREFIX_PATH 里已经有就跳过；Linux 用发行版包。
if(_obsharp_os STREQUAL "linux")
  message(STATUS "OBSharp: Linux 的 Qt6 用发行版包（qt6-base-dev 之类）")
else()
  set(_obsharp_have_qt6 FALSE)
  foreach(_prefix IN LISTS CMAKE_PREFIX_PATH)
    if(EXISTS "${_prefix}/lib/cmake/Qt6/Qt6Config.cmake")
      set(_obsharp_have_qt6 TRUE)
      break()
    endif()
  endforeach()

  if(_obsharp_have_qt6)
    message(STATUS "OBSharp: 用 CMAKE_PREFIX_PATH 里已有的 Qt6")
  else()
    _obsharp_ensure(qt6 _obsharp_qt6_dir)
    list(APPEND CMAKE_PREFIX_PATH "${_obsharp_qt6_dir}")
    list(REMOVE_DUPLICATES CMAKE_PREFIX_PATH)

    # Windows arm64 是在 x64 机器上交叉编译的：目标那份 Qt6 的 moc / rcc / uic 是 arm64
    # 的可执行文件，跑不动。CMake 靠 QT_HOST_PATH 找能在 host 上跑的那份（obs-studio 的
    # _handle_qt_cross_compile 就是这么干的，我们照抄：目标架构用 ARM64 的包，
    # host tools 用 x64 的包，两个包版本必须一致）。
    if(_obsharp_os STREQUAL "windows" AND _obsharp_arch STREQUAL "arm64")
      _obsharp_ensure(qt6 _obsharp_qt6_host_dir ARCH x64)
      set(QT_HOST_PATH
          "${_obsharp_qt6_host_dir}"
          CACHE PATH "Qt6 host tools（Windows arm64 交叉编译用）" FORCE)
      # 让 Qt6 的 CMake 走 host info 检查（跨架构时必须的，见 Qt6Dependencies.cmake）。
      set(QT_REQUIRE_HOST_PATH_CHECK
          TRUE
          CACHE STRING "Qt Host Tools Check Required"
          FORCE)
      message(STATUS "OBSharp: Qt6 host tools（x64）→ ${_obsharp_qt6_host_dir}")
    endif()
  endif()

  # 自己给了 Qt6、又要交叉编译 arm64 的，host tools 得自己指（我们不去覆盖用户的设置）。
  if(_obsharp_have_qt6
     AND _obsharp_os STREQUAL "windows"
     AND _obsharp_arch STREQUAL "arm64"
     AND NOT DEFINED QT_HOST_PATH)
    message(
      STATUS
        "OBSharp: Windows arm64 交叉编译需要 x64 的 Qt6 host tools，记得设 QT_HOST_PATH（moc / rcc / uic 跑不动 arm64 的那份）"
    )
  endif()
endif()
