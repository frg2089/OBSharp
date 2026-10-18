#include "HostApi.h"

#include "RuntimeHost.h"

#include <cstdio>

namespace obsharp {

QCef::~QCef() {}

namespace {

// obs_get_module / obs_get_module_lib 的签名（libobs 稳定 C API）。
// 我们自己声明，避免引入 obs-module.h / obsconfig.h 那一大串依赖。
using ObsGetModuleFn = void *(*)(const char *name);
using ObsGetModuleLibFn = void *(*)(void *module);

#if !defined(_WIN32) && !defined(__APPLE__)
// enum obs_nix_platform_type { INVALID=0, X11_EGL=1, WAYLAND=2 };
using ObsGetNixPlatformFn = int (*)();
constexpr int kObsNixPlatformWayland = 2;
#endif

using CreateQCefFn = QCef *(*)();
using QCefVersionFn = int (*)();

std::string Describe(const char *what) {
	return std::string(what);
}

} // namespace

bool IsBrowserPanelSupported(std::string &reason) {
#if !defined(_WIN32) && !defined(__APPLE__)
	auto getPlatform = reinterpret_cast<ObsGetNixPlatformFn>(FindLoadedSymbol("obs_get_nix_platform"));
	if (getPlatform && getPlatform() == kObsNixPlatformWayland) {
		reason = "browser panels are disabled by obs-browser on Wayland";
		return false;
	}
#endif

	reason.clear();
	return true;
}

bool LoadBrowserPanel(BrowserPanel &panel, std::string &error) {
	if (panel.IsValid())
		return true;

	std::string unsupported;
	if (!IsBrowserPanelSupported(unsupported)) {
		error = unsupported;
		return false;
	}

	auto getModule = reinterpret_cast<ObsGetModuleFn>(FindLoadedSymbol("obs_get_module"));
	auto getModuleLib = reinterpret_cast<ObsGetModuleLibFn>(FindLoadedSymbol("obs_get_module_lib"));
	if (!getModule || !getModuleLib) {
		error = "libobs (obs_get_module / obs_get_module_lib) is not loaded in this process";
		return false;
	}

	void *module = getModule("obs-browser");
	if (!module) {
		error = "the obs-browser module is not loaded";
		return false;
	}

	void *lib = getModuleLib(module);
	if (!lib) {
		error = "cannot resolve the obs-browser module handle";
		return false;
	}

	auto createQCef = reinterpret_cast<CreateQCefFn>(FindSymbolInModule(lib, "obs_browser_create_qcef"));
	if (!createQCef) {
		error = "obs-browser does not export obs_browser_create_qcef (too old?)";
		return false;
	}

	panel.cef = createQCef();
	if (!panel.cef) {
		error = "obs_browser_create_qcef() returned null";
		return false;
	}

	if (auto version = reinterpret_cast<QCefVersionFn>(FindSymbolInModule(lib, "obs_browser_qcef_version_export")))
		panel.version = version();
	else
		panel.version = 0;

	return true;
}

} // namespace obsharp
