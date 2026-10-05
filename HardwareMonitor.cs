using System.Diagnostics;
using System.Globalization;

namespace RunnerRoom;

internal static class HardwareMonitor
{
    internal static async Task<HardwareReadings> ReadAsync(string sys, bool allowPiCommand, CancellationToken cancellation)
    {
        var temperatures = new List<TemperatureReading>(); var throttling = new List<ThrottleReading>(); var batteries = new List<BatteryReading>();
        foreach (var zone in LinuxMetrics.Directories(Path.Combine(sys, "class/thermal"), "thermal_zone*"))
            AddTemperature(temperatures, LinuxMetrics.Read(Path.Combine(zone, "type"), 256) ?? Path.GetFileName(zone), LinuxMetrics.Read(Path.Combine(zone, "temp"), 100));
        bool? lowVoltage = null;
        foreach (var hw in LinuxMetrics.Directories(Path.Combine(sys, "class/hwmon")))
        {
            var name = LinuxMetrics.Read(Path.Combine(hw, "name"), 256) ?? Path.GetFileName(hw);
            foreach (var sensor in LinuxMetrics.Files(hw, "temp*_input"))
            {
                var label = LinuxMetrics.Read(sensor.Replace("_input", "_label"), 256) ?? Path.GetFileName(sensor).Replace("_input", "");
                AddTemperature(temperatures, name + " / " + label, LinuxMetrics.Read(sensor, 100));
            }
            if (name is "rpi_volt" && LinuxMetrics.Read(Path.Combine(hw, "in0_lcrit_alarm"), 100) is { } alarm && alarm is "0" or "1") lowVoltage = alarm == "1";
        }
        foreach (var cpu in LinuxMetrics.Directories(Path.Combine(sys, "devices/system/cpu"), "cpu*").Where(p => LinuxMetrics.Number(Path.GetFileName(p)[3..]) is not null))
        {
            foreach (var name in new[] { "core_throttle_count", "package_throttle_count" })
                if (LinuxMetrics.Number(LinuxMetrics.Read(Path.Combine(cpu, "thermal_throttle", name), 100)) is { } count)
                    throttling.Add(new(Path.GetFileName(cpu) + " / " + name.Replace("_throttle_count", ""), count, null, count > 0));
        }
        foreach (var supply in LinuxMetrics.Directories(Path.Combine(sys, "class/power_supply")))
        {
            if (LinuxMetrics.Read(Path.Combine(supply, "type"), 100) != "Battery" || LinuxMetrics.Read(Path.Combine(supply, "present"), 100) == "0") continue;
            double? Read(string key) => LinuxMetrics.Decimal(LinuxMetrics.Read(Path.Combine(supply, key), 100));
            var percent = Read("capacity"); if (percent > 100) percent = null;
            batteries.Add(new(Path.GetFileName(supply), LinuxMetrics.Read(Path.Combine(supply, "status"), 100) ?? "Unknown", percent,
                Read("energy_now") / 1e6, Read("energy_full") / 1e6, Read("power_now") / 1e6));
        }
        // rpi_volt reports a sticky alarm from the kernel's recent polling window, not instantaneous voltage.
        PiHealth? pi = lowVoltage is not null ? new(null, null, null, null, null, null, "rpi_volt sensor") { RecentVoltageAlarm = lowVoltage } : null;
        if (allowPiCommand)
        {
            var executable = new[] { "/usr/bin/vcgencmd", "/opt/vc/bin/vcgencmd" }.FirstOrDefault(File.Exists);
            if (executable is not null)
            {
                using var process = new Process { StartInfo = new(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
                process.StartInfo.ArgumentList.Add("get_throttled");
                try
                {
                    process.Start();
                    var output = process.StandardOutput.ReadToEndAsync(cancellation); var error = process.StandardError.ReadToEndAsync(cancellation);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(1000);
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
                    var text = await output; await error;
                    if (process.ExitCode == 0 && ParsePi(text) is { } firmware) pi = firmware with { RecentVoltageAlarm = lowVoltage };
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            }
        }
        if (pi?.Throttled is { } active) throttling.Add(new("Raspberry Pi firmware", null, active, pi.ThrottledSinceBoot));
        return new(temperatures.Take(128).ToArray(), throttling.Take(256).ToArray(), pi, batteries.ToArray());
    }
    private static void AddTemperature(List<TemperatureReading> result, string name, string? raw)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value is >= -40000 and <= 150000)
            result.Add(new(LocalRunnerReader.Text(name) ?? "Temperature", value / 1000));
    }
    internal static PiHealth? ParsePi(string text)
    {
        const string prefix = "throttled=0x"; text = text.Trim();
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !uint.TryParse(text[prefix.Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits)) return null;
        return new((bits & 1) != 0, (bits & (1 << 16)) != 0, (bits & 4) != 0, (bits & (1 << 18)) != 0,
            (bits & 2) != 0, (bits & 8) != 0, "vcgencmd get_throttled");
    }
}
