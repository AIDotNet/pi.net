// ============================================================================
// POSIX file metadata — the lstat/getuid slice experimental/server.ts needs (4g-3d-4b)
// ============================================================================
//
// `ensurePrivateServerDirectory` must answer two questions Node answers with `lstat` and
// `process.getuid`: is the path a directory owned by the current user? .NET has no managed API for
// file owners, so the two reads go through libc; the mode and the directory check use the managed
// Unix file-mode APIs instead (they exist on .NET and are correct on both Linux and macOS).
//
// This branch only runs off Windows: the Windows path skips the POSIX checks entirely (C369), the
// same way the Unix listener and transport stay usable there.

using System.Runtime.InteropServices;

namespace Pi.CodingAgent.Experimental;

/// <summary>The POSIX metadata one path exposes without following a final symlink.</summary>
internal static class PosixStat
{
    /// <summary>One lstat result.</summary>
    /// <param name="IsDirectory">S_ISDIR of the mode.</param>
    /// <param name="Uid">The owning user id (st_uid).</param>
    public readonly record struct Entry(bool IsDirectory, uint Uid);

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int LstatRaw(string path, byte[] buffer);

    [DllImport("libc", SetLastError = true, EntryPoint = "getuid")]
    private static extern uint GetUidRaw();

    /// <summary>The current process uid. TS <c>process.getuid()</c>.</summary>
    public static uint CurrentUid => GetUidRaw();

    /// <summary>
    /// lstat one path, or null when it does not exist or libc is unavailable. TS's
    /// <c>lstat</c> (no symlink follow on the final component).
    /// </summary>
    public static Entry? Lstat(string path)
    {
        try
        {
            var buffer = new byte[256];
            return LstatRaw(path, buffer) == 0 ? FromBuffer(buffer) : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // 非 glibc/musl 平台（或受限运行时）：拿不到属主，调用方按「无法校验」处理。
            return null;
        }
    }

    private static Entry FromBuffer(byte[] buffer)
    {
        int mode;
        uint uid;
        if (OperatingSystem.IsMacOS())
        {
            // XNU struct stat (x86_64/arm64): st_dev@0(4) st_mode@4(2) st_nlink@6(2)
            // st_ino@8(8) st_uid@16(4) st_gid@20(4) …
            mode = BitConverter.ToUInt16(buffer, 4);
            uid = BitConverter.ToUInt32(buffer, 16);
        }
        else
        {
            // glibc struct stat (x86_64/arm64): st_dev@0(8) st_ino@8(8) st_nlink@16(8)
            // st_mode@24(4) st_uid@28(4) st_gid@32(4) …
            mode = BitConverter.ToInt32(buffer, 24);
            uid = BitConverter.ToUInt32(buffer, 28);
        }

        return new Entry((mode & 0xF000) == 0x4000, uid);
    }
}
