using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

using OBSharp.Loader.Models;

using OBSharp.PInvoke;

using Shimakaze;

namespace OBSharp.Loader;

public static unsafe class EntryPoints
{
    private const string AssemblyFileName = "OBSharp.Runtime.dll";
    private const string RuntimeConfigFileName = "OBSharp.Runtime.runtimeconfig.json";
    private const string TypeName = "OBSharp.OBSModule, OBSharp.Runtime";
    private const string Tfm = "net10.0";
    private const string FrameworkVersion = "10.0.0";

    private const int LIBOBS_API_MAJOR_VER = 32;
    private const int LIBOBS_API_MINOR_VER = 0;
    private const int LIBOBS_API_PATCH_VER = 0;
    private const int LIBOBS_API_VER = (LIBOBS_API_MAJOR_VER << 24) | (LIBOBS_API_MINOR_VER << 16) | LIBOBS_API_PATCH_VER;

    private static ObsModule* s_obs_module_pointer;
    private static HostFXR? s_hostFxr;
    private static HostFXRHandle? s_context;

    private static nint s_obs_module_set_pointer;
    private static nint s_obs_module_name;
    private static nint s_obs_module_description;
    private static nint s_obs_module_set_locale;
    private static nint s_obs_module_load;
    private static nint s_obs_module_post_load;
    private static nint s_obs_module_unload;
    private static nint s_obs_module_free_locale;

    private static Framework NETCore => new("Microsoft.NETCore.App", FrameworkVersion);
    private static Framework WindowsDesktop => new("Microsoft.WindowsDesktop.App", FrameworkVersion);
    private static Framework AspNetCore => new("Microsoft.AspNetCore.App", FrameworkVersion);

    private static bool LoadMethod(string methodName, string? delegateTypeName, [NotNullWhen(true)] out nint @delegate, bool required)
    {
        Debug.Assert(s_context is not null);
        try
        {
            Logger.Debug($"get_function_pointer(\"{TypeName}\", \"{methodName}\", \"{delegateTypeName}\")");
            s_context.GetFunctionPointer(TypeName, methodName, delegateTypeName, out @delegate);
            return true;
        }
        catch (Exception ex)
        {
            if (required)
                throw;

            @delegate = 0;
            Logger.Warn(ex.ToString());
            return false;
        }
    }

    [MemberNotNullWhen(true, nameof(s_hostFxr), nameof(s_context))]
    private static bool InitializeCoreCLR()
    {
        try
        {
            if (s_hostFxr is not null && s_context is not null)
                return true;

            if (s_obs_module_pointer is null)
                throw new InvalidOperationException("MUST invoke obs_module_set_pointer first.");

            Logger.Info("Initializing CoreCLR ...");

            if (s_obs_module_pointer->GetModuleBinaryPath() is not { Length: not 0 } unmanaged)
                throw new InvalidOperationException("Cannot get current plugin's binary path.");
            unmanaged = Path.GetFullPath(unmanaged);
            Logger.Debug($"OBSharp Unmanaged Path: {unmanaged}");

            if (s_obs_module_pointer->GetModuleDataPath() is not { Length: not 0 } dataPath)
                throw new InvalidOperationException("Cannot get current plugin's data path.");
            dataPath = Path.GetFullPath(dataPath);
            Logger.Debug($"OBSharp Data Path: {dataPath}");

            if (Path.GetDirectoryName(unmanaged) is not { Length: not 0 } binArch)
                throw new DirectoryNotFoundException($"Cannot found the path \"{unmanaged}\"'s parent.");

            if (Path.GetDirectoryName(binArch) is not { Length: not 0 } binBase)
                throw new DirectoryNotFoundException($"Cannot found the path \"{binArch}\"'s parent.");

            string binAny = Path.Combine(binBase, "any");
            string asm = Path.Combine(binAny, AssemblyFileName);
            Logger.Debug($"OBSharp Managed Path: {asm}");

            Logger.Info($"Using NETCore framework.");
            List<Framework> frameworks = new(3)
            {
                NETCore
            };

            string appsettings = Path.Combine(dataPath, "appsettings.json");
            if (File.Exists(appsettings))
            {
                Logger.Debug($"Loading appsettings.json from {appsettings}");
                using var fs = File.OpenRead(appsettings);
                var appconfig = JsonSerializer.Deserialize(fs, SourceGenerationContext.Default.AppSettings);
                if (appconfig?.Runtime.UseWindowsDesktop is true)
                {
                    Logger.Info($"Using WindowsDesktop framework.");
                    frameworks.Add(WindowsDesktop);
                }
                if (appconfig?.Runtime.UseAspNet is true)
                {
                    Logger.Info($"Using AspNetCore framework.");
                    frameworks.Add(AspNetCore);
                }
            }

            string runtimeconfigTemplatePath = Path.Combine(dataPath, RuntimeConfigFileName);
            RuntimeConfig? runtimeConfig = null;
            if (File.Exists(runtimeconfigTemplatePath))
            {
                using var fs = File.OpenRead(runtimeconfigTemplatePath);
                runtimeConfig = JsonSerializer.Deserialize(fs, SourceGenerationContext.Default.RuntimeConfig);
            }

            runtimeConfig ??= new();
            runtimeConfig.RuntimeOptions.Tfm = Tfm;
            if (frameworks.Count is 1)
                runtimeConfig.RuntimeOptions.Framework = frameworks[0];
            else
                runtimeConfig.RuntimeOptions.Frameworks = frameworks;

            string runtimeconfigPath = Path.Combine(binAny, RuntimeConfigFileName);
            using (var fs = File.Create(runtimeconfigPath))
                JsonSerializer.Serialize(fs, runtimeConfig, SourceGenerationContext.Default.RuntimeConfig);
            try
            {
                Logger.Debug($"CoreCLR Runtime Config Path: {runtimeconfigPath}");

                string hostfxrPath = NetHost.GetHostFxrPath();
                Logger.Info($"Found CoreCLR Host Framework at \"{hostfxrPath}\".");

                s_hostFxr = new HostFXR(hostfxrPath);
                Logger.Debug($"CoreCLR Host Framework loaded!");

                s_hostFxr.InitializeForRuntimeConfig(runtimeconfigPath, null, out s_context);

                s_context.LoadAssembly(asm);

                LoadMethod("SetPointer", HostFXR.UNMANAGEDCALLERSONLY_METHOD, out s_obs_module_set_pointer, true);
                LoadMethod("Load", HostFXR.UNMANAGEDCALLERSONLY_METHOD, out s_obs_module_load, true);

                LoadMethod("GetName", HostFXR.UNMANAGEDCALLERSONLY_METHOD, out s_obs_module_name, false);
                LoadMethod("GetDescription", HostFXR.UNMANAGEDCALLERSONLY_METHOD, out s_obs_module_description, false);
                LoadMethod("SetLocale", HostFXR.UNMANAGEDCALLERSONLY_METHOD, out s_obs_module_set_locale, false);
                LoadMethod("PostLoad", HostFXR.UNMANAGEDCALLERSONLY_METHOD, out s_obs_module_post_load, false);
                LoadMethod("Unload", HostFXR.UNMANAGEDCALLERSONLY_METHOD, out s_obs_module_unload, false);
                LoadMethod("FreeLocale", HostFXR.UNMANAGEDCALLERSONLY_METHOD, out s_obs_module_free_locale, false);

                return true;
            }
            catch
            {
                Logger.Error($"runtimeconfig.json: {runtimeconfigPath}");
                Logger.Error(File.ReadAllText(runtimeconfigPath));
                throw;
            }
        }
        catch (Exception e)
        {
            Logger.Error(e.ToString());
            return false;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "obs_module_set_pointer", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetPointer(ObsModule* module)
    {
        s_obs_module_pointer = module;
        Logger.Debug("Invoke obs_module_set_pointer.");
        if (!InitializeCoreCLR())
            return;

        if (s_obs_module_set_pointer is 0)
            return;

        ((delegate* unmanaged<ObsModule*, void>)s_obs_module_set_pointer)(module);
    }

    [UnmanagedCallersOnly(EntryPoint = "obs_module_ver", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static uint GetVersion()
    {
        return LIBOBS_API_VER;
    }

    [UnmanagedCallersOnly(EntryPoint = "obs_module_name", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static byte* Name()
    {
        Logger.Debug("Invoke obs_module_name.");
        if (!InitializeCoreCLR())
            return null;

        if (s_obs_module_name is 0)
            return null;

        return ((delegate* unmanaged<byte*>)s_obs_module_name)();
    }

    [UnmanagedCallersOnly(EntryPoint = "obs_module_description", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static byte* Description()
    {
        Logger.Debug("Invoke obs_module_description.");
        if (!InitializeCoreCLR())
            return null;

        if (s_obs_module_description is 0)
            return null;

        return ((delegate* unmanaged<byte*>)s_obs_module_description)();
    }

    [UnmanagedCallersOnly(EntryPoint = "obs_module_set_locale", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetLocale(byte* locale)
    {
        Logger.Debug("Invoke obs_module_set_locale.");
        if (!InitializeCoreCLR())
            return;

        if (s_obs_module_set_locale is 0)
            return;

        ((delegate* unmanaged<byte*, void>)s_obs_module_set_locale)(locale);
    }

    [UnmanagedCallersOnly(EntryPoint = "obs_module_load", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static bool Load()
    {
        Logger.Debug("Invoke obs_module_load.");
        if (!InitializeCoreCLR())
            return false;

        if (s_obs_module_load is 0)
            return false;

        return ((delegate* unmanaged<int>)s_obs_module_load)() is not 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "obs_module_post_load", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void PostLoad()
    {
        Logger.Debug("Invoke obs_module_post_load.");
        if (!InitializeCoreCLR())
            return;

        if (s_obs_module_post_load is 0)
            return;

        ((delegate* unmanaged<void>)s_obs_module_post_load)();
    }

    [UnmanagedCallersOnly(EntryPoint = "obs_module_unload", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Unload()
    {
        Logger.Debug("Invoke obs_module_unload.");
        if (!InitializeCoreCLR())
            return;

        if (s_obs_module_unload is 0)
            return;

        ((delegate* unmanaged<void>)s_obs_module_unload)();
    }

    [UnmanagedCallersOnly(EntryPoint = "obs_module_free_locale", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void FreeLocale()
    {
        Logger.Debug("Invoke obs_module_free_locale.");
        if (!InitializeCoreCLR())
            return;

        if (s_obs_module_free_locale is 0)
            return;

        ((delegate* unmanaged<void>)s_obs_module_free_locale)();
    }
}
