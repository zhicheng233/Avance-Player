using System;
using System.IO;
using System.Text.Json;

namespace Avance_Player.Services.Persistence;

/// <summary>
/// 很小的 JSON 状态存储。写的时候先落临时文件，崩了也不会留下写一半的 state；
/// 上一个文件留成 <c>state.json.bak</c>。
/// </summary>
public sealed class JsonPlayerStateStore : IPlayerStateStore {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object _sync = new();
    private readonly string _path;

    public JsonPlayerStateStore(string? path = null) {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AvancePlayer",
            "state.json");
    }

    public string StatePath => _path;
    public string? LastError { get; private set; }

    public PlayerState? Load() {
        lock (_sync) {
            LastError = null;
            try {
                if (!File.Exists(_path)) return null;

                var json = File.ReadAllText(_path);
                var state = JsonSerializer.Deserialize<PlayerState>(json, JsonOptions);
                if (state is null) {
                    LastError = "The state file is empty or invalid.";
                    return null;
                }

                if (state.Version > PlayerState.CurrentVersion) {
                    LastError = $"The state file version {state.Version} is newer than this app supports.";
                    return null;
                }

                if (state.Playback is null || state.Library is null || state.Ui is null) {
                    LastError = "The state file is missing one of its required sections.";
                    return null;
                }

                return state;
            }
            catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or JsonException
                                                  or NotSupportedException) {
                LastError = exception.Message;
                return null;
            }
        }
    }

    public bool Save(PlayerState state) {
        lock (_sync) {
            LastError = null;
            string? tempPath = null;
            try {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(state, JsonOptions);
                tempPath = _path + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(_path)) {
                    var backupPath = _path + ".bak";
                    try {
                        File.Copy(_path, backupPath, overwrite: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                        // 备份能成就成。.bak 被占用或者只读，也不能把主状态的写挡住。
                    }
                }

                File.Move(tempPath, _path, overwrite: true);
                tempPath = null;
                return true;
            }
            catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or JsonException
                                                  or NotSupportedException) {
                LastError = exception.Message;
                return false;
            }
            finally {
                if (tempPath is not null) {
                    try {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch (Exception) {
                        // 清理也一样，失败就算了。
                    }
                }
            }
        }
    }
}
