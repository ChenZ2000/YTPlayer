<?php
declare(strict_types=1);

require __DIR__ . '/lib/bootstrap.php';

ini_set('display_errors', '0');
set_time_limit(60);

$actionRaw = $_GET['action'] ?? 'check';
$action = is_string($actionRaw) ? strtolower($actionRaw) : 'check';

$progress = new ProgressTracker();

try {
    if ($action === 'download') {
        $progress->start('download', 'Preparing cached asset', 25);
        $assetInput = $_REQUEST['asset'] ?? null;
        $assetPattern = is_string($assetInput) ? trim($assetInput) : null;
        yt_handle_download_request($assetPattern, $progress, 'update-download');
        exit;
    }

    handleCheck($progress);
} catch (Throwable $exception) {
    error_log('[update.php] ' . $exception->getMessage());
    yt_send_json(500, [
        'status' => 'error',
        'message' => 'Update service error',
        'detail' => $exception->getMessage(),
    ], $progress);
}

function handleCheck(ProgressTracker $progress): void
{
    yt_ensure_cache_structure();

    $progress->start('metadata', '读取缓存版本信息', 20);
    $metadata = yt_read_metadata();

    if ($metadata === null) {
        yt_background_refresh('update-check', ['force' => true]);
        $progress->update('metadata', '首次载入，正在检查最新版本', 45);
        $metadata = yt_wait_for_metadata(null, 3, false);
        if ($metadata === null) {
            sendPendingResponse($progress, '版本信息正在准备，请稍后重试。');
        }
    }

    if ($metadata !== null && yt_metadata_is_stale($metadata)) {
        yt_background_refresh('update-check');
        $progress->update('metadata', '检测到缓存过期，正在刷新', 70);
        $fresh = yt_wait_for_metadata($metadata, 3, true);
        if ($fresh !== null) {
            $metadata = $fresh;
        }
    }

    if ($metadata === null) {
        sendPendingResponse($progress, '版本信息尚未准备好，请稍后重试。');
    }

    $titleRefreshMessage = '检测到最新版本标题有更新，正在刷新缓存。';
    if ($metadata !== null && yt_release_title_refresh_pending($metadata)) {
        $progress->update('metadata', '检测到版本标题更新，正在刷新缓存', 80);
        yt_background_refresh('title-sync', [
            'mode' => 'meta',
            'force' => true,
        ]);
        sendPendingResponse($progress, $titleRefreshMessage);
    }

    if ($metadata !== null && yt_maybe_queue_release_title_refresh($metadata)) {
        $progress->update('metadata', '检测到版本标题更新，正在刷新缓存', 80);
        sendPendingResponse($progress, $titleRefreshMessage);
    }

    $release = $metadata['release'] ?? [];
    $latestTag = (string)($release['tag'] ?? '');
    $latestVersion = normalizeVersion($latestTag);

    $clientVersionInput = $_REQUEST['version'] ?? null;
    $clientVersion = normalizeVersion(is_string($clientVersionInput) ? $clientVersionInput : null);
    $updateAvailable = null;
    if ($clientVersion !== null && $latestVersion !== null) {
        $updateAvailable = version_compare($latestVersion, $clientVersion, '>');
    }

    $primaryAsset = $metadata !== null ? yt_select_asset_record($metadata) : null;
    $cacheReady = $primaryAsset !== null ? yt_asset_is_ready($primaryAsset) : false;
    if ($primaryAsset !== null && !$cacheReady) {
        yt_background_refresh('update-check', [
            'asset' => $primaryAsset['name'] ?? null,
            'force' => true,
        ]);
    }

    $assetsPayload = [];
    foreach ($metadata['assets'] ?? [] as $asset) {
        $name = (string)($asset['name'] ?? '');
        $assetsPayload[] = [
            'id' => (int)($asset['id'] ?? 0),
            'name' => $name,
            'size' => (int)($asset['size'] ?? 0),
            'contentType' => $asset['content_type'] ?? 'application/octet-stream',
            'updatedAt' => $asset['updated_at'] ?? null,
            'ready' => yt_asset_is_ready($asset),
            'download' => buildDownloadUrl($name),
        ];
    }

    $progress->complete(
        'metadata',
        $cacheReady ? '版本信息已准备' : '版本信息已获取，缓存正在准备',
        100
    );

    $status = $cacheReady ? 'ok' : 'preparing';
    $message = $cacheReady
        ? '最新版本已准备就绪。'
        : ($latestTag !== '' ? '正在准备最新版本安装包。' : '正在准备安装包。');

    $payload = [
        'status' => $status,
        'message' => $message,
        'progress' => $progress->toArray(),
        'timestamp' => gmdate('c'),
        'data' => [
            'clientVersion' => $clientVersion,
            'latest' => [
                'tag' => $latestTag,
                'semantic' => $latestVersion,
                'name' => $release['name'] ?? $latestTag,
                'publishedAt' => $release['published_at'] ?? null,
                'notes' => trim((string)($release['body'] ?? '')),
                'releasePage' => $release['url'] ?? buildReleaseUrl($latestTag),
            ],
            'updateAvailable' => $updateAvailable,
            'assets' => $assetsPayload,
            'cache' => [
                'ready' => $cacheReady,
                'asset' => $primaryAsset === null ? null : [
                    'id' => (int)($primaryAsset['id'] ?? 0),
                    'name' => $primaryAsset['name'] ?? null,
                ],
            ],
            'refresh' => $metadata['refresh'] ?? yt_read_refresh_status(),
        ],
        'nextPollAfter' => $cacheReady ? 0 : 4,
    ];

    yt_send_json(200, $payload, $progress);
}

function sendPendingResponse(ProgressTracker $progress, string $message): void
{
    $payload = [
        'status' => 'pending',
        'message' => $message,
        'progress' => $progress->toArray(),
        'timestamp' => gmdate('c'),
        'refresh' => yt_read_refresh_status(),
        'nextPollAfter' => 4,
    ];
    yt_send_json(202, $payload, $progress);
}

function buildDownloadUrl(?string $assetName): string
{
    $scheme = (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off') ? 'https' : 'http';
    $host = $_SERVER['HTTP_HOST'] ?? ($_SERVER['SERVER_NAME'] ?? 'localhost');
    $scriptName = $_SERVER['SCRIPT_NAME'] ?? '/update.php';
    $directory = str_replace('\\', '/', dirname($scriptName));
    if ($directory === '\\' || $directory === '/' || $directory === '.') {
        $directory = '';
    }
    $path = $directory === '' ? '/download.php' : $directory . '/download.php';
    $url = sprintf('%s://%s%s', $scheme, $host, $path);
    if ($assetName !== null && $assetName !== '') {
        $url .= (str_contains($url, '?') ? '&' : '?') . 'asset=' . rawurlencode($assetName);
    }
    return $url;
}

function buildReleaseUrl(?string $tag): ?string
{
    if (!is_string($tag) || $tag === '') {
        return null;
    }
    return 'https://github.com/ChenZ2000/YTPlayer/releases/tag/' . rawurlencode($tag);
}

function normalizeVersion(?string $version): ?string
{
    if ($version === null) {
        return null;
    }
    $trimmed = trim($version);
    if ($trimmed === '') {
        return null;
    }
    return ltrim($trimmed, 'vV');
}
