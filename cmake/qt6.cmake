# Qt6：版本 / 地址 / 哈希由 ObSharpDeps.cmake 解析，本文件负责下载、校验、解压。
# CMAKE_PREFIX_PATH 里已经有就跳过；Linux 用发行版包。
if(_obsharp_os STREQUAL "linux")
  message(STATUS "OBSharp: Linux 的 Qt6 用发行版包（qt6-base-dev 之类）")
  return()
endif()

set(_qt6_have FALSE)
foreach(_prefix IN LISTS CMAKE_PREFIX_PATH)
  if(EXISTS "${_prefix}/lib/cmake/Qt6/Qt6Config.cmake")
    set(_qt6_have TRUE)
    break()
  endif()
endforeach()

if(_qt6_have)
  message(STATUS "OBSharp: 用 CMAKE_PREFIX_PATH 里已有的 Qt6")

  # 自己给了 Qt6、又要交叉编译 arm64 的，host tools 得自己指（我们不去覆盖用户的设置）。
  if(_obsharp_os STREQUAL "windows" AND _obsharp_arch STREQUAL "arm64" AND NOT DEFINED QT_HOST_PATH)
    message(
      STATUS
        "OBSharp: Windows arm64 交叉编译需要 x64 的 Qt6 host tools，记得设 QT_HOST_PATH（moc / rcc / uic 跑不动 arm64 的那份）"
    )
  endif()
  return()
endif()

# Windows arm64 是在 x64 机器上交叉编译的：目标那份 Qt6 的 moc / rcc / uic 是 arm64
# 的可执行文件，跑不动。CMake 靠 QT_HOST_PATH 找能在 host 上跑的那份（obs-studio 的
# _handle_qt_cross_compile 就是这么干的，我们照抄：目标架构用 ARM64 的包，
# host tools 用 x64 的包，两个包版本必须一致）。
set(_qt6_arches "${_obsharp_arch}")
if(_obsharp_os STREQUAL "windows" AND _obsharp_arch STREQUAL "arm64")
  list(APPEND _qt6_arches x64)
endif()

foreach(_qt6_arch IN LISTS _qt6_arches)
  _obsharp_resolve(qt6 _qt6_url _qt6_hash _qt6_file _qt6_destination _qt6_sentinel
                   ARCH "${_qt6_arch}")
  set(_qt6_dir "${OBSHARP_DEPS_DIR}/${_qt6_destination}")

  if(NOT EXISTS "${_qt6_dir}/${_qt6_sentinel}")
    if(NOT OBSHARP_FETCH_DEPS)
      message(
        FATAL_ERROR
          "缺少 Qt6（${_qt6_dir}）。\n"
          "把 OBSHARP_FETCH_DEPS 设成 ON 允许自动下载，或用 CMAKE_PREFIX_PATH 指定现成的。")
    endif()

    file(MAKE_DIRECTORY "${OBSHARP_DEPS_DIR}")
    set(_qt6_archive "${OBSHARP_DEPS_DIR}/${_qt6_file}")
    string(TOLOWER "${_qt6_hash}" _qt6_expected_hash)

    set(_qt6_need_download TRUE)
    if(EXISTS "${_qt6_archive}")
      file(SHA256 "${_qt6_archive}" _qt6_actual_hash)
      string(TOLOWER "${_qt6_actual_hash}" _qt6_actual_hash)
      if(_qt6_actual_hash STREQUAL _qt6_expected_hash)
        set(_qt6_need_download FALSE)
        message(STATUS "OBSharp: ${_qt6_file} 已在缓存里，跳过下载")
      endif()
    endif()

    if(_qt6_need_download)
      message(STATUS "OBSharp: 下载 ${_qt6_url}")
      # SHOW_PROGRESS 从 4.0 起是默认行为，旧版本才需要这个开关。
      set(_qt6_download_args STATUS _qt6_download_status TLS_VERIFY ON)
      if(CMAKE_VERSION VERSION_LESS 4.0)
        list(APPEND _qt6_download_args SHOW_PROGRESS)
      endif()
      file(DOWNLOAD "${_qt6_url}" "${_qt6_archive}" ${_qt6_download_args})
      list(GET _qt6_download_status 0 _qt6_download_code)
      if(NOT _qt6_download_code EQUAL 0)
        list(GET _qt6_download_status 1 _qt6_download_message)
        file(REMOVE "${_qt6_archive}")
        message(FATAL_ERROR "下载 ${_qt6_url} 失败：${_qt6_download_message}")
      endif()

      file(SHA256 "${_qt6_archive}" _qt6_actual_hash)
      string(TOLOWER "${_qt6_actual_hash}" _qt6_actual_hash)
      if(NOT _qt6_actual_hash STREQUAL _qt6_expected_hash)
        file(REMOVE "${_qt6_archive}")
        message(
          FATAL_ERROR
            "${_qt6_file} 校验失败：期望 SHA-256 ${_qt6_hash}，实际 ${_qt6_actual_hash}。已删除。")
      endif()
    endif()

    message(STATUS "OBSharp: 解压 ${_qt6_file} → ${_qt6_dir}")
    # obs-deps 的包是平的，先解到临时目录再整体改名。
    set(_qt6_unpack_dir "${_qt6_dir}.unpack")
    file(REMOVE_RECURSE "${_qt6_unpack_dir}")
    file(ARCHIVE_EXTRACT INPUT "${_qt6_archive}" DESTINATION "${_qt6_unpack_dir}")
    if(NOT EXISTS "${_qt6_unpack_dir}/${_qt6_sentinel}")
      message(FATAL_ERROR "解压后没在 ${_qt6_unpack_dir} 里找到 ${_qt6_sentinel}；obs-studio 的包结构可能变了。")
    endif()
    file(REMOVE_RECURSE "${_qt6_dir}")
    file(RENAME "${_qt6_unpack_dir}" "${_qt6_dir}")
  else()
    message(STATUS "OBSharp: qt6 已就绪（${_qt6_dir}）")
  endif()

  if(_qt6_arch STREQUAL _obsharp_arch)
    list(APPEND CMAKE_PREFIX_PATH "${_qt6_dir}")
    list(REMOVE_DUPLICATES CMAKE_PREFIX_PATH)
  else()
    set(QT_HOST_PATH "${_qt6_dir}" CACHE PATH "Qt6 host tools（Windows arm64 交叉编译用）" FORCE)
    # 让 Qt6 的 CMake 走 host info 检查（跨架构时必须的，见 Qt6Dependencies.cmake）。
    set(QT_REQUIRE_HOST_PATH_CHECK TRUE CACHE STRING "Qt Host Tools Check Required" FORCE)
    message(STATUS "OBSharp: Qt6 host tools（x64）→ ${_qt6_dir}")
  endif()
endforeach()
