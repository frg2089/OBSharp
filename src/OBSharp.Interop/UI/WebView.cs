using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using OBSharp.Interop.Abi;
using OBSharp.Interop.UI.Internal;

namespace OBSharp.Interop.UI;

public partial class WebView : IDisposable
{
    private static class QtEventType
    {
        public const int Show = 17;

        public const int Close = 19;

        public const int ObsDockClose = 1019;
    }

    private GCHandle _handle;

    private nint _closeGuard;

    private readonly BrowserHostConfig _config;

    private bool _disposedValue;

    protected ILogger Logger { get; }

    public nint Handle => Widget;

    public nint Widget { get; private set; }

    public string CurrentUrl { get; private set; } = "";

    public event EventHandler<TitleChangedEventArgs>? TitleChanged;

    public event EventHandler<UrlChangedEventArgs>? UrlChanged;

    public event EventHandler<LoadEndEventArgs>? LoadEnd;

    public event EventHandler<ConsoleMessageEventArgs>? ConsoleMessage;

    public event EventHandler<MessageEventArgs>? Message;

    public unsafe WebView(WebViewOptions options, ILogger logger)
    {
        Logger = logger;

        NativeLogSink.Install(logger);

        if (!BrowserHostConfigResolver.TryResolve(
                options.Url, options.Scheme, options.Domain, options.Endpoint,
                options.EndpointKind, options.Host,
                out BrowserHostConfig config, out string? resolveError))
            throw NativeErrors.Create(
                OBSharpResult.InvalidArgument,
                "Invalid browser options: " + (resolveError ?? "unknown error"));


        _config = config;
        CurrentUrl = config.Url;

        LogConfigResolved(
            Logger,
            config.Url,
            config.Scheme,
            config.Domain,
            config.Host is { Length: 0 } ? "(none)" : config.Host,
            config is { HasEndpoint: true, Endpoint: not null } ? config.Endpoint.Address : "(none)");

        NativeBridge.Initialize();

        if (QcefPanel.Load(out string? panelError) is not QcefPanel panel)
            throw NativeErrors.Create(
                OBSharpResult.BrowserUnavailable,
                "No usable obs-browser panel interface: " + (panelError ?? "unknown error"));

        if (config is { HasEndpoint: true, Endpoint: not null })
        {
            LogRegisteringSchemeHandler(Logger, config.Scheme, config.Domain);

            if (!SchemeHandlerBridge.EnsureInstalled(
                    config.Scheme, config.Domain, config.Endpoint, out string? schemeError))
                throw NativeErrors.Create(
                    OBSharpResult.Internal,
                    "Failed to register the scheme handler (" + config.Scheme + "://" + config.Domain + "): " +
                    (schemeError ?? "unknown error"));

            SchemeHandlerBridge.SetMessageSink(config.Domain, OnBridgeMessage);
        }

        string startupScript = options.StartupScript ?? "";

        nint widget = 0;
        nint guard = 0;

        _handle = GCHandle.Alloc(this);

        try
        {
            NativeErrors.RunUI(() =>
            {
                nint parent = ParentResolver.Resolve(options.ParentWidget);

                LogWaitingForCef(Logger);

                if (!AbiApi.Cef(panel.Cef).WaitForInit())
                    throw NativeErrors.Create(
                        OBSharpResult.BrowserUnavailable,
                        "Timed out waiting for obs-browser to finish initializing CEF.");

                widget = AbiApi.Detach(AbiApi.Cef(panel.Cef).CreateWidget(
                    AbiApi.Widget(parent), config.Url, null,
                    s_onTitleChanged, s_onUrlChanged, GCHandle.ToIntPtr(_handle)));
                if (widget is 0)
                    throw NativeErrors.Create(
                        OBSharpResult.Internal,
                        "obsharp_qcef_create_widget() returned a null handle: QCef::create_widget() failed.");
                
                guard = AbiApi.Widget(widget).InstallEventFilter(s_onFilterEvent, GCHandle.ToIntPtr(_handle))?.DangerousGetHandle() ?? 0;

                if (options.Transparent)
                    AbiApi.Widget(widget).SetTranslucentBackground();
                else if (options.BackgroundColor is not 0)
                    AbiApi.Widget(widget).SetBackgroundColor(options.BackgroundColor);

                if (!string.IsNullOrEmpty(startupScript))
                    AbiApi.CefWidget(widget).SetStartupScript(startupScript);

                LogWidgetCreated(Logger, config.Url);
            });
        }
        catch
        {
            if (widget is not 0)
            {
                try
                {
                    ReleaseWidget(widget, guard, Logger);
                }
                catch (Exception cleanupError) when (cleanupError is AbiException or OBSharpNativeException)
                {
                    LogCleanupFailed(Logger, cleanupError);
                }
            }

            if (_handle.IsAllocated)
                _handle.Free();

            Widget = 0;
            _closeGuard = 0;
            throw;
        }

        Widget = widget;
        _closeGuard = guard;
    }

    public int Load(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return Fail(OBSharpResult.InvalidArgument, "The URL must not be empty.");

        if (Widget is 0)
            return Fail(OBSharpResult.InvalidArgument, "The browser has already been disposed.");

        if (!UrlMapping.TryMapObsharpUrlForBrowser(_config.Host, url, _config.Scheme, out string mapped, out string? error))
            return Fail(OBSharpResult.InvalidArgument, error ?? ("Failed to map the URL: " + url));

        return Forward(() =>
        {
            AbiApi.CefWidget(Widget).SetUrl(mapped);
            CurrentUrl = mapped;
        });
    }

    public int Reload() => Forward(() => AbiApi.CefWidget(Widget).Reload());

    public int Zoom(int direction)
    {
        if (Widget is 0)
            return Fail(OBSharpResult.InvalidArgument, "The browser has already been disposed.");

        bool ok = false;
        int result = Forward(() => ok = AbiApi.CefWidget(Widget).Zoom(direction));
        if (result is not 0)
            return result;

        return ok ? 0 : (int)OBSharpResult.Internal;
    }

    public int ExecuteScript(string? script)
    {
        if (string.IsNullOrEmpty(script))
            return Fail(OBSharpResult.InvalidArgument, "The script must not be empty.");

        return Forward(() => AbiApi.CefWidget(Widget).ExecuteScript(script));
    }

    public int SetStartupScript(string? script)
    {
        string startupScript = script ?? "";
        return Forward(() => AbiApi.CefWidget(Widget).SetStartupScript(startupScript));
    }

    public int AllowPopups(bool allow)
        => Forward(() => AbiApi.CefWidget(Widget).AllowAllPopups(allow));

    public int Resize(int width, int height) => Forward(() => AbiApi.Widget(Widget).Resize(width, height));

    public int Move(int x, int y) => Forward(() => AbiApi.Widget(Widget).Move(x, y));

    public int SetVisible(bool visible)
        => Forward(() => AbiApi.Widget(Widget).SetVisible(visible));

    private readonly Queue<string> _pendingMessages = new();

    private bool _draining;

    private void OnBridgeMessage(string json)
    {
        lock (_pendingMessages)
        {
            _pendingMessages.Enqueue(json);

            if (_draining)
                return;

            _draining = true;
        }

        ThreadPool.QueueUserWorkItem(static state => ((WebView)state!).DrainMessages(), this);
    }

    private void DrainMessages()
    {
        while (true)
        {
            string json;
            lock (_pendingMessages)
            {
                if (_pendingMessages.Count is 0)
                {

                    _draining = false;
                    return;
                }

                json = _pendingMessages.Dequeue();
            }

            try
            {
                AbiApi.InvokeUI(() =>
                {
                    if (_disposedValue)
                        return;

                    DispatchJson(json);
                });
            }
            catch (Exception exception) when (exception is AbiException or OBSharpNativeException)
            {

                LogPageMessageDropped(Logger, exception);
            }
        }
    }

    private int Fail(OBSharpResult code, string message)
    {
        LogOperationFailed(Logger, message);
        return (int)code;
    }

    private void DispatchJson(string json)
    {
        bool control = BridgeDispatcher.TryDispatch(
            json,
            info => RaiseConsoleMessage(info),
            (statusCode, errorCode, errorText) =>
                RaiseSafely(() => LoadEnd?.Invoke(this, new LoadEndEventArgs(errorCode, statusCode, errorText))));

        if (!control)
            RaiseSafely(() => Message?.Invoke(this, new MessageEventArgs(json)));
    }

    private void RaiseConsoleMessage(ConsoleMessageInfo info)
    {
        var level = (OBSharpConsoleLevel)info.Level;
        switch (level)
        {
            case OBSharpConsoleLevel.Verbose:
                LogPageConsoleDebug(Logger, level, info.Message, info.Source, info.Line);
                break;
            case OBSharpConsoleLevel.Warning:
                LogPageConsoleWarning(Logger, level, info.Message, info.Source, info.Line);
                break;
            case OBSharpConsoleLevel.Error:
                LogPageConsoleError(Logger, level, info.Message, info.Source, info.Line);
                break;
            case OBSharpConsoleLevel.Fatal:
                LogPageConsoleCritical(Logger, level, info.Message, info.Source, info.Line);
                break;
            default:
                LogPageConsoleInformation(Logger, level, info.Message, info.Source, info.Line);
                break;
        }

        RaiseSafely(() => ConsoleMessage?.Invoke(
            this, new ConsoleMessageEventArgs(level, info.Message, info.Source, info.Line)));
    }

    private void RaiseSafely(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception exception)
        {
            LogException(Logger, exception);
        }
    }

    // 委托实例静态持有：原生侧（QCefWidget 的信号）长期持有它们。
    private static readonly ObsharpCefWidgetValueCallback s_onTitleChanged = OnTitleChanged;
    private static readonly ObsharpCefWidgetValueCallback s_onUrlChanged = OnUrlChanged;
    private static readonly ObsharpWidgetFilterEventCallback s_onFilterEvent = OnFilterEvent;

    private static void OnTitleChanged(nint widget, string? value, nint user)
    {
        if (GCHandle.FromIntPtr(user).Target is not WebView browser)
            return;

        browser.RaiseSafely(() => browser.TitleChanged?.Invoke(browser, new TitleChangedEventArgs(value)));
    }

    private static void OnUrlChanged(nint widget, string? value, nint user)
    {
        if (GCHandle.FromIntPtr(user).Target is not WebView browser)
            return;

        browser.CurrentUrl = value ?? "";
        browser.RaiseSafely(() => browser.UrlChanged?.Invoke(browser, new UrlChangedEventArgs(value)));
    }

    private static bool OnFilterEvent(nint watched, int eventType, nint user)
    {
        if (GCHandle.FromIntPtr(user).Target is not WebView browser)
            return false;

        if (eventType is QtEventType.ObsDockClose or QtEventType.Close)
        {
            LogBlockedCloseEvent(browser.Logger, eventType);
            return true;
        }

        if (eventType is QtEventType.Show)
        {
            LogNudgingRepaint(browser.Logger);
            NudgeRepaint(watched);
        }

        return false;
    }

    private static unsafe void NudgeRepaint(nint widget)
    {
        if (widget is 0)
            return;

        int width = 0;
        int height = 0;
        width = AbiApi.Widget(widget).GetWidth();
            height = AbiApi.Widget(widget).GetHeight();

        if (width <= 0 || height <= 0)
        {

            AbiApi.Widget(widget).Update();
            return;
        }

        AbiApi.Widget(widget).Resize(width + 1, height + 1);
        AbiApi.Widget(widget).Resize(width, height);
        AbiApi.Widget(widget).Update();
    }

    private int Forward(Action action)
    {
        if (Widget is 0)
            return (int)OBSharpResult.InvalidArgument;

        try
        {
            NativeErrors.RunUI(action);
            return (int)OBSharpResult.Ok;
        }
        catch (OBSharpNativeException exception)
        {
            LogBrowserOperationFailed(Logger, exception, exception.Result);
            return (int)exception.Result;
        }
    }

    private static void ReleaseWidget(nint widget, nint guard, ILogger logger)
    {
        AbiApi.InvokeUI(() =>
        {

            AbiApi.CefWidget(widget).DetachCallbacks();

            if (guard is not 0)
                AbiApi.Widget(widget).RemoveEventFilter(AbiApi.QObject(guard));

            AbiApi.CefWidget(widget).CloseBrowser();

            AbiApi.Widget(widget).Hide();

            if (QWidget.GetParent(AbiApi.Widget(widget)) is null)
            {
                AbiApi.DestroyWidget(widget);
            }
            else
            {
                LogWidgetOwnedElsewhere(logger);
            }
        });
    }

    [LoggerMessage(LogLevel.Error, "An exception occurred in the browser callback.")]
    private static partial void LogException(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Information,
        "Browser options resolved: url={Url} scheme={Scheme} domain={Domain} host={Host} endpoint={Endpoint}")]
    private static partial void LogConfigResolved(
        ILogger logger, string url, string scheme, string domain, string host, string endpoint);

    [LoggerMessage(LogLevel.Debug, "Registering the scheme handler for {Scheme}://{Domain}")]
    private static partial void LogRegisteringSchemeHandler(ILogger logger, string scheme, string domain);

    [LoggerMessage(LogLevel.Debug, "Waiting for obs-browser to finish initializing CEF")]
    private static partial void LogWaitingForCef(ILogger logger);

    [LoggerMessage(LogLevel.Information, "Browser widget created for {Url}")]
    private static partial void LogWidgetCreated(ILogger logger, string url);

    [LoggerMessage(LogLevel.Debug, "Cleanup after a failed WebView construction also failed")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Debug,
        "Could not marshal a page message to the Qt main thread; the message was dropped")]
    private static partial void LogPageMessageDropped(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "Browser operation failed: {Message}")]
    private static partial void LogOperationFailed(ILogger logger, string message);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "Page console {ConsoleLevel}: {Message} (source={Source}, line={Line})")]
    private static partial void LogPageConsoleDebug(
        ILogger logger, OBSharpConsoleLevel consoleLevel, string message, string source, int line);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "Page console {ConsoleLevel}: {Message} (source={Source}, line={Line})")]
    private static partial void LogPageConsoleInformation(
        ILogger logger, OBSharpConsoleLevel consoleLevel, string message, string source, int line);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "Page console {ConsoleLevel}: {Message} (source={Source}, line={Line})")]
    private static partial void LogPageConsoleWarning(
        ILogger logger, OBSharpConsoleLevel consoleLevel, string message, string source, int line);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "Page console {ConsoleLevel}: {Message} (source={Source}, line={Line})")]
    private static partial void LogPageConsoleError(
        ILogger logger, OBSharpConsoleLevel consoleLevel, string message, string source, int line);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Critical,
        Message = "Page console {ConsoleLevel}: {Message} (source={Source}, line={Line})")]
    private static partial void LogPageConsoleCritical(
        ILogger logger, OBSharpConsoleLevel consoleLevel, string message, string source, int line);

    [LoggerMessage(LogLevel.Debug, "Blocked a close event on the browser widget (type={EventType})")]
    private static partial void LogBlockedCloseEvent(ILogger logger, int eventType);

    [LoggerMessage(LogLevel.Debug, "Browser widget shown; nudging CEF to repaint")]
    private static partial void LogNudgingRepaint(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Browser operation failed with {Result}")]
    private static partial void LogBrowserOperationFailed(ILogger logger, Exception exception, OBSharpResult result);

    [LoggerMessage(LogLevel.Debug,
        "Browser widget is owned elsewhere (adopted by OBS's dock); leaving it to its parent")]
    private static partial void LogWidgetOwnedElsewhere(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Failed to destroy the browser widget (Qt may already have shut down)")]
    private static partial void LogDestroyFailed(ILogger logger, Exception exception);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposedValue)
            return;

        _disposedValue = true;

        if (Widget is 0)
        {
            if (disposing && _handle.IsAllocated)
                _handle.Free();

            return;
        }

        nint widget = Widget;
        nint guard = _closeGuard;

        Widget = 0;
        _closeGuard = 0;

        try
        {
            ReleaseWidget(widget, guard, Logger);
        }
        catch (Exception exception)
        {
            LogDestroyFailed(Logger, exception);
        }

        if (_handle.IsAllocated)
            _handle.Free();
    }

    ~WebView()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
