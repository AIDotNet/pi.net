using Pi.Chord.Context;
using Pi.Durable.Env;

namespace Pi.Env;

/// <summary>把守护进程错误与中止检查翻译成 Durable 的 <see cref="FileError"/>。对应 TS <c>errors.ts</c>。</summary>
internal static class RemoteErrors
{
    /// <summary>已中止时返回 <c>aborted</c> 失败值，否则 null。对应 TS <c>abortResult</c>。</summary>
    public static Result<T, FileError>? AbortResult<T>(Context context, string? path = null) =>
        context.AbortSignal is { IsCancellationRequested: true }
            ? Result<T, FileError>.Err(new FileError(FileErrorCode.Aborted, "aborted", path))
            : null;

    /// <summary><c>NodeExecutionEnv</c> 的 Node 错误码到 <see cref="FileErrorCode"/> 的映射。</summary>
    public static FileError ToFileError(Exception error, string? fallbackPath = null)
    {
        if (error is FileError file) return file;
        if (error is not RemoteError remote)
        {
            return new FileError(FileErrorCode.Unknown, error.Message, fallbackPath, error);
        }

        var path = remote.Path ?? fallbackPath;
        var code = remote.Code switch
        {
            "aborted" => FileErrorCode.Aborted,
            "ENOENT" => FileErrorCode.NotFound,
            "EACCES" or "EPERM" => FileErrorCode.PermissionDenied,
            "ENOTDIR" => FileErrorCode.NotDirectory,
            "EISDIR" => FileErrorCode.IsDirectory,
            "EINVAL" or "SYMLINK" or "NOT_REGULAR" => FileErrorCode.Invalid,
            _ => FileErrorCode.Unknown,
        };
        return new FileError(code, remote.Message, path, remote);
    }
}
