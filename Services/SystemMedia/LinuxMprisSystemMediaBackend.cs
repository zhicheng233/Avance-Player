using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Avance_Player.Services.SystemMedia;

/// <summary>
/// 把播放器挂到 Linux MPRIS D-Bus 上，再把媒体键命令翻回
/// <see cref="SystemMediaCommand"/>。连接是可选的：没有 session bus 的无头会话，后端就是不可用。
/// </summary>
public sealed class LinuxMprisSystemMediaBackend : ISystemMediaBackend {
    private const string ServiceName = "org.mpris.MediaPlayer2.avanceplayer";
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string MediaPlayerInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly object _sync = new();
    private readonly object _sendSync = new();
    private DBusConnection? _connection;
    private SystemMediaSnapshot? _snapshot;
    private bool _disposed;

    public LinuxMprisSystemMediaBackend() {
        _ = ConnectAsync();
    }

    public bool IsAvailable {
        get {
            lock (_sync) return !_disposed && _connection is not null;
        }
    }

    public event Action<SystemMediaCommand>? CommandReceived;

    private async Task ConnectAsync() {
        DBusConnection? connection = null;
        try {
            var address = DBusAddress.Session;
            if (string.IsNullOrWhiteSpace(address)) return;

            connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            if (_disposed) {
                connection.Dispose();
                return;
            }

            var handler = new MprisMethodHandler(this);
            connection.AddMethodHandler(handler);
            await connection.RequestNameAsync(ServiceName, RequestNameOptions.None).ConfigureAwait(false);
            if (_disposed) {
                connection.Dispose();
                return;
            }

            lock (_sync) {
                _connection = connection;
            }

            // 名字拿到之后先把当前快照推一遍。第一首歌还没准备好就启动的播放器，
            // 也能对外是个有效但空的媒体会话。
            // 客户端会自己在 D-Bus 上找会话再调 GetAll；不用先发信号，后端现在能回答属性读取就行。
        }
        catch (Exception) {
            // 没有 session bus、没权限，或者名字被别的实例占了。
            // 应用继续放，只是 OS 集成不可用。
            try {
                connection?.Dispose();
            }
            catch (Exception) {
                // 忽略连接销毁时的竞态。
            }
            lock (_sync) _connection = null;
        }
    }

    public void Update(SystemMediaSnapshot snapshot) {
        lock (_sync) {
            if (_disposed) return;
            _snapshot = snapshot;
        }

        // handler 实例挂在连接上；发信号走后端，生命周期只有一份要管。
        SendPropertiesChanged(snapshot);
    }

    public void Clear() {
        lock (_sync) {
            if (_disposed) return;
            _snapshot = null;
            _lastSignalled = null;
        }

        var changed = new Dictionary<string, VariantValue> {
            ["PlaybackStatus"] = "Stopped",
            ["Metadata"] = new Dict<string, VariantValue>()
        };
        SendPropertiesChanged(PlayerInterface, changed);
    }

    public void Dispose() {
        DBusConnection? connection;
        lock (_sync) {
            if (_disposed) return;
            _disposed = true;
            connection = _connection;
            _connection = null;
            _snapshot = null;
        }

        try {
            connection?.Dispose();
        }
        catch (Exception) {
            // 忽略关闭时的竞态。
        }
    }

    private void SendPropertiesChanged(SystemMediaSnapshot snapshot) {
        var changed = new Dictionary<string, VariantValue>();
        var previous = _lastSignalled;
        _lastSignalled = snapshot;

        if (previous is null ||
            previous.Title != snapshot.Title ||
            previous.Artist != snapshot.Artist ||
            previous.Album != snapshot.Album ||
            previous.Duration != snapshot.Duration ||
            previous.ArtworkPath != snapshot.ArtworkPath ||
            previous.SourcePath != snapshot.SourcePath) {
            changed["Metadata"] = new Dict<string, VariantValue>(BuildMetadata(snapshot));
        }

        if (previous is null || previous.IsPlaying != snapshot.IsPlaying) {
            changed["PlaybackStatus"] = snapshot.IsPlaying ? "Playing" : "Paused";
        }

        if (previous is null || Math.Abs(previous.Volume - snapshot.Volume) > 0.0001) {
            changed["Volume"] = Math.Clamp(snapshot.Volume, 0, 1);
        }

        if (previous is null || previous.IsShuffle != snapshot.IsShuffle) {
            changed["Shuffle"] = snapshot.IsShuffle;
        }

        if (previous is null || previous.RepeatMode != snapshot.RepeatMode) {
            changed["LoopStatus"] = snapshot.RepeatMode switch {
                SystemMediaRepeatMode.Track => "Track",
                SystemMediaRepeatMode.Playlist => "Playlist",
                _ => "None"
            };
        }

        if (previous is null || Math.Abs(previous.Rate - snapshot.Rate) > 0.0001) {
            changed["Rate"] = snapshot.Rate;
        }

        if (changed.Count > 0) SendPropertiesChanged(PlayerInterface, changed);
    }

    private SystemMediaSnapshot? _lastSignalled;

    private void SendPropertiesChanged(string interfaceName, Dictionary<string, VariantValue> changed) {
        DBusConnection? connection;
        lock (_sync) connection = _connection;
        if (connection is null) return;

        try {
            lock (_sendSync) {
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(
                    path: ObjectPath,
                    @interface: PropertiesInterface,
                    member: "PropertiesChanged",
                    signature: "sa{sv}as");
                writer.WriteString(interfaceName);
                writer.WriteDictionary(changed);
                writer.WriteArray(Array.Empty<string>());
                connection.TrySendMessage(writer.CreateMessage());
            }
        }
        catch (Exception) {
            // 应用退出时 session bus 可能已经没了。
        }
    }

    private void SendSeeked(long positionMicroseconds) {
        DBusConnection? connection;
        lock (_sync) connection = _connection;
        if (connection is null) return;

        try {
            lock (_sendSync) {
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(
                    path: ObjectPath,
                    @interface: PlayerInterface,
                    member: "Seeked",
                    signature: "x");
                writer.WriteInt64(positionMicroseconds);
                connection.TrySendMessage(writer.CreateMessage());
            }
        }
        catch (Exception) {
            // 忽略关闭时的竞态。
        }
    }

    private void EmitCommand(SystemMediaCommand command) {
        if (!_disposed) CommandReceived?.Invoke(command);
    }

    private static Dictionary<string, VariantValue> BuildMetadata(SystemMediaSnapshot snapshot) {
        var metadata = new Dictionary<string, VariantValue> {
            ["mpris:trackid"] = VariantValue.ObjectPath(new ObjectPath(TrackId(snapshot))),
            ["mpris:length"] = VariantValue.Int64((long)Math.Max(0, snapshot.Duration.TotalMilliseconds * 1000)),
            ["xesam:title"] = snapshot.Title,
            ["xesam:artist"] = VariantValue.Array(new[] { snapshot.Artist }),
            ["xesam:album"] = snapshot.Album
        };

        var url = ToFileUri(snapshot.SourcePath);
        if (url is not null) metadata["xesam:url"] = url;
        var artUrl = ToFileUri(snapshot.ArtworkPath);
        if (artUrl is not null) metadata["mpris:artUrl"] = artUrl;
        return metadata;
    }

    private static string TrackId(SystemMediaSnapshot snapshot) {
        var key = snapshot.SourcePath ?? $"{snapshot.Title}\u001f{snapshot.Artist}\u001f{snapshot.Album}";
        var hash = (uint)StringComparer.Ordinal.GetHashCode(key);
        return $"/com/avance/Player/Track/{hash:x8}";
    }

    private static string? ToFileUri(string? path) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        catch (Exception) {
            return null;
        }
    }

    private sealed class MprisMethodHandler(LinuxMprisSystemMediaBackend owner) : IPathMethodHandler {
        private const string IntrospectionXml = """
            <interface name="org.mpris.MediaPlayer2">
              <method name="Raise"/>
              <method name="Quit"/>
              <property name="CanQuit" type="b" access="read"/>
              <property name="CanRaise" type="b" access="read"/>
              <property name="HasTrackList" type="b" access="read"/>
              <property name="Identity" type="s" access="read"/>
              <property name="DesktopEntry" type="s" access="read"/>
              <property name="SupportedUriSchemes" type="as" access="read"/>
              <property name="SupportedMimeTypes" type="as" access="read"/>
              <property name="CanSetFullscreen" type="b" access="read"/>
              <property name="Fullscreen" type="b" access="read"/>
            </interface>
            <interface name="org.mpris.MediaPlayer2.Player">
              <method name="Next"/>
              <method name="Previous"/>
              <method name="Pause"/>
              <method name="PlayPause"/>
              <method name="Stop"/>
              <method name="Play"/>
              <method name="Seek"><arg direction="in" type="x"/></method>
              <method name="SetPosition"><arg direction="in" type="o"/><arg direction="in" type="x"/></method>
              <method name="OpenUri"><arg direction="in" type="s"/></method>
              <property name="PlaybackStatus" type="s" access="read"/>
              <property name="LoopStatus" type="s" access="readwrite"/>
              <property name="Rate" type="d" access="readwrite"/>
              <property name="Shuffle" type="b" access="readwrite"/>
              <property name="Metadata" type="a{sv}" access="read"/>
              <property name="Volume" type="d" access="readwrite"/>
              <property name="Position" type="x" access="read"/>
              <property name="MinimumRate" type="d" access="read"/>
              <property name="MaximumRate" type="d" access="read"/>
              <property name="CanGoNext" type="b" access="read"/>
              <property name="CanGoPrevious" type="b" access="read"/>
              <property name="CanPlay" type="b" access="read"/>
              <property name="CanPause" type="b" access="read"/>
              <property name="CanSeek" type="b" access="read"/>
              <property name="CanControl" type="b" access="read"/>
              <signal name="Seeked"><arg name="Position" type="x"/></signal>
            </interface>
            """;

        private static readonly ReadOnlyMemory<byte> InterfaceXml = System.Text.Encoding.UTF8.GetBytes(IntrospectionXml);

        public string Path => ObjectPath;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context) {
            try {
                if (context.IsDBusIntrospectRequest) {
                    context.ReplyIntrospectXml(new[] { InterfaceXml }, Array.Empty<string>());
                    return ValueTask.CompletedTask;
                }

                if (context.IsPropertiesInterfaceRequest) {
                    HandleProperties(context);
                    return ValueTask.CompletedTask;
                }

                var interfaceName = context.Request.InterfaceAsString;
                var member = context.Request.MemberAsString;
                if (interfaceName == MediaPlayerInterface) {
                    HandleMediaPlayerMethod(context, member);
                }
                else if (interfaceName == PlayerInterface) {
                    HandlePlayerMethod(context, member);
                }
                else {
                    context.ReplyUnknownMethodError();
                }
            }
            catch (Exception exception) {
                context.HandleException(exception, false);
            }

            return ValueTask.CompletedTask;
        }

        private void HandleMediaPlayerMethod(MethodContext context, string? member) {
            switch (member) {
                case "Raise":
                    ReplyEmpty(context);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Raise));
                    break;
                case "Quit":
                    ReplyEmpty(context);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Quit));
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }

        private void HandlePlayerMethod(MethodContext context, string? member) {
            var reader = context.Request.GetBodyReader();
            switch (member) {
                case "Next":
                    ReplyEmpty(context);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Next));
                    break;
                case "Previous":
                    ReplyEmpty(context);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Previous));
                    break;
                case "Pause":
                    ReplyEmpty(context);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Pause));
                    break;
                case "PlayPause":
                    ReplyEmpty(context);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.TogglePlayPause));
                    break;
                case "Stop":
                    ReplyEmpty(context);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Stop));
                    break;
                case "Play":
                    ReplyEmpty(context);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Play));
                    break;
                case "Seek": {
                    var offset = reader.ReadInt64();
                    ReplyEmpty(context);
                    var snapshot = owner.CurrentSnapshot;
                    var target = ClampPosition(
                        (snapshot?.Position ?? TimeSpan.Zero) + TimeSpan.FromTicks(offset * 10), snapshot);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Seek, target));
                    owner.SendSeeked(ToMicroseconds(target));
                    break;
                }
                case "SetPosition": {
                    reader.ReadObjectPath();
                    var position = reader.ReadInt64();
                    ReplyEmpty(context);
                    var snapshot = owner.CurrentSnapshot;
                    var target = ClampPosition(TimeSpan.FromTicks(position * 10), snapshot);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Seek, target));
                    owner.SendSeeked(ToMicroseconds(target));
                    break;
                }
                case "OpenUri":
                    reader.ReadString();
                    ReplyEmpty(context);
                    owner.EmitCommand(new SystemMediaCommand(SystemMediaCommandKind.Raise));
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }

        private void HandleProperties(MethodContext context) {
            var reader = context.Request.GetBodyReader();
            switch (context.Request.MemberAsString) {
                case "Get": {
                    var interfaceName = reader.ReadString();
                    var propertyName = reader.ReadString();
                    if (!TryGetProperty(interfaceName, propertyName, out var value)) {
                        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"Unknown property {propertyName}");
                        return;
                    }

                    using var writer = context.CreateReplyWriter("v");
                    writer.WriteVariant(value);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                case "GetAll": {
                    var interfaceName = reader.ReadString();
                    var values = GetAll(interfaceName);
                    using var writer = context.CreateReplyWriter("a{sv}");
                    writer.WriteDictionary(values);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                case "Set": {
                    var interfaceName = reader.ReadString();
                    var propertyName = reader.ReadString();
                    var value = reader.ReadVariantValue();
                    ApplyProperty(interfaceName, propertyName, value);
                    ReplyEmpty(context);
                    break;
                }
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }

        private bool TryGetProperty(string interfaceName, string propertyName, out VariantValue value) {
            value = default;
            if (interfaceName == MediaPlayerInterface) {
                switch (propertyName) {
                    case "CanQuit": value = true; return true;
                    case "CanRaise": value = true; return true;
                    case "HasTrackList": value = false; return true;
                    case "Identity": value = "Avance Player"; return true;
                    case "DesktopEntry": value = "avance-player"; return true;
                    case "SupportedUriSchemes": value = VariantValue.Array(new[] { "file" }); return true;
                    case "SupportedMimeTypes": value = VariantValue.Array(new[] { "audio/mpeg", "audio/flac", "audio/ogg", "audio/wav" }); return true;
                    case "CanSetFullscreen": value = false; return true;
                    case "Fullscreen": value = false; return true;
                }
            }
            else if (interfaceName == PlayerInterface) {
                var snapshot = owner.CurrentSnapshot;
                switch (propertyName) {
                    case "PlaybackStatus": value = snapshot is null ? "Stopped" : snapshot.IsPlaying ? "Playing" : "Paused"; return true;
                    case "LoopStatus": value = snapshot?.RepeatMode switch {
                        SystemMediaRepeatMode.Track => "Track",
                        SystemMediaRepeatMode.Playlist => "Playlist",
                        _ => "None"
                    }; return true;
                    case "Rate": value = snapshot?.Rate ?? 1.0; return true;
                    case "Shuffle": value = snapshot?.IsShuffle ?? false; return true;
                    case "Metadata": value = snapshot is null ? new Dict<string, VariantValue>() : new Dict<string, VariantValue>(BuildMetadata(snapshot)); return true;
                    case "Volume": value = snapshot?.Volume ?? 1.0; return true;
                    case "Position": value = snapshot is null ? 0L : ToMicroseconds(snapshot.Position); return true;
                    case "MinimumRate": value = 0.25; return true;
                    case "MaximumRate": value = 4.0; return true;
                    case "CanGoNext": value = true; return true;
                    case "CanGoPrevious": value = true; return true;
                    case "CanPlay": value = true; return true;
                    case "CanPause": value = true; return true;
                    case "CanSeek": value = true; return true;
                    case "CanControl": value = true; return true;
                }
            }

            return false;
        }

        private Dictionary<string, VariantValue> GetAll(string interfaceName) {
            var values = new Dictionary<string, VariantValue>();
            if (interfaceName == MediaPlayerInterface) {
                foreach (var name in new[] {
                    "CanQuit", "CanRaise", "HasTrackList", "Identity", "DesktopEntry",
                    "SupportedUriSchemes", "SupportedMimeTypes", "CanSetFullscreen", "Fullscreen"
                }) {
                    if (TryGetProperty(interfaceName, name, out var value)) values[name] = value;
                }
            }
            else if (interfaceName == PlayerInterface) {
                foreach (var name in new[] {
                    "PlaybackStatus", "LoopStatus", "Rate", "Shuffle", "Metadata", "Volume",
                    "Position", "MinimumRate", "MaximumRate", "CanGoNext", "CanGoPrevious",
                    "CanPlay", "CanPause", "CanSeek", "CanControl"
                }) {
                    if (TryGetProperty(interfaceName, name, out var value)) values[name] = value;
                }
            }

            return values;
        }

        private void ApplyProperty(string interfaceName, string propertyName, VariantValue value) {
            if (interfaceName != PlayerInterface) return;
            try {
                switch (propertyName) {
                    case "Volume":
                        owner.EmitCommand(new SystemMediaCommand(
                            SystemMediaCommandKind.Volume,
                            Number: Math.Clamp(value.GetDouble(), 0, 1)));
                        break;
                    case "Shuffle":
                        owner.EmitCommand(new SystemMediaCommand(
                            SystemMediaCommandKind.Shuffle,
                            Flag: value.GetBool()));
                        break;
                    case "LoopStatus":
                        owner.EmitCommand(new SystemMediaCommand(
                            SystemMediaCommandKind.Repeat,
                            Repeat: value.GetString() switch {
                                "Track" => SystemMediaRepeatMode.Track,
                                "Playlist" => SystemMediaRepeatMode.Playlist,
                                _ => SystemMediaRepeatMode.Off
                            }));
                        break;
                }
            }
            catch (Exception) {
                // 第三方客户端发来的坏属性值，忽略。
            }
        }

        private static void ReplyEmpty(MethodContext context) {
            if (context.NoReplyExpected) return;
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }

        private static TimeSpan ClampPosition(TimeSpan position, SystemMediaSnapshot? snapshot) {
            if (position < TimeSpan.Zero) return TimeSpan.Zero;
            if (snapshot is not null && position > snapshot.Duration) return snapshot.Duration;
            return position;
        }

        private static long ToMicroseconds(TimeSpan position) =>
            (long)Math.Max(0, position.TotalMilliseconds * 1000);
    }

    private SystemMediaSnapshot? CurrentSnapshot {
        get {
            lock (_sync) return _snapshot;
        }
    }
}
