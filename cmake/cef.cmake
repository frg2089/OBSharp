# CEF：版本 / 地址 / 哈希由 ObSharpDeps.cmake 解析，本文件负责下载、校验、解压。
#
# CEF_ROOT_DIR 指向的目录里真有一份 CEF 才不动（不能用「变量有没有值」判断，
# 因为这个缓存变量本身就是本模块写的，重启 configure 时也是有值的）。
if(CEF_ROOT_DIR AND EXISTS "${CEF_ROOT_DIR}/include/cef_version.h")
  message(STATUS "OBSharp: 用已有的 CEF_ROOT_DIR=${CEF_ROOT_DIR}")
  return()
endif()

if(CEF_ROOT_DIR)
  message(STATUS "OBSharp: CEF_ROOT_DIR=${CEF_ROOT_DIR} 里没有 CEF，改用 lib/deps")
endif()

_obsharp_resolve(cef _cef_url _cef_hash _cef_file _cef_destination _cef_sentinel)
set(_cef_dir "${OBSHARP_DEPS_DIR}/${_cef_destination}")

if(NOT EXISTS "${_cef_dir}/${_cef_sentinel}")
  if(NOT OBSHARP_FETCH_DEPS)
    message(
      FATAL_ERROR
        "缺少 CEF（${_cef_dir}）。\n"
        "把 OBSHARP_FETCH_DEPS 设成 ON 允许自动下载，或用 CEF_ROOT_DIR 指定现成的。")
  endif()

  file(MAKE_DIRECTORY "${OBSHARP_DEPS_DIR}")
  set(_cef_archive "${OBSHARP_DEPS_DIR}/${_cef_file}")
  string(TOLOWER "${_cef_hash}" _cef_expected_hash)

  set(_cef_need_download TRUE)
  if(EXISTS "${_cef_archive}")
    file(SHA256 "${_cef_archive}" _cef_actual_hash)
    string(TOLOWER "${_cef_actual_hash}" _cef_actual_hash)
    if(_cef_actual_hash STREQUAL _cef_expected_hash)
      set(_cef_need_download FALSE)
      message(STATUS "OBSharp: ${_cef_file} 已在缓存里，跳过下载")
    endif()
  endif()

  if(_cef_need_download)
    message(STATUS "OBSharp: 下载 ${_cef_url}")
    # SHOW_PROGRESS 从 4.0 起是默认行为，旧版本才需要这个开关。
    set(_cef_download_args STATUS _cef_download_status TLS_VERIFY ON)
    if(CMAKE_VERSION VERSION_LESS 4.0)
      list(APPEND _cef_download_args SHOW_PROGRESS)
    endif()
    file(DOWNLOAD "${_cef_url}" "${_cef_archive}" ${_cef_download_args})
    list(GET _cef_download_status 0 _cef_download_code)
    if(NOT _cef_download_code EQUAL 0)
      list(GET _cef_download_status 1 _cef_download_message)
      file(REMOVE "${_cef_archive}")
      message(FATAL_ERROR "下载 ${_cef_url} 失败：${_cef_download_message}")
    endif()

    file(SHA256 "${_cef_archive}" _cef_actual_hash)
    string(TOLOWER "${_cef_actual_hash}" _cef_actual_hash)
    if(NOT _cef_actual_hash STREQUAL _cef_expected_hash)
      file(REMOVE "${_cef_archive}")
      message(
        FATAL_ERROR
          "${_cef_file} 校验失败：期望 SHA-256 ${_cef_hash}，实际 ${_cef_actual_hash}。已删除。")
    endif()
  endif()

  message(STATUS "OBSharp: 解压 ${_cef_file} → ${_cef_dir}")
  # Windows 的 CEF zip 是平的；Linux 的 tar.xz 多包了一层
  # （obs-studio 自己的 setup_ubuntu 是用 tar --strip-components 1 解的）。
  set(_cef_unpack_dir "${_cef_dir}.unpack")
  file(REMOVE_RECURSE "${_cef_unpack_dir}")
  file(ARCHIVE_EXTRACT INPUT "${_cef_archive}" DESTINATION "${_cef_unpack_dir}")

  if(EXISTS "${_cef_unpack_dir}/${_cef_sentinel}")
    set(_cef_payload "${_cef_unpack_dir}")
  else()
    file(GLOB _cef_nested RELATIVE "${_cef_unpack_dir}" "${_cef_unpack_dir}/*/${_cef_sentinel}")
    list(LENGTH _cef_nested _cef_nested_count)
    if(NOT _cef_nested_count EQUAL 1)
      message(
        FATAL_ERROR
          "解压后没在 ${_cef_unpack_dir} 里找到 ${_cef_sentinel}（找到 ${_cef_nested_count} 个候选）；"
          "obs-studio 的包结构可能变了。")
    endif()
    list(GET _cef_nested 0 _cef_nested_first)
    string(REGEX REPLACE "/.*$" "" _cef_nested_root "${_cef_nested_first}")
    set(_cef_payload "${_cef_unpack_dir}/${_cef_nested_root}")
  endif()

  file(REMOVE_RECURSE "${_cef_dir}")
  file(RENAME "${_cef_payload}" "${_cef_dir}")
  file(REMOVE_RECURSE "${_cef_unpack_dir}")

  if(NOT EXISTS "${_cef_dir}/${_cef_sentinel}")
    message(FATAL_ERROR "解压后没找到 ${_cef_dir}/${_cef_sentinel}；obs-studio 的包结构可能变了。")
  endif()
else()
  message(STATUS "OBSharp: cef 已就绪（${_cef_dir}）")
endif()

set(CEF_ROOT_DIR "${_cef_dir}" CACHE PATH "CEF 根目录（由 cmake/cef.cmake 设置）" FORCE)
