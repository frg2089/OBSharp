/* Umbrella header used by scripts/Generate-PInvoke.ps1.
 *
 * Why this exists: parsing libobs/obs.h directly makes obs.h the clang "main
 * file", and clang does NOT honor #pragma once for the main file. Because many
 * libobs headers (#include "obs.h") are reachable from obs.h, that leads to
 * every enum/struct being defined twice and the parse failing outright.
 *
 * Instead we make THIS file the main file and pull the public headers in
 * explicitly. We also list extra public headers that obs.h does not reach so a
 * single ClangSharp pass can bind all of libobs at once. */
#pragma once

/* Core (obs.h transitively pulls util/, graphics/, media-io/, callback/). */
#include <obs.h>

/* obs-scene.h references pthread_mutex_t but does not include threading.h
 * itself (obs-scene.c includes it first). Pull threading in before it. */
#include <util/threading.h>

/* Public headers that obs.h does NOT include. */
#include <obs-module.h>
#include <obs-scene.h>
#include <obs-missing-files.h>

/* Codec / NAL helpers. */
#include <obs-nal.h>
#include <obs-avc.h>
#include <obs-hevc.h>
#include <obs-av1.h>

/* Media IO extras not reached from obs.h. */
#include <media-io/audio-resampler.h>
#include <media-io/video-scaler.h>
#include <media-io/media-remux.h>
#include <media-io/format-conversion.h>

/* Utility helpers worth binding. */
#include <util/config-file.h>
#include <util/platform.h>
#include <util/threading.h>
#include <util/dstr.h>
#include <util/utf8.h>
#include <util/serializer.h>
#include <util/pipe.h>
#include <util/lexer.h>
#include <util/text-lookup.h>
#include <util/profiler.h>
#include <util/source-profiler.h>
#include <util/util_uint128.h>
#include <util/crc32.h>
#include <util/bitstream.h>
