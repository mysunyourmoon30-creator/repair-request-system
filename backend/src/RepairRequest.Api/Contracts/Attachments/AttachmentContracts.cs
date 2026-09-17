using RepairRequest.Application.Attachments;

namespace RepairRequest.Api.Contracts.Attachments;

/// <summary>Attachment metadata. Storage references are never returned; the file is only available through the download API.</summary>
public sealed record AttachmentResponse(
    Guid AttachmentId,
    Guid FileAssetId,
    string FileName,
    string MimeType,
    long SizeBytes,
    string MalwareScanStatus,
    DateTime UploadedAt);

public static class AttachmentResponses
{
    public static AttachmentResponse ToResponse(AttachmentDto dto) =>
        new(
            dto.AttachmentId,
            dto.FileAssetId,
            dto.FileName,
            dto.MimeType,
            dto.SizeBytes,
            MalwareScanStatusCodes.ToCode(dto.MalwareScanStatus),
            DateTime.SpecifyKind(dto.UploadedAt, DateTimeKind.Utc));

    public static string DownloadLocation(Guid fileAssetId) => $"/api/v1/files/{fileAssetId}";
}
