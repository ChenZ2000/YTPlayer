<?php
declare(strict_types=1);

require __DIR__ . '/lib/bootstrap.php';

ini_set('display_errors', '0');
set_time_limit(60);

$progress = new ProgressTracker();
$progress->start('init', 'Preparing download', 10);

$assetPatternRaw = $_GET['asset'] ?? null;
$assetPattern = is_string($assetPatternRaw) ? trim($assetPatternRaw) : null;

try {
    yt_handle_download_request($assetPattern, $progress, 'download');
} catch (Throwable $exception) {
    error_log('[download.php] ' . $exception->getMessage());
    yt_send_client_message(500, '下载服务暂不可用，请稍后重试。', [
        'error' => 'download_unavailable',
        'detail' => $exception->getMessage(),
    ], $progress);
}
