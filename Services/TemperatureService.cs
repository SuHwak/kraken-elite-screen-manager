using System;
using System.IO;
using System.Linq;

namespace KrakenEliteScreenManager.Services;

public enum TempSource
{
    Coolant,
    Cpu,
}

/// <summary>
/// Reads temperatures straight from /sys/class/hwmon — no sudo required.
/// hwmon indices shuffle across reboots, so we resolve each chip by its
/// reported name once at construction.
/// </summary>
public class TemperatureService
{
    private const string HwmonRoot = "/sys/class/hwmon";
    private static readonly string[] CoolantChipNames =
    {
        "kraken2023elite",
        "kraken2024elitergb",
        "nzxt_kraken",
        "nzxt-kraken3",
    };

    private readonly string? _coolantInput;
    private readonly string? _cpuInput;
    private readonly Func<double?>? _coolantFallback;

    public TemperatureService(Func<double?>? coolantFallback = null)
    {
        // Kernel names vary by distro/driver version; detect common Kraken aliases.
        _coolantInput = FindCoolantInput();
        // CPU package temp: k10temp (AMD) or coretemp (Intel)
        _cpuInput = FindTempInput("k10temp", "temp1") ?? FindTempInput("coretemp", "temp1");
        // GPU temp is auto-detected (nvidia-smi or amdgpu) via SystemStats, not hwmon.
        _coolantFallback = coolantFallback;
    }

    public bool IsAvailable(TempSource source) => InputFor(source) is not null;

    /// <summary>Returns the temperature in °C, or null if the sensor is unavailable/unreadable.</summary>
    public double? Read(TempSource source)
    {
        var path = InputFor(source);

        if (path is not null)
        {
            try
            {
                var raw = File.ReadAllText(path).Trim();
                // hwmon reports milli-degrees Celsius
                if (long.TryParse(raw, out var milli))
                    return milli / 1000.0;
            }
            catch
            {
                // sensor vanished or unreadable — treat as unavailable
            }
        }

        if (source == TempSource.Coolant && _coolantFallback is not null)
        {
            try { return _coolantFallback(); }
            catch { return null; }
        }

        return null;
    }

    private string? InputFor(TempSource source) => source switch
    {
        TempSource.Coolant => _coolantInput,
        TempSource.Cpu => _cpuInput,
        _ => null,
    };

    private static string? FindTempInput(string chipName, string tempPrefix)
    {
        if (!Directory.Exists(HwmonRoot)) return null;

        foreach (var dir in Directory.GetDirectories(HwmonRoot))
        {
            try
            {
                var namePath = Path.Combine(dir, "name");
                if (!File.Exists(namePath)) continue;

                var name = File.ReadAllText(namePath).Trim();
                if (!string.Equals(name, chipName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var input = Path.Combine(dir, $"{tempPrefix}_input");
                if (File.Exists(input)) return input;
            }
            catch
            {
                // skip unreadable hwmon entries
            }
        }

        return null;
    }

    private static string? FindCoolantInput()
    {
        foreach (var name in CoolantChipNames)
        {
            var input = FindTempInput(name, "temp1");
            if (input is not null) return input;
        }

        if (!Directory.Exists(HwmonRoot)) return null;

        foreach (var dir in Directory.GetDirectories(HwmonRoot))
        {
            try
            {
                var namePath = Path.Combine(dir, "name");
                if (!File.Exists(namePath)) continue;

                var chip = File.ReadAllText(namePath).Trim();
                bool looksKraken = chip.Contains("kraken", StringComparison.OrdinalIgnoreCase)
                    || chip.Contains("nzxt", StringComparison.OrdinalIgnoreCase);
                if (!looksKraken) continue;

                var labeled = Directory.GetFiles(dir, "temp*_label");
                foreach (var labelPath in labeled)
                {
                    var label = File.ReadAllText(labelPath).Trim();
                    bool coolantLabel = label.Contains("liquid", StringComparison.OrdinalIgnoreCase)
                        || label.Contains("coolant", StringComparison.OrdinalIgnoreCase)
                        || label.Contains("water", StringComparison.OrdinalIgnoreCase);
                    if (!coolantLabel) continue;

                    var inputPath = Path.Combine(dir,
                        Path.GetFileNameWithoutExtension(labelPath).Replace("_label", "_input"));
                    if (File.Exists(inputPath)) return inputPath;
                }

                var temp1 = Path.Combine(dir, "temp1_input");
                if (File.Exists(temp1)) return temp1;
            }
            catch
            {
                // skip unreadable hwmon entries
            }
        }

        return null;
    }
}
