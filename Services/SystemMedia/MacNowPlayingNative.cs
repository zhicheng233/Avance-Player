using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Avance_Player.Services.SystemMedia;

/// <summary>
/// macOS Now Playing / 远程命令中心的最小 Objective-C 桥。所有平台都编译（只有 P/Invoke 声明），
/// 但只在 macOS 上实例化。失败都包住了，缺框架也不会把播放带崩。
/// </summary>
internal sealed class MacNowPlayingNative : IDisposable {
    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Send0(IntPtr receiver, IntPtr selector);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Send1(IntPtr receiver, IntPtr selector, IntPtr arg1);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Send2(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendVoid0(IntPtr receiver, IntPtr selector);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendVoid1(IntPtr receiver, IntPtr selector, IntPtr arg1);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendVoid2(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendVoidUInt64(IntPtr receiver, IntPtr selector, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SendDoubleArg(IntPtr receiver, IntPtr selector, double value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate double SendDouble(IntPtr receiver, IntPtr selector);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool SendBoolArg(IntPtr receiver, IntPtr selector, IntPtr arg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ObjCAction(IntPtr self, IntPtr command, IntPtr argument);

    [DllImport(ObjCRuntime, CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjCRuntime, CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjCRuntime)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);
    [DllImport(ObjCRuntime)]
    private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjCRuntime, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr implementation, string types);

    private static MacNowPlayingNative? _instance;
    private static readonly ObjCAction PlayAction = HandlePlay;
    private static readonly ObjCAction PauseAction = HandlePause;
    private static readonly ObjCAction ToggleAction = HandleToggle;
    private static readonly ObjCAction NextAction = HandleNext;
    private static readonly ObjCAction PreviousAction = HandlePrevious;
    private static readonly ObjCAction StopAction = HandleStop;
    private static readonly ObjCAction PositionAction = HandlePosition;

    private readonly IntPtr _objcHandle;
    private readonly IntPtr _mediaPlayerHandle;
    private readonly IntPtr _appKitHandle;
    private readonly Send0 _send0 = null!;
    private readonly Send1 _send1 = null!;
    private readonly Send2 _send2 = null!;
    private readonly SendVoid0 _sendVoid0 = null!;
    private readonly SendVoid1 _sendVoid1 = null!;
    private readonly SendVoid2 _sendVoid2 = null!;
    private readonly SendVoidUInt64 _sendVoidUInt64 = null!;
    private readonly SendVoidBool _sendVoidBool = null!;
    private readonly SendDoubleArg _sendDoubleArg = null!;
    private readonly SendDouble _sendDouble = null!;
    private readonly SendBoolArg _sendBoolArg = null!;
    private readonly IntPtr _nowPlayingCenter;
    private readonly IntPtr _remoteCenter;
    private readonly IntPtr _target;
    private readonly List<IntPtr> _commands = [];
    private readonly Dictionary<string, IntPtr> _keys = new(StringComparer.Ordinal);
    private readonly IntPtr _selSetNowPlayingInfo;
    private readonly IntPtr _selSetPlaybackState;
    private readonly IntPtr _selSetObjectForKey;
    private readonly IntPtr _selPositionTime;
    private readonly IntPtr _selInitWithContentsOfFile;
    private readonly IntPtr _selInitWithImage;
    private readonly IntPtr _selRelease;
    private readonly IntPtr _selDrain;
    private bool _available;
    private bool _disposed;

    public MacNowPlayingNative() {
        try {
            _objcHandle = NativeLibrary.Load(ObjCRuntime);
            _mediaPlayerHandle = NativeLibrary.Load("/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer");
            _appKitHandle = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

            _send0 = GetExport<Send0>("objc_msgSend");
            _send1 = GetExport<Send1>("objc_msgSend");
            _send2 = GetExport<Send2>("objc_msgSend");
            _sendVoid0 = GetExport<SendVoid0>("objc_msgSend");
            _sendVoid1 = GetExport<SendVoid1>("objc_msgSend");
            _sendVoid2 = GetExport<SendVoid2>("objc_msgSend");
            _sendVoidUInt64 = GetExport<SendVoidUInt64>("objc_msgSend");
            _sendVoidBool = GetExport<SendVoidBool>("objc_msgSend");
            _sendDoubleArg = GetExport<SendDoubleArg>("objc_msgSend");
            _sendDouble = GetExport<SendDouble>("objc_msgSend");
            _sendBoolArg = GetExport<SendBoolArg>("objc_msgSend");

            _selSetNowPlayingInfo = Sel("setNowPlayingInfo:");
            _selSetPlaybackState = Sel("setPlaybackState:");
            _selSetObjectForKey = Sel("setObject:forKey:");
            _selPositionTime = Sel("positionTime");
            _selInitWithContentsOfFile = Sel("initWithContentsOfFile:");
            _selInitWithImage = Sel("initWithImage:");
            _selRelease = Sel("release");
            _selDrain = Sel("drain");

            _nowPlayingCenter = _send0(GetClass("MPNowPlayingInfoCenter"), Sel("defaultCenter"));
            _remoteCenter = _send0(GetClass("MPRemoteCommandCenter"), Sel("sharedCommandCenter"));
            _target = CreateRemoteTarget();
            if (_nowPlayingCenter == IntPtr.Zero || _remoteCenter == IntPtr.Zero || _target == IntPtr.Zero) return;

            RegisterCommand("playCommand", "handlePlay:");
            RegisterCommand("pauseCommand", "handlePause:");
            RegisterCommand("togglePlayPauseCommand", "handleToggle:");
            RegisterCommand("nextTrackCommand", "handleNext:");
            RegisterCommand("previousTrackCommand", "handlePrevious:");
            RegisterCommand("stopCommand", "handleStop:");
            RegisterCommand("changePlaybackPositionCommand", "handlePosition:");

            _instance = this;
            _available = true;
        }
        catch (Exception) {
            _available = false;
        }
    }

    public bool IsAvailable => _available && !_disposed;
    public event Action<SystemMediaCommand>? CommandReceived;

    private T GetExport<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_objcHandle, name));

    private static IntPtr GetClass(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    private IntPtr CreateRemoteTarget() {
        var existing = GetClass("AvanceRemoteCommandTarget");
        if (existing != IntPtr.Zero) return _send0(_send0(existing, Sel("alloc")), Sel("init"));

        var cls = objc_allocateClassPair(GetClass("NSObject"), "AvanceRemoteCommandTarget", IntPtr.Zero);
        if (cls == IntPtr.Zero) return IntPtr.Zero;

        AddMethod(cls, "handlePlay:", PlayAction);
        AddMethod(cls, "handlePause:", PauseAction);
        AddMethod(cls, "handleToggle:", ToggleAction);
        AddMethod(cls, "handleNext:", NextAction);
        AddMethod(cls, "handlePrevious:", PreviousAction);
        AddMethod(cls, "handleStop:", StopAction);
        AddMethod(cls, "handlePosition:", PositionAction);
        objc_registerClassPair(cls);
        return _send0(_send0(cls, Sel("alloc")), Sel("init"));
    }

    private static void AddMethod(IntPtr cls, string selector, ObjCAction action) {
        class_addMethod(cls, Sel(selector), Marshal.GetFunctionPointerForDelegate(action), "v@:@");
    }

    private void RegisterCommand(string commandSelector, string actionSelector) {
        var command = _send0(_remoteCenter, Sel(commandSelector));
        if (command == IntPtr.Zero) return;
        _sendVoidBool(command, Sel("setEnabled:"), true);
        _sendVoid2(command, Sel("addTarget:action:"), _target, Sel(actionSelector));
        _commands.Add(command);
    }

    public void Update(SystemMediaSnapshot snapshot) {
        if (!IsAvailable) return;
        try {
            var pool = _send0(_send0(GetClass("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
            try {
                var dictionary = _send0(_send0(GetClass("NSMutableDictionary"), Sel("alloc")), Sel("init"));
                if (dictionary == IntPtr.Zero) return;

                SetObject(dictionary, Key("MPMediaItemPropertyTitle"), NewString(snapshot.Title));
                SetObject(dictionary, Key("MPMediaItemPropertyArtist"), NewString(snapshot.Artist));
                SetObject(dictionary, Key("MPMediaItemPropertyAlbumTitle"), NewString(snapshot.Album));
                SetObject(dictionary, Key("MPMediaItemPropertyPlaybackDuration"), NewNumber(snapshot.Duration.TotalSeconds));
                SetObject(dictionary, Key("MPNowPlayingInfoPropertyElapsedPlaybackTime"), NewNumber(snapshot.Position.TotalSeconds));
                SetObject(dictionary, Key("MPNowPlayingInfoPropertyPlaybackRate"),
                    NewNumber(snapshot.IsPlaying ? Math.Max(0.01, snapshot.Rate) : 0));

                if (!string.IsNullOrWhiteSpace(snapshot.ArtworkPath)) {
                    var artwork = NewArtwork(snapshot.ArtworkPath);
                    if (artwork != IntPtr.Zero) SetObject(dictionary, Key("MPMediaItemPropertyArtwork"), artwork);
                }

                _sendVoid1(_nowPlayingCenter, _selSetNowPlayingInfo, dictionary);
                _sendVoid0(dictionary, _selRelease);

                if (_sendBoolArg(_nowPlayingCenter, Sel("respondsToSelector:"), _selSetPlaybackState))
                    _sendVoidUInt64(_nowPlayingCenter, _selSetPlaybackState, snapshot.IsPlaying ? 1UL : 2UL);
            }
            finally {
                if (pool != IntPtr.Zero) _sendVoid0(pool, _selDrain);
            }
        }
        catch (Exception) {
            // Now Playing 挂了也不能影响播放。
        }
    }

    public void Clear() {
        if (!IsAvailable) return;
        try {
            _sendVoid1(_nowPlayingCenter, _selSetNowPlayingInfo, IntPtr.Zero);
            if (_sendBoolArg(_nowPlayingCenter, Sel("respondsToSelector:"), _selSetPlaybackState))
                _sendVoidUInt64(_nowPlayingCenter, _selSetPlaybackState, 3UL);
        }
        catch (Exception) {
            // 忽略关闭时的竞态。
        }
    }

    private void SetObject(IntPtr dictionary, IntPtr key, IntPtr value) {
        if (value == IntPtr.Zero) return;
        _sendVoid2(dictionary, _selSetObjectForKey, value, key);
        _sendVoid0(value, _selRelease);
    }

    private IntPtr NewString(string value) {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value ?? string.Empty);
        try {
            return _send1(_send0(GetClass("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), utf8);
        }
        finally {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private IntPtr NewNumber(double value) =>
        _sendDoubleArg(_send0(GetClass("NSNumber"), Sel("alloc")), Sel("initWithDouble:"), value);

    private IntPtr NewArtwork(string path) {
        var pathString = NewString(path);
        if (pathString == IntPtr.Zero) return IntPtr.Zero;

        var image = _send1(_send0(GetClass("NSImage"), Sel("alloc")), _selInitWithContentsOfFile, pathString);
        _sendVoid0(pathString, _selRelease);
        if (image == IntPtr.Zero) return IntPtr.Zero;

        var artwork = _send1(_send0(GetClass("MPMediaItemArtwork"), Sel("alloc")), _selInitWithImage, image);
        _sendVoid0(image, _selRelease);
        return artwork;
    }

    private IntPtr Key(string symbol) {
        if (_keys.TryGetValue(symbol, out var key)) return key;
        try {
            key = Marshal.ReadIntPtr(NativeLibrary.GetExport(_mediaPlayerHandle, symbol));
        }
        catch (Exception) {
            key = IntPtr.Zero;
        }
        _keys[symbol] = key;
        return key;
    }

    private double ReadPosition(IntPtr commandEvent) {
        try {
            return commandEvent == IntPtr.Zero ? 0 : _sendDouble(commandEvent, _selPositionTime);
        }
        catch (Exception) {
            return 0;
        }
    }

    private void Emit(SystemMediaCommand command) {
        if (!_disposed) CommandReceived?.Invoke(command);
    }

    private static void HandlePlay(IntPtr self, IntPtr command, IntPtr argument) {
        try { _instance?.Emit(new SystemMediaCommand(SystemMediaCommandKind.Play)); } catch (Exception) { }
    }

    private static void HandlePause(IntPtr self, IntPtr command, IntPtr argument) {
        try { _instance?.Emit(new SystemMediaCommand(SystemMediaCommandKind.Pause)); } catch (Exception) { }
    }

    private static void HandleToggle(IntPtr self, IntPtr command, IntPtr argument) {
        try { _instance?.Emit(new SystemMediaCommand(SystemMediaCommandKind.TogglePlayPause)); } catch (Exception) { }
    }

    private static void HandleNext(IntPtr self, IntPtr command, IntPtr argument) {
        try { _instance?.Emit(new SystemMediaCommand(SystemMediaCommandKind.Next)); } catch (Exception) { }
    }

    private static void HandlePrevious(IntPtr self, IntPtr command, IntPtr argument) {
        try { _instance?.Emit(new SystemMediaCommand(SystemMediaCommandKind.Previous)); } catch (Exception) { }
    }

    private static void HandleStop(IntPtr self, IntPtr command, IntPtr argument) {
        try { _instance?.Emit(new SystemMediaCommand(SystemMediaCommandKind.Stop)); } catch (Exception) { }
    }

    private static void HandlePosition(IntPtr self, IntPtr command, IntPtr argument) {
        try {
            var instance = _instance;
            if (instance is not null) {
                instance.Emit(new SystemMediaCommand(
                    SystemMediaCommandKind.Seek,
                    TimeSpan.FromSeconds(Math.Max(0, instance.ReadPosition(argument)))));
            }
        }
        catch (Exception) {
            // 忽略格式不对的远程命令事件。
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _available = false;
        if (ReferenceEquals(_instance, this)) _instance = null;
        try {
            foreach (var command in _commands) _sendVoid1(command, Sel("removeTarget:"), _target);
            _commands.Clear();
            if (_target != IntPtr.Zero) _sendVoid0(_target, _selRelease);
        }
        catch (Exception) {
            // 忽略原生关闭时的竞态。
        }
    }
}
