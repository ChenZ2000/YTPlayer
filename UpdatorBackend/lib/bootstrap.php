<?php
declare(strict_types=1);

if (defined('YT_BOOTSTRAPPED')) {
    return;
}

define('YT_BOOTSTRAPPED', true);

if (!extension_loaded('curl')) {
    trigger_error('cURL extension is required for YT service.', E_USER_WARNING);
}

if (!headers_sent()) {
    mb_internal_encoding('UTF-8');
    if (!ini_get('date.timezone')) {
        date_default_timezone_set('UTC');
    }
}

define('YT_ROOT_PATH', dirname(__DIR__));
define('YT_CACHE_PATH', YT_ROOT_PATH . DIRECTORY_SEPARATOR . 'cache');
define('YT_METADATA_FILE', YT_CACHE_PATH . DIRECTORY_SEPARATOR . 'metadata.json');
define('YT_REFRESH_LOCK_FILE', YT_CACHE_PATH . DIRECTORY_SEPARATOR . 'refresh.lock');
define('YT_REFRESH_STATUS_FILE', YT_CACHE_PATH . DIRECTORY_SEPARATOR . 'refresh-status.json');
define('YT_REFRESH_LOG_FILE', YT_CACHE_PATH . DIRECTORY_SEPARATOR . 'refresh.log');
define('YT_REFRESH_SCRIPT', YT_ROOT_PATH . DIRECTORY_SEPARATOR . 'bin' . DIRECTORY_SEPARATOR . 'refresh.php');

define('YT_RELEASE_ENDPOINT', 'https://api.github.com/repos/ChenZ2000/YTPlayer/releases/latest');
define('YT_HTTP_USER_AGENT', 'YTPlayer-Service/2.0 (+https://yt.chenz.cloud)');

define('YT_METADATA_TTL', (int)($_ENV['YT_METADATA_TTL'] ?? 300));
define('YT_REFRESH_GRACE_SECONDS', (int)($_ENV['YT_REFRESH_GRACE'] ?? 900));
define('YT_RELEASE_TITLE_CHECK_INTERVAL', max(0, (int)($_ENV['YT_TITLE_CHECK_INTERVAL'] ?? 15)));
define('YT_SCHEMA_VERSION', 2);
define('YT_TITLE_CHECK_LOCK_FILE', YT_CACHE_PATH . DIRECTORY_SEPARATOR . 'title-check.lock');

$accelPrefix = getenv('YT_ACCEL_REDIRECT_PREFIX');
define('YT_ACCEL_REDIRECT_PREFIX', $accelPrefix === false ? null : rtrim($accelPrefix));

function yt_php_cli_binary(): string
{
    static $binary = null;
    if ($binary !== null) {
        return $binary;
    }

    $env = getenv('YT_PHP_CLI');
    if (is_string($env) && $env !== '') {
        $binary = $env;
        return $binary;
    }

    $binary = PHP_BINARY;
    if (PHP_SAPI === 'cli') {
        return $binary;
    }

    $isWindows = stripos(PHP_OS_FAMILY, 'Windows') === 0;
    $suffixDefault = $isWindows ? 'php.exe' : 'php';
    $suffixVersion = $isWindows ? $suffixDefault : sprintf('php%u.%u', PHP_MAJOR_VERSION, PHP_MINOR_VERSION);

    $candidates = [];
    $bindir = PHP_BINDIR;
    if (is_string($bindir) && $bindir !== '') {
        $dir = rtrim($bindir, DIRECTORY_SEPARATOR) . DIRECTORY_SEPARATOR;
        $candidates[] = $dir . $suffixDefault;
        if (!$isWindows) {
            $candidates[] = $dir . $suffixVersion;
        }
    }

    if (!$isWindows) {
        $candidates[] = '/usr/bin/' . $suffixDefault;
        $candidates[] = '/usr/local/bin/' . $suffixDefault;
        $candidates[] = '/usr/bin/' . $suffixVersion;
        $candidates[] = '/usr/local/bin/' . $suffixVersion;
    } else {
        $candidates[] = 'C:\\php\\php.exe';
    }

    $binaryName = basename(PHP_BINARY);
    if (!$isWindows && preg_match('/^php-fpm(\d+\.\d+)?$/i', $binaryName, $matches)) {
        $dir = dirname(PHP_BINARY) . DIRECTORY_SEPARATOR;
        if (!empty($matches[1])) {
            $candidates[] = $dir . 'php' . $matches[1];
        }
        $candidates[] = $dir . 'php';
    }

    foreach ($candidates as $candidate) {
        if (!is_string($candidate) || $candidate === '') {
            continue;
        }
        if (@is_file($candidate) && @is_executable($candidate)) {
            $binary = $candidate;
            return $binary;
        }
    }

    return $binary;
}

function yt_ensure_cache_structure(): void
{
    static $initialized = false;
    if ($initialized) {
        return;
    }

    if (!is_dir(YT_CACHE_PATH) && !mkdir(YT_CACHE_PATH, 0775, true) && !is_dir(YT_CACHE_PATH)) {
        throw new RuntimeException('Unable to create cache directory: ' . YT_CACHE_PATH);
    }

    $assetDir = YT_CACHE_PATH . DIRECTORY_SEPARATOR . 'assets';
    if (is_dir($assetDir)) {
        $GLOBALS['YT_ASSET_DIR'] = $assetDir;
        $GLOBALS['YT_ASSET_PREFIX'] = 'assets';
    } elseif (@mkdir($assetDir, 0775, true) || is_dir($assetDir)) {
        $GLOBALS['YT_ASSET_DIR'] = $assetDir;
        $GLOBALS['YT_ASSET_PREFIX'] = 'assets';
    } else {
        $GLOBALS['YT_ASSET_DIR'] = YT_CACHE_PATH;
        $GLOBALS['YT_ASSET_PREFIX'] = '';
    }

    $initialized = true;
}

function yt_assets_base_path(): string
{
    if (!isset($GLOBALS['YT_ASSET_DIR'])) {
        yt_ensure_cache_structure();
    }
    return $GLOBALS['YT_ASSET_DIR'] ?? YT_CACHE_PATH;
}

function yt_assets_relative_prefix(): string
{
    if (!isset($GLOBALS['YT_ASSET_PREFIX'])) {
        yt_ensure_cache_structure();
    }
    $prefix = $GLOBALS['YT_ASSET_PREFIX'] ?? '';
    return is_string($prefix) ? trim($prefix, '/') : '';
}

function yt_read_metadata(): ?array
{
    if (!is_file(YT_METADATA_FILE)) {
        return null;
    }
    $json = file_get_contents(YT_METADATA_FILE);
    if ($json === false || trim($json) === '') {
        return null;
    }
    try {
        $decoded = json_decode($json, true, 512, JSON_THROW_ON_ERROR);
    } catch (Throwable) {
        return null;
    }
    if (!is_array($decoded)) {
        return null;
    }
    $normalized = yt_normalize_metadata($decoded);
    if ($normalized !== $decoded
        && (int)($normalized['schema'] ?? 0) === YT_SCHEMA_VERSION
    ) {
        try {
            yt_write_metadata($normalized);
        } catch (Throwable) {
            // best-effort upgrade
        }
    }
    return $normalized;
}

function yt_normalize_metadata(array $decoded): array
{
    if ((int)($decoded['schema'] ?? 0) === YT_SCHEMA_VERSION) {
        return $decoded;
    }

    if (isset($decoded['assets']) && is_array($decoded['assets'])) {
        return $decoded;
    }

    if (!isset($decoded['asset_id']) && !isset($decoded['asset_name'])) {
        return $decoded;
    }

    $assetId = (int)($decoded['asset_id'] ?? 0);
    $assetName = (string)($decoded['asset_name'] ?? 'YTPlayer.bin');
    if ($assetId === 0) {
        $assetId = crc32($assetName) ?: random_int(1, PHP_INT_MAX);
    }
    if ($assetName === '') {
        $assetName = 'YTPlayer.bin';
    }

    $legacyBinary = YT_CACHE_PATH . DIRECTORY_SEPARATOR . 'latest.bin';
    $fileExists = is_file($legacyBinary);
    $fileSize = $fileExists ? (filesize($legacyBinary) ?: null) : null;
    $fileMtime = $fileExists ? (filemtime($legacyBinary) ?: null) : null;

    $asset = [
        'id' => $assetId,
        'name' => $assetName,
        'size' => (int)($decoded['size'] ?? ($fileSize ?? 0)),
        'content_type' => 'application/octet-stream',
        'download_url' => '',
        'updated_at' => $decoded['downloaded_at'] ?? null,
        'cached' => [
            'status' => $fileExists ? 'ready' : 'missing',
            'path' => $fileExists ? 'latest.bin' : '',
            'size' => $fileSize ?? (int)($decoded['size'] ?? 0),
            'updated_at' => $fileMtime ? gmdate('c', $fileMtime) : null,
        ],
    ];

    $tag = $decoded['tag'] ?? null;
    $release = [
        'id' => 0,
        'tag' => is_string($tag) ? $tag : null,
        'name' => $assetName,
        'body' => '',
        'published_at' => $decoded['downloaded_at'] ?? null,
        'url' => is_string($tag) && $tag !== ''
            ? 'https://github.com/ChenZ2000/YTPlayer/releases/tag/' . rawurlencode($tag)
            : null,
    ];

    return [
        'schema' => YT_SCHEMA_VERSION,
        'etag' => $decoded['etag'] ?? null,
        'last_checked_at' => $decoded['downloaded_at'] ?? gmdate('c'),
        'refreshed_at' => $decoded['downloaded_at'] ?? gmdate('c'),
        'release' => $release,
        'assets' => [$asset],
        'primary_asset_id' => $assetId,
        'refresh' => [
            'state' => 'legacy',
            'updated_at' => gmdate('c'),
            'reason' => 'migration',
        ],
    ];
}

function yt_write_metadata(array $metadata): void
{
    $metadata['schema'] = YT_SCHEMA_VERSION;
    $encoded = json_encode(
        $metadata,
        JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT
    );
    if ($encoded === false) {
        throw new RuntimeException('Unable to encode metadata.');
    }
    $tempFile = tempnam(YT_CACHE_PATH, 'metadata-');
    if ($tempFile === false) {
        throw new RuntimeException('Unable to create temporary metadata file.');
    }
    if (file_put_contents($tempFile, $encoded) === false) {
        @unlink($tempFile);
        throw new RuntimeException('Unable to write metadata.');
    }
    if (!@rename($tempFile, YT_METADATA_FILE)) {
        @unlink($tempFile);
        throw new RuntimeException('Unable to move metadata file into place.');
    }
}

function yt_metadata_age_seconds(?array $metadata): ?int
{
    if ($metadata === null) {
        return null;
    }
    $timestamp = $metadata['last_checked_at'] ?? null;
    if (!is_string($timestamp)) {
        return null;
    }
    $seconds = strtotime($timestamp);
    if ($seconds === false) {
        return null;
    }
    return time() - $seconds;
}

function yt_metadata_is_stale(?array $metadata, ?int $ttl = null): bool
{
    if ($metadata === null) {
        return true;
    }
    if ((int)($metadata['schema'] ?? 0) !== YT_SCHEMA_VERSION) {
        return true;
    }
    $age = yt_metadata_age_seconds($metadata);
    if ($age === null) {
        return true;
    }
    $effectiveTtl = $ttl ?? YT_METADATA_TTL;
    return $age >= $effectiveTtl;
}

function yt_wait_for_metadata(?array $metadata, int $timeoutSeconds = 3, bool $mustBeFresh = false): ?array
{
    $deadline = microtime(true) + max(0, $timeoutSeconds);
    $current = $metadata;
    while (microtime(true) < $deadline) {
        if ($current !== null && (!$mustBeFresh || !yt_metadata_is_stale($current))) {
            return $current;
        }
        usleep(200000);
        $current = yt_read_metadata();
    }
    if ($current !== null && (!$mustBeFresh || !yt_metadata_is_stale($current))) {
        return $current;
    }
    return $current;
}

function yt_release_title_refresh_pending(?array $metadata): bool
{
    return is_array($metadata) && !empty($metadata['release_title_refresh_pending']);
}

function yt_release_title_check_due(?array $metadata): bool
{
    if ($metadata === null) {
        return false;
    }
    if (yt_release_title_refresh_pending($metadata)) {
        return false;
    }
    if (yt_refresh_in_progress()) {
        return false;
    }
    $release = $metadata['release'] ?? null;
    if (!is_array($release) || (string)($release['tag'] ?? '') === '') {
        return false;
    }
    if (YT_RELEASE_TITLE_CHECK_INTERVAL === 0) {
        return true;
    }
    $checkedAt = $metadata['release_title_checked_at'] ?? null;
    if (!is_string($checkedAt) || $checkedAt === '') {
        return true;
    }
    $timestamp = strtotime($checkedAt);
    if ($timestamp === false) {
        return true;
    }
    return (time() - $timestamp) >= YT_RELEASE_TITLE_CHECK_INTERVAL;
}

function yt_remote_release_changed(?string $etag): ?bool
{
    $headers = [
        'User-Agent: ' . YT_HTTP_USER_AGENT,
        'Accept: application/vnd.github+json',
    ];
    if (is_string($etag) && $etag !== '') {
        $headers[] = 'If-None-Match: ' . $etag;
    }

    $ch = curl_init(YT_RELEASE_ENDPOINT);
    if ($ch === false) {
        return null;
    }
    curl_setopt_array($ch, [
        CURLOPT_NOBODY => true,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_FOLLOWLOCATION => true,
        CURLOPT_TIMEOUT => 15,
        CURLOPT_CONNECTTIMEOUT => 5,
        CURLOPT_HTTPHEADER => $headers,
    ]);

    $result = curl_exec($ch);
    $error = $result === false ? curl_error($ch) : null;
    $statusCode = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    if ($result === false) {
        error_log('[title-check] GitHub probe failed: ' . $error);
        return null;
    }
    if ($statusCode === 304) {
        return false;
    }
    if ($statusCode === 200) {
        return true;
    }
    error_log('[title-check] Unexpected GitHub status: ' . $statusCode);
    return null;
}

function yt_maybe_queue_release_title_refresh(array &$metadata): bool
{
    if (!yt_release_title_check_due($metadata)) {
        return false;
    }

    $lock = fopen(YT_TITLE_CHECK_LOCK_FILE, 'c');
    if ($lock === false) {
        return false;
    }
    if (!flock($lock, LOCK_EX | LOCK_NB)) {
        fclose($lock);
        return false;
    }

    try {
        $etag = $metadata['etag'] ?? null;
        $changed = yt_remote_release_changed(is_string($etag) ? $etag : null);
        if ($changed === null) {
            return false;
        }

        $metadata['release_title_checked_at'] = gmdate('c');
        if ($changed) {
            $metadata['release_title_refresh_pending'] = true;
        } else {
            unset($metadata['release_title_refresh_pending']);
        }

        yt_write_metadata($metadata);
    } catch (Throwable $e) {
        error_log('[title-check] ' . $e->getMessage());
        return false;
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }

    if (!empty($metadata['release_title_refresh_pending'])) {
        yt_background_refresh('title-sync', [
            'force' => true,
            'mode' => 'meta',
        ]);
        return true;
    }

    return false;
}

function yt_refresh_in_progress(): bool
{
    $lock = fopen(YT_REFRESH_LOCK_FILE, 'c');
    if ($lock === false) {
        return false;
    }
    $running = !flock($lock, LOCK_EX | LOCK_NB);
    if (!$running) {
        flock($lock, LOCK_UN);
    }
    fclose($lock);
    return $running;
}

function yt_background_refresh(string $reason = 'web', array $context = []): void
{
    if (yt_refresh_in_progress()) {
        return;
    }
    if (!is_file(YT_REFRESH_SCRIPT)) {
        return;
    }

    $phpCli = yt_php_cli_binary();
    $modeRaw = isset($context['mode']) ? strtolower((string)$context['mode']) : 'auto';
    $mode = preg_replace('/[^a-z0-9._-]+/i', '', $modeRaw);
    if ($mode === '') {
        $mode = 'auto';
    }
    $cmdParts = [
        escapeshellarg($phpCli),
        escapeshellarg(YT_REFRESH_SCRIPT),
        '--mode=' . $mode,
        '--reason=' . escapeshellarg($reason),
    ];

    if (isset($context['asset']) && $context['asset'] !== '') {
        $cmdParts[] = '--asset=' . escapeshellarg((string)$context['asset']);
    }
    if (!empty($context['force'])) {
        $cmdParts[] = '--force';
    }

    $command = implode(' ', $cmdParts);
    $logTarget = escapeshellarg(YT_REFRESH_LOG_FILE);

    if (stripos(PHP_OS_FAMILY, 'Windows') === 0) {
        $background = sprintf('start /B "" %s >> %s 2>&1', $command, $logTarget);
        @pclose(@popen($background, 'r'));
        return;
    }

    $background = sprintf('%s >> %s 2>&1 &', $command, $logTarget);
    @pclose(@popen($background, 'r'));
}

function yt_read_refresh_status(): ?array
{
    if (!is_file(YT_REFRESH_STATUS_FILE)) {
        return null;
    }
    $contents = file_get_contents(YT_REFRESH_STATUS_FILE);
    if ($contents === false || trim($contents) === '') {
        return null;
    }
    try {
        $decoded = json_decode($contents, true, 512, JSON_THROW_ON_ERROR);
    } catch (Throwable) {
        return null;
    }
    return is_array($decoded) ? $decoded : null;
}

function yt_write_refresh_status(array $status): void
{
    $encoded = json_encode(
        $status,
        JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT
    );
    if ($encoded === false) {
        throw new RuntimeException('Unable to encode refresh status.');
    }
    file_put_contents(YT_REFRESH_STATUS_FILE, $encoded);
}

function yt_select_asset_record(array $metadata, ?string $pattern = null): ?array
{
    $assets = $metadata['assets'] ?? [];
    if (!is_array($assets) || empty($assets)) {
        return null;
    }

    $pattern = $pattern !== null ? trim($pattern) : null;
    if ($pattern !== null && $pattern !== '') {
        $needle = mb_strtolower($pattern);
        foreach ($assets as $asset) {
            $name = (string)($asset['name'] ?? '');
            if ($name === '') {
                continue;
            }
            if (mb_strpos(mb_strtolower($name), $needle) !== false) {
                return $asset;
            }
            if ((string)($asset['id'] ?? '') === $pattern) {
                return $asset;
            }
        }
    }

    $primaryId = (int)($metadata['primary_asset_id'] ?? 0);
    if ($primaryId > 0) {
        foreach ($assets as $asset) {
            if ((int)($asset['id'] ?? 0) === $primaryId) {
                return $asset;
            }
        }
    }

    return $assets[0];
}

function yt_select_release_asset(array $assets, ?string $pattern = null): array
{
    if (empty($assets)) {
        throw new RuntimeException('Release does not contain downloadable assets.');
    }
    $pattern = $pattern !== null ? trim($pattern) : null;
    if ($pattern !== null && $pattern !== '') {
        $needle = mb_strtolower($pattern);
        foreach ($assets as $asset) {
            $name = (string)($asset['name'] ?? '');
            if ($name === '') {
                continue;
            }
            if (mb_strpos(mb_strtolower($name), $needle) !== false) {
                return $asset;
            }
            if ((string)($asset['id'] ?? '') === $pattern) {
                return $asset;
            }
        }
        throw new RuntimeException(sprintf('Asset matching "%s" not found in release.', $pattern));
    }
    $priorityExtensions = ['.zip', '.7z', '.tar.gz', '.tar.xz', '.tar.bz2', '.appimage', '.exe'];
    foreach ($priorityExtensions as $extension) {
        foreach ($assets as $asset) {
            $name = (string)($asset['name'] ?? '');
            if ($name !== '' && str_ends_with(mb_strtolower($name), mb_strtolower($extension))) {
                return $asset;
            }
        }
    }
    return $assets[0];
}

function yt_asset_cache_relative_path(int $assetId, string $assetName): string
{
    $safeName = preg_replace('/[^A-Za-z0-9._-]+/', '_', $assetName);
    if ($safeName === null || $safeName === '' || $safeName === '_') {
        $safeName = 'asset.bin';
    }
    $filename = $assetId . '-' . $safeName;
    $prefix = yt_assets_relative_prefix();
    if ($prefix === '') {
        return $filename;
    }
    return $prefix . '/' . $filename;
}

function yt_asset_absolute_path(string $relativePath): string
{
    $relativePath = str_replace(['..', '\\'], '', $relativePath);
    $relativePath = ltrim($relativePath, '/');
    return YT_CACHE_PATH . DIRECTORY_SEPARATOR . str_replace('/', DIRECTORY_SEPARATOR, $relativePath);
}

function yt_asset_is_ready(array $asset): bool
{
    $cached = $asset['cached'] ?? null;
    if (!is_array($cached)) {
        return false;
    }
    $path = $cached['path'] ?? null;
    if (!is_string($path) || $path === '') {
        return false;
    }
    $absolute = yt_asset_absolute_path($path);
    if (!is_file($absolute)) {
        return false;
    }
    $size = filesize($absolute);
    $expected = (int)($asset['size'] ?? 0);
    return $size !== false && $size === $expected;
}

function yt_handle_download_request(?string $pattern, ProgressTracker $progress, string $reason = 'download'): void
{
    yt_ensure_cache_structure();
    $metadata = yt_read_metadata();
    if ($metadata === null) {
        yt_background_refresh($reason, ['force' => true, 'asset' => $pattern]);
        yt_send_client_message(503, '安装包尚未准备好，请稍后重试。', [], $progress);
    }
    if (yt_metadata_is_stale($metadata)) {
        yt_background_refresh($reason, ['asset' => $pattern]);
    }

    $asset = yt_select_asset_record($metadata, $pattern);
    if ($asset === null) {
        yt_send_client_message(404, '未找到匹配的安装资源。', [], $progress);
    }

    if (!yt_asset_is_ready($asset)) {
        yt_background_refresh($reason . '-asset', [
            'asset' => $pattern ?? ($asset['name'] ?? ''),
            'force' => true,
        ]);
        yt_send_client_message(202, '正在准备最新安装包，请稍后再试。', [
            'asset' => [
                'name' => $asset['name'] ?? null,
                'id' => $asset['id'] ?? null,
            ],
        ], $progress);
    }

    $downloadName = (string)($asset['name'] ?? ($metadata['release']['tag'] ?? 'YTPlayer.bin'));
    $progress->complete('serve', 'Streaming cached asset', 100);
    yt_stream_cached_asset($asset, $downloadName, $progress);
}

function yt_build_content_disposition(string $filename): string
{
    $filename = trim($filename);
    if ($filename === '') {
        $filename = 'YTPlayer.bin';
    }
    $fallback = preg_replace('/[^A-Za-z0-9._-]+/', '_', $filename);
    if ($fallback === null || $fallback === '') {
        $fallback = 'YTPlayer.bin';
    }
    $encoded = rawurlencode($filename);
    $ascii = addcslashes($fallback, "\"\\");
    return sprintf("attachment; filename=\"%s\"; filename*=UTF-8''%s", $ascii, $encoded);
}

function yt_response_is_json(): bool
{
    $accept = $_SERVER['HTTP_ACCEPT'] ?? '';
    return stripos($accept, 'application/json') !== false;
}

function yt_send_json(int $statusCode, array $payload, ?ProgressTracker $progress = null): void
{
    http_response_code($statusCode);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store, no-cache, must-revalidate');
    header('Pragma: no-cache');
    if ($progress !== null) {
        header('X-YT-Download-Progress: ' . $progress->toHeaderValue());
    }
    echo json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT);
    exit;
}

function yt_send_text(int $statusCode, string $message, ?ProgressTracker $progress = null): void
{
    http_response_code($statusCode);
    header('Content-Type: text/plain; charset=utf-8');
    header('Cache-Control: no-store, no-cache, must-revalidate');
    header('Pragma: no-cache');
    if ($progress !== null) {
        header('X-YT-Download-Progress: ' . $progress->toHeaderValue());
    }
    echo $message;
    exit;
}

function yt_send_client_message(int $statusCode, string $message, array $payload = [], ?ProgressTracker $progress = null): void
{
    $body = array_merge([
        'message' => $message,
    ], $payload);
    if (!isset($body['status'])) {
        $body['status'] = $statusCode >= 400 ? 'error' : 'pending';
    }
    if (yt_response_is_json()) {
        yt_send_json($statusCode, $body, $progress);
    }
    yt_send_text($statusCode, $message, $progress);
}

function yt_try_accel_redirect(string $absolutePath): ?string
{
    if (YT_ACCEL_REDIRECT_PREFIX === null || YT_ACCEL_REDIRECT_PREFIX === '') {
        return null;
    }
    $relative = str_replace(YT_CACHE_PATH . DIRECTORY_SEPARATOR, '', $absolutePath);
    $relative = str_replace(DIRECTORY_SEPARATOR, '/', $relative);
    return rtrim(YT_ACCEL_REDIRECT_PREFIX, '/') . '/' . ltrim($relative, '/');
}

function yt_stream_cached_asset(array $asset, string $downloadName, ?ProgressTracker $progress = null): void
{
    $cached = $asset['cached'] ?? null;
    $path = is_array($cached) ? ($cached['path'] ?? null) : null;
    if (!is_string($path) || $path === '') {
        yt_send_json(503, [
            'status' => 'error',
            'message' => 'Requested asset is not ready.',
        ], $progress);
    }
    $absolute = yt_asset_absolute_path($path);
    if (!is_file($absolute)) {
        yt_send_json(503, [
            'status' => 'error',
            'message' => 'Cached asset file missing.',
        ], $progress);
    }

    if (ob_get_level() > 0) {
        ob_end_clean();
    }

    header('Content-Type: ' . ($asset['content_type'] ?? 'application/octet-stream'));
    header('Content-Length: ' . filesize($absolute));
    header('Content-Disposition: ' . yt_build_content_disposition($downloadName));
    header('Cache-Control: no-store, no-cache, must-revalidate');
    header('Pragma: no-cache');
    if ($progress !== null) {
        header('X-YT-Download-Progress: ' . $progress->toHeaderValue());
    }

    $accel = yt_try_accel_redirect($absolute);
    if ($accel !== null) {
        header('X-Accel-Redirect: ' . $accel);
        exit;
    }

    $handle = fopen($absolute, 'rb');
    if ($handle === false) {
        yt_send_json(500, [
            'status' => 'error',
            'message' => 'Unable to open cached asset file.',
        ], $progress);
    }
    while (!feof($handle)) {
        echo fread($handle, 1048576);
        flush();
    }
    fclose($handle);
    exit;
}

class ProgressTracker
{
    /**
     * @var array<int, array<string, mixed>>
     */
    private array $steps = [];

    public function start(string $step, string $message, int $progress = 0): void
    {
        $this->steps[$step] = [
            'step' => $step,
            'state' => 'in_progress',
            'progress' => $this->clamp($progress),
            'message' => $message,
        ];
    }

    public function update(string $step, string $message, int $progress): void
    {
        if (!isset($this->steps[$step])) {
            $this->start($step, $message, $progress);
            return;
        }
        $this->steps[$step]['message'] = $message;
        $this->steps[$step]['progress'] = $this->clamp($progress);
    }

    public function complete(string $step, string $message, int $progress = 100): void
    {
        if (!isset($this->steps[$step])) {
            $this->start($step, $message, $progress);
        }
        $this->steps[$step]['state'] = 'completed';
        $this->steps[$step]['message'] = $message;
        $this->steps[$step]['progress'] = $this->clamp($progress);
    }

    /**
     * @return array<int, array<string, mixed>>
     */
    public function toArray(): array
    {
        return array_values($this->steps);
    }

    public function toHeaderValue(): string
    {
        $encoded = json_encode($this->toArray(), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        return $encoded === false ? '[]' : $encoded;
    }

    private function clamp(int $value): int
    {
        return max(0, min(100, $value));
    }
}
