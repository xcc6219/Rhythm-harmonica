using System;
using System.Runtime.InteropServices;

namespace HarmonicaStudio
{
    // Read only process-token metadata. No target memory or game code is accessed.
    internal static class WindowsInputAccess
    {
        internal static bool CanSendTo(uint targetProcessId, out string reason)
        {
            int ownLevel, targetLevel;
            if (!TryIntegrity(GetCurrentProcess(), out ownLevel))
            { reason = "权限：无法确认本软件的 Windows 输入权限，未发送演奏输入。"; return false; }
            IntPtr target = OpenProcess(0x1000, false, targetProcessId);
            if (target == IntPtr.Zero)
            { reason = "权限：无法查询游戏窗口的 Windows 权限，未发送演奏输入。"; return false; }
            try
            {
                if (!TryIntegrity(target, out targetLevel))
                { reason = "权限：无法确认游戏窗口的 Windows 输入权限，未发送演奏输入。"; return false; }
                if (!AllowsIntegrity(ownLevel, targetLevel))
                {
                    reason = "权限：游戏的权限高于本软件，Windows 会阻止模拟输入。请停止演奏，在托盘的“热键与运行状态”中按“管理员方式重启”，再回游戏重试。";
                    return false;
                }
                reason = null; return true;
            }
            finally { CloseHandle(target); }
        }

        internal static bool AllowsIntegrity(int ownLevel, int targetLevel)
        { return ownLevel >= 0 && targetLevel >= 0 && ownLevel >= targetLevel; }

        internal static string OwnDescription()
        {
            int level;
            if (!TryIntegrity(GetCurrentProcess(), out level)) return "无法查询";
            return level >= 0x3000 ? "管理员权限" : level >= 0x2000 ? "普通权限" : "受限权限";
        }

        private static bool TryIntegrity(IntPtr process, out int level)
        {
            level = -1; IntPtr token;
            if (!OpenProcessToken(process, 8, out token)) return false;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int required;
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out required);
                if (required <= 0 || required > 65536) return false;
                buffer = Marshal.AllocHGlobal(required);
                if (!GetTokenInformation(token, 25, buffer, required, out required)) return false;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero || !IsValidSid(sid)) return false;
                IntPtr countAddress = GetSidSubAuthorityCount(sid);
                if (countAddress == IntPtr.Zero) return false;
                byte count = Marshal.ReadByte(countAddress);
                if (count == 0) return false;
                IntPtr value = GetSidSubAuthority(sid, (uint)(count - 1));
                if (value == IntPtr.Zero) return false;
                level = Marshal.ReadInt32(value); return level >= 0;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                CloseHandle(token);
            }
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int type, IntPtr buffer, int size, out int returned);
        [DllImport("advapi32.dll")] private static extern bool IsValidSid(IntPtr sid);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    }
}
