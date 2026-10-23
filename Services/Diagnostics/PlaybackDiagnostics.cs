using System;
using System.IO;
using System.Runtime.InteropServices;
using Avance_Player.Services.Playback;
using LibVLCSharp.Shared;

namespace Avance_Player.Services.Diagnostics;

/// <summary>
/// Console-only checks for local playback. This intentionally does not start Avalonia, so it can
/// run on a headless machine or over SSH when validating a native LibVLC install.
/// </summary>
public static class PlaybackDiagnostics {
    public static int Run() {
        Console.WriteLine("Avance Player playback diagnostics");
        Console.WriteLine($"OS          : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Base folder : {AppContext.BaseDirectory}");
        Console.WriteLine($"State file  : {Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AvancePlayer",
            "state.json")}");

        using var runtime = new LibVlcRuntime(["--no-video", "--quiet"]);
        var libVlc = runtime.TryGet(out var error);
        if (libVlc is null) {
            Console.Error.WriteLine("LibVLC      : unavailable");
            Console.Error.WriteLine(error);
            return 1;
        }

        Console.WriteLine($"LibVLC      : {libVlc.Version} ({libVlc.Changeset})");
        Console.WriteLine($"Compiler    : {libVlc.LibVLCCompiler}");

        WriteAudioOutputs(libVlc);
        WriteAudioFilters(libVlc);
        return 0;
    }

    private static void WriteAudioOutputs(LibVLC libVlc) {
        Console.WriteLine("Audio outputs:");
        try {
            var outputs = libVlc.AudioOutputs;
            var count = 0;
            if (outputs is not null) {
                foreach (var output in outputs) {
                    count++;
                    Console.WriteLine($"  - {output.Name}: {output.Description}");
                    WriteAudioDevices(libVlc, output.Name);
                }
            }

            if (count == 0) Console.WriteLine("  (none reported)");
        }
        catch (Exception exception) {
            Console.WriteLine($"  (enumeration failed: {exception.Message})");
        }
    }

    private static void WriteAudioDevices(LibVLC libVlc, string outputName) {
        try {
            var devices = libVlc.AudioOutputDevices(outputName);
            if (devices is null) return;
            foreach (var device in devices) {
                Console.WriteLine($"      {device.DeviceIdentifier}: {device.Description}");
            }
        }
        catch (Exception exception) {
            Console.WriteLine($"      (device enumeration failed: {exception.Message})");
        }
    }

    private static void WriteAudioFilters(LibVLC libVlc) {
        Console.WriteLine("Audio filters:");
        try {
            var filters = libVlc.AudioFilters;
            var count = 0;
            if (filters is not null) {
                foreach (var filter in filters) {
                    count++;
                    Console.WriteLine($"  - {filter.ShortName}: {filter.LongName}");
                }
            }

            if (count == 0) Console.WriteLine("  (none reported)");
        }
        catch (Exception exception) {
            Console.WriteLine($"  (enumeration failed: {exception.Message})");
        }
    }
}
