using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Avance_Player.Services.Playback;

/// <summary>
/// 直接跟 libvlccore 打交道的那一小层。libvlc 的公开 API 里既没有音频滤镜链，
/// 也没有移调入口，只能拿 MediaPlayer 的原生对象去读写它的内部变量。
/// 这些符号不属于公开 ABI，取不到的时候上层要能整套退回“不支持”，不能崩。
/// </summary>
internal static class LibVlcNative {
    /// <summary>vlc_variables.h 里的 VLC_VAR_FLOAT。</summary>
    private const int VarTypeFloat = 0x0050;

    private static readonly Lazy<Bindings?> LazyBindings =
        new(LoadBindings, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>符号都拿到了才谈得上改滤镜链。</summary>
    public static bool IsAvailable => LazyBindings.Value is not null;

    /// <summary>
    /// 在对象上创建变量。已经存在时返回 0，重复调用是安全的；
    /// 不先创建直接 var_Set 会拿到 VLC_ENOVAR。
    /// </summary>
    public static int Create(IntPtr target, string name, int type) {
        if (target == IntPtr.Zero || LazyBindings.Value is not { } bindings) return -1;
        try {
            return Marshal.GetDelegateForFunctionPointer<CreateDelegate>(bindings.Create)(target, name, type);
        }
        catch (Exception) {
            return -1;
        }
    }

    /// <summary>写字符串变量。原生侧会自己 dup 一份，返回后就能释放。</summary>
    public static int SetString(IntPtr target, string name, string value) {
        if (target == IntPtr.Zero || LazyBindings.Value is not { } bindings) return -1;

        var text = IntPtr.Zero;
        try {
            text = Marshal.StringToCoTaskMemUTF8(value);
            var payload = new VlcValue { String = text };
            return Marshal.GetDelegateForFunctionPointer<SetDelegate>(bindings.Set)(target, name, payload);
        }
        catch (Exception) {
            return -1;
        }
        finally {
            if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>写浮点变量（音调偏移就是这种）。</summary>
    public static int SetFloat(IntPtr target, string name, float value) {
        if (target == IntPtr.Zero || LazyBindings.Value is not { } bindings) return -1;
        try {
            var payload = new VlcValue { Float = value };
            return Marshal.GetDelegateForFunctionPointer<SetDelegate>(bindings.Set)(target, name, payload);
        }
        catch (Exception) {
            return -1;
        }
    }

    /// <summary>音调滤镜的模块名，能力探测和滤镜链都用它。</summary>
    public const string PitchFilterModule = "scaletempo_pitch";

    /// <summary>pitch-shift 变量的类型（VLC_VAR_FLOAT）。</summary>
    public const int PitchShiftVarType = VarTypeFloat;

    private static Bindings? LoadBindings() {
        foreach (var candidate in EnumerateCandidates()) {
            if (!TryLoad(candidate, out var handle)) continue;
            if (!NativeLibrary.TryGetExport(handle, "var_Create", out var create)) continue;
            if (!NativeLibrary.TryGetExport(handle, "var_Set", out var set)) continue;
            return new Bindings(create, set);
        }

        return null;
    }

    private static bool TryLoad(string candidate, out IntPtr handle) {
        try {
            return NativeLibrary.TryLoad(candidate, out handle);
        }
        catch (Exception) {
            handle = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>
    /// 先试裸名字（系统装的 libvlc 走 loader 路径就能命中），再找便携版常见的几个目录。
    /// 顺序跟 <see cref="LibVlcRuntime"/> 找 runtime 的顺序保持一致。
    /// </summary>
    private static IEnumerable<string> EnumerateCandidates() {
        foreach (var name in LibraryNames) yield return name;

        var baseDirectory = AppContext.BaseDirectory;
        var rid = RuntimeIdentifier;
        foreach (var directory in new[] {
                     Path.Combine(baseDirectory, "libvlc"),
                     Path.Combine(baseDirectory, "libvlc", rid),
                     Path.Combine(baseDirectory, "runtimes", rid, "native")
                 }) {
            foreach (var name in LibraryNames) {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) yield return path;
            }
        }
    }

    private static string[] LibraryNames => OperatingSystem.IsWindows()
        ? ["libvlccore.dll"]
        : OperatingSystem.IsMacOS()
            ? ["libvlccore.dylib", "libvlccore.9.dylib"]
            : ["libvlccore.so.9", "libvlccore.so"];

    private static string RuntimeIdentifier {
        get {
            var architecture = RuntimeInformation.ProcessArchitecture;
            if (OperatingSystem.IsWindows()) {
                return architecture switch {
                    Architecture.X86 => "win-x86",
                    Architecture.Arm64 => "win-arm64",
                    _ => "win-x64"
                };
            }

            if (OperatingSystem.IsMacOS()) {
                return architecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            }

            return architecture switch {
                Architecture.X86 => "linux-x86",
                Architecture.Arm => "linux-arm",
                Architecture.Arm64 => "linux-arm64",
                _ => "linux-x64"
            };
        }
    }

    private sealed record Bindings(IntPtr Create, IntPtr Set);

    /// <summary>
    /// vlc_value_t 是个联合体，最大成员是 8 字节的整数/指针，
    /// 所以这里显式布局、全部压到偏移 0。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct VlcValue {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public IntPtr String;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetDelegate(
        IntPtr target,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        VlcValue value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateDelegate(
        IntPtr target,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int type);
}
