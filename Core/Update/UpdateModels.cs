using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using Newtonsoft.Json;

namespace YTPlayer.Update
{
    public sealed class UpdateProgressStage
    {
        [JsonProperty("step")]
        public string Step { get; set; } = string.Empty;

        [JsonProperty("state")]
        public string State { get; set; } = string.Empty;

        [JsonProperty("progress")]
        public int Progress { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{Progress:D3}% [{State}] {Message}";
        }
    }

    public sealed class UpdateAsset
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; } = string.Empty;

        [JsonProperty("updatedAt")]
        public DateTime? UpdatedAt { get; set; }

        [JsonProperty("download")]
        public string Download { get; set; } = string.Empty;
    }

    public sealed class UpdateReleaseInfo
    {
        [JsonProperty("tag")]
        public string Tag { get; set; } = string.Empty;

        [JsonProperty("semantic")]
        public string SemanticVersion { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("publishedAt")]
        public DateTime? PublishedAt { get; set; }

        [JsonProperty("notes")]
        public string Notes { get; set; } = string.Empty;

        [JsonProperty("releasePage")]
        public string ReleasePage { get; set; } = string.Empty;
    }

    public sealed class UpdateCheckData
    {
        [JsonProperty("clientVersion")]
        public string ClientVersion { get; set; } = string.Empty;

        [JsonProperty("latest")]
        public UpdateReleaseInfo? Latest { get; set; }

        [JsonProperty("updateAvailable")]
        public bool UpdateAvailable { get; set; }

        [JsonProperty("assets")]
        public List<UpdateAsset>? Assets { get; set; }
    }

    public sealed class UpdateErrorInfo
    {
        [JsonProperty("code")]
        public string Code { get; set; } = string.Empty;

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class UpdateCheckResponse
    {
        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("timestamp")]
        public DateTime? Timestamp { get; set; }

        [JsonProperty("progress")]
        public List<UpdateProgressStage>? Progress { get; set; }

        [JsonProperty("data")]
        public UpdateCheckData? Data { get; set; }

        [JsonProperty("error")]
        public UpdateErrorInfo? Error { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }
    }

    public sealed class UpdateCheckResult
    {
        public UpdateCheckResult(UpdateCheckResponse response, IReadOnlyList<UpdateProgressStage> headerProgress)
        {
            Response = response ?? throw new ArgumentNullException(nameof(response));
            HeaderProgress = headerProgress ?? Array.Empty<UpdateProgressStage>();
        }

        public UpdateCheckResponse Response { get; }

        public IReadOnlyList<UpdateProgressStage> HeaderProgress { get; }

        public IReadOnlyList<UpdateProgressStage> CombinedProgress
        {
            get
            {
                if (Response.Progress != null && Response.Progress.Count > 0)
                {
                    return Response.Progress;
                }

                return HeaderProgress;
            }
        }
    }

    public sealed class UpdatePlan
    {
        [JsonProperty("currentVersion")]
        public string CurrentVersion { get; set; } = string.Empty;

        [JsonProperty("targetVersion")]
        public string TargetVersion { get; set; } = string.Empty;

        [JsonProperty("targetTag")]
        public string TargetTag { get; set; } = string.Empty;

        [JsonProperty("releaseTitle")]
        public string ReleaseTitle { get; set; } = string.Empty;

        [JsonProperty("releaseNotes")]
        public string ReleaseNotes { get; set; } = string.Empty;

        [JsonProperty("releasePage")]
        public string ReleasePage { get; set; } = string.Empty;

        [JsonProperty("publishedAt")]
        public DateTime? PublishedAt { get; set; }

        [JsonProperty("assetName")]
        public string AssetName { get; set; } = string.Empty;

        [JsonProperty("assetDownloadUrl")]
        public string AssetDownloadUrl { get; set; } = string.Empty;

        [JsonProperty("assetSize")]
        public long AssetSize { get; set; }

        [JsonProperty("assetContentType")]
        public string AssetContentType { get; set; } = string.Empty;

        [JsonProperty("assetUpdatedAt")]
        public DateTime? AssetUpdatedAt { get; set; }

        [JsonProperty("createdAt")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public string DisplayVersion
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(TargetVersion))
                {
                    return TargetVersion;
                }

                if (!string.IsNullOrWhiteSpace(TargetTag))
                {
                    return TargetTag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                        ? TargetTag.Substring(1)
                        : TargetTag;
                }

                return string.Empty;
            }
        }

        public void SaveTo(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        public static UpdatePlan LoadFrom(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("未找到更新计划文件", filePath);
            }

            var json = File.ReadAllText(filePath);
            var plan = JsonConvert.DeserializeObject<UpdatePlan>(json);
            if (plan == null)
            {
                throw new InvalidOperationException("无法解析更新计划文件");
            }

            return plan;
        }

        public static UpdatePlan FromResponse(UpdateCheckResponse response, UpdateAsset asset, string currentVersion)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            var latest = response.Data?.Latest;

            return new UpdatePlan
            {
                CurrentVersion = currentVersion ?? string.Empty,
                TargetVersion = latest?.SemanticVersion ?? string.Empty,
                TargetTag = latest?.Tag ?? string.Empty,
                ReleaseTitle = latest?.Name ?? string.Empty,
                ReleaseNotes = latest?.Notes ?? string.Empty,
                ReleasePage = latest?.ReleasePage ?? string.Empty,
                PublishedAt = latest?.PublishedAt,
                AssetName = asset.Name ?? string.Empty,
                AssetDownloadUrl = asset.Download ?? string.Empty,
                AssetSize = asset.Size,
                AssetContentType = asset.ContentType ?? string.Empty,
                AssetUpdatedAt = asset.UpdatedAt,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public sealed class UpdateDownloadProgress
    {
        public UpdateDownloadProgress(long bytesReceived, long? totalBytes)
        {
            BytesReceived = bytesReceived;
            TotalBytes = totalBytes;
        }

        public long BytesReceived { get; }

        public long? TotalBytes { get; }

        public double? Percentage
        {
            get
            {
                if (!TotalBytes.HasValue || TotalBytes <= 0)
                {
                    return null;
                }

                return BytesReceived * 100d / TotalBytes.Value;
            }
        }

        public string ToHumanReadable()
        {
            string received = FormatBytes(BytesReceived);
            string total = TotalBytes.HasValue ? FormatBytes(TotalBytes.Value) : "未知";
            if (Percentage.HasValue)
            {
                return $"{received} / {total} ({Percentage.Value.ToString("0.0", CultureInfo.InvariantCulture)}%)";
            }

            return $"{received} / {total}";
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int index = 0;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }

            return $"{value:0.##}{units[index]}";
        }
    }

    public sealed class UpdateServiceException : Exception
    {
        public UpdateServiceException(string message)
            : base(message)
        {
        }

        public UpdateServiceException(string message, HttpStatusCode statusCode, string? payload = null)
            : base(message)
        {
            StatusCode = statusCode;
            Payload = payload;
        }

        public HttpStatusCode? StatusCode { get; }

        public string? Payload { get; }
    }
}
