# SIMDe（header-only；不需要按平台下载不同的二进制包）
#
# 优先使用调用方指定的 OBSHARP_SIMDE_ROOT，其次尝试项目内的常见目录；
# 都不存在时，在 OBSHARP_FETCH_DEPS=ON 的情况下通过 FetchContent 获取固定版本。
# 对外提供 OBSharp::SIMDe 接口目标，使用方 target_link_libraries(... PRIVATE OBSharp::SIMDe)。

set(_obsharp_simde_include_dir "")
set(_obsharp_simde_candidates
    "${OBSHARP_SIMDE_ROOT}"
    "${_obsharp_root}/lib/simde"
    "${_obsharp_root}/third_party/simde"
    "${OBSHARP_DEPS_DIR}/simde"
    "${OBSHARP_DEPS_DIR}/simde-${OBSHARP_SIMDE_VERSION}"
)

foreach(_candidate IN LISTS _obsharp_simde_candidates)
  if(_candidate AND EXISTS "${_candidate}/simde/simde-common.h")
    set(_obsharp_simde_include_dir "${_candidate}")
    break()
  endif()
endforeach()

if(NOT _obsharp_simde_include_dir)
  if(NOT OBSHARP_FETCH_DEPS)
    message(
      FATAL_ERROR
        "找不到 SIMDe。请设置 OBSHARP_SIMDE_ROOT 指向包含 simde/ 子目录的源码根目录，"
        "或启用 OBSHARP_FETCH_DEPS 以自动获取 SIMDe。"
    )
  endif()

  include(FetchContent)
  FetchContent_Declare(
    obsharp_simde
    GIT_REPOSITORY https://github.com/simd-everywhere/simde.git
    GIT_TAG "${OBSHARP_SIMDE_VERSION}"
    GIT_SHALLOW TRUE
  )
  FetchContent_GetProperties(obsharp_simde)
  if(NOT obsharp_simde_POPULATED)
    FetchContent_Populate(obsharp_simde)
  endif()

  set(_obsharp_simde_include_dir "${obsharp_simde_SOURCE_DIR}")
  if(NOT EXISTS "${_obsharp_simde_include_dir}/simde/simde-common.h")
    message(FATAL_ERROR "获取 SIMDe 后没有找到 simde/simde-common.h：${_obsharp_simde_include_dir}")
  endif()
endif()

if(NOT TARGET OBSharp::SIMDe)
  add_library(obsharp_simde INTERFACE)
  add_library(OBSharp::SIMDe ALIAS obsharp_simde)
  target_include_directories(obsharp_simde SYSTEM INTERFACE "${_obsharp_simde_include_dir}")
endif()

message(STATUS "OBSharp: SIMDe include → ${_obsharp_simde_include_dir} (version ${OBSHARP_SIMDE_VERSION})")
