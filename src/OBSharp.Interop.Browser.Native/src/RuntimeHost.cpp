#include "RuntimeHost.h"

#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace obsharp {
namespace {

#if defined(_WIN32)

// libobs 在 Windows 上编成 obs.dll（历史/发行版里偶见 libobs.dll）。
const wchar_t *const kHostModules[] = {L"obs.dll", L"libobs.dll", nullptr};

#elif defined(__APPLE__)

const char *const kHostModules[] = {"libobs.dylib", "obs.dylib", "libobs.framework/libobs", nullptr};

#else // Linux / BSD

const char *const kHostModules[] = {"libobs.so.0", "libobs.so", "obs.so", nullptr};

#endif

} // namespace

void *FindSymbolInModule(void *module, const char *name) {
	if (!module || !name || !*name)
		return nullptr;

#if defined(_WIN32)
	return reinterpret_cast<void *>(GetProcAddress(reinterpret_cast<HMODULE>(module), name));
#else
	return dlsym(module, name);
#endif
}

void *FindLoadedSymbol(const char *name) {
	if (!name || !*name)
		return nullptr;

#if defined(_WIN32)
	for (int i = 0; kHostModules[i] != nullptr; ++i) {
		HMODULE module = GetModuleHandleW(kHostModules[i]);
		if (!module)
			continue;

		if (void *symbol = reinterpret_cast<void *>(GetProcAddress(module, name)))
			return symbol;
	}

	// 兜底：静态链接进主程序的情况。
	if (HMODULE exe = GetModuleHandleW(nullptr))
		return reinterpret_cast<void *>(GetProcAddress(exe, name));

	return nullptr;
#else
	for (int i = 0; kHostModules[i] != nullptr; ++i) {
		// RTLD_NOLOAD：只找已经加载的，绝不因为查找而把 libobs 拉进来。
		void *module = dlopen(kHostModules[i], RTLD_NOW | RTLD_NOLOAD);
		if (!module)
			continue;

		if (void *symbol = dlsym(module, name))
			return symbol;
	}

	// 主程序直接依赖 libobs 时，RTLD_DEFAULT 里能找到它。
	return dlsym(RTLD_DEFAULT, name);
#endif
}

} // namespace obsharp
