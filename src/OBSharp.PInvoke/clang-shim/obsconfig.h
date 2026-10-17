/* Shim replacement for the CMake-generated obsconfig.h.
 * libobs/obs-config.h does `#include "obsconfig.h"`, which only exists after a
 * CMake configure. ClangSharp only needs these macros to parse the headers, so
 * we provide plausible Windows defaults. This file is NOT shipped to runtime. */
#pragma once

#define OBS_DATA_PATH "data"
#define OBS_PLUGIN_PATH "obs-plugins"
#define OBS_PLUGIN_DESTINATION "obs-plugins/64bit"

#define OBS_RELEASE_CANDIDATE 0
#define OBS_BETA 0
