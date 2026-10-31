using Microsoft.Win32;

namespace BottomScreen;

// Windows' per-executable graphics preference, observed on this machine (build 26300):
// turning off Auto HDR in Settings writes AutoHDREnable=48. This registry schema is
// not a documented DXGI API. Keep the change narrow and report write/readback failures.
internal sealed class AutoHdrPreference
{
    internal const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private readonly string executable;
    public string? Error { get; private set; }
    public bool Changed { get; private set; }

    public AutoHdrPreference(bool allow, string executable)
    {
        this.executable = Path.GetFullPath(executable);
        Apply(allow);
    }

    internal static string Merge(string? previous, bool allow)
    {
        // Allow means inherit Windows' global policy, rather than force HDR on.
        var tokens = (previous ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.Split('=', 2)[0].Trim().Equals("AutoHDREnable", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!allow) tokens.Add("AutoHDREnable=48");
        return tokens.Count == 0 ? "" : string.Join(';', tokens) + ";";
    }

    public bool Apply(bool allow)
    {
        Changed = false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            object? raw = key.GetValue(executable);
            if (raw != null && raw is not string)
                throw new InvalidOperationException("目标程序的 Windows 图形配置不是文本，未覆盖。");
            string? before = raw as string;
            string after = Merge(before, allow);
            if (!string.Equals(before ?? "", after, StringComparison.Ordinal))
            {
                if (after.Length == 0) key.DeleteValue(executable, throwOnMissingValue: false);
                else key.SetValue(executable, after, RegistryValueKind.String);
                Changed = true;
            }
            if (!string.Equals(key.GetValue(executable) as string ?? "", after, StringComparison.Ordinal))
                throw new IOException("Windows 图形设置读回与请求不一致。");
            Error = null;
            return true;
        }
        catch (Exception e)
        {
            Error = "自动 HDR 设置未能写入：" + e.Message;
            return false;
        }
    }
}
