using System.Net;
using System.Net.Http;

namespace AudioVault.Core.Sources;

/// <summary>What went wrong, as a fixed category: unlike an exception's message, it never holds a title, path or address.</summary>
public enum FailureKind
{
    Other,
    DownloadFailed,
    DownloadIncomplete,
    SourceUnavailable,
    SourceBlocked,
    SourceLayoutChanged,
    NoAccount,
    Network,
    Timeout,
    NoAudio,
    ArchiveRejected,
    ArchiveEntryOutsideFolder,
    ConversionFailed,
    ConversionUnavailable,
    Disk,
    DiskFull,
    FileAccess,
    CacheFolderConflict,
    DownloadLimit,
    NotFound,
}

/// <param name="ExceptionType">The exception's class name, e.g. "IOException".</param>
public sealed record FailureInfo(FailureKind Kind, string ExceptionType, int? HttpStatus)
{
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);
    private const int NoSpaceErrno = 28;

    public static FailureInfo From(Exception ex)
    {
        int? status = null;
        for (var e = (Exception?)ex; e is not null && status is null; e = e.InnerException)
        {
            status = e switch
            {
                SourceException { HttpStatus: { } s } => s,
                HttpRequestException { StatusCode: { } s } => (int)s,
                _ => null,
            };
        }

        return new FailureInfo(Classify(ex), ex.GetType().Name, status);
    }

    private static FailureKind Classify(Exception ex)
    {
        switch (ex)
        {
            case SourceException { Kind: { } kind }:
                return kind;
            case Media.ConversionUnavailableException:
                return FailureKind.ConversionUnavailable;
            case Media.ConversionFailedException:
                return FailureKind.ConversionFailed;
            case Media.InsufficientDiskSpaceException:
                return FailureKind.DiskFull;
            case Media.CacheFolderConflictException:
                return FailureKind.CacheFolderConflict;
            case Policy.QuotaExceededException:
                return FailureKind.DownloadLimit;
            case Media.EntryNotFoundException:
                return FailureKind.NotFound;
            case SourceFormatChangedException:
                return FailureKind.SourceLayoutChanged;
        }

        // A wrapper says little on its own; what it wraps may say more.
        for (var e = ex.InnerException; e is not null; e = e.InnerException)
        {
            if (Low(e) is { } inner)
            {
                return inner;
            }
        }

        return Low(ex) ?? ex switch
        {
            SourceUnavailableException => FailureKind.SourceUnavailable,
            SourceException => FailureKind.DownloadFailed,
            _ => FailureKind.Other,
        };
    }

    private static FailureKind? Low(Exception e) => e switch
    {
        HttpRequestException or HttpIOException or WebException or System.Net.Sockets.SocketException => FailureKind.Network,
        TimeoutException or OperationCanceledException => FailureKind.Timeout,
        UnauthorizedAccessException => FailureKind.FileAccess,
        IOException io when io.HResult is DiskFullHResult or HandleDiskFullHResult or NoSpaceErrno => FailureKind.DiskFull,
        IOException => FailureKind.Disk,
        _ => null,
    };
}
