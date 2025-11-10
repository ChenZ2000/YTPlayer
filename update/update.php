<?php
declare(strict_types=1);

mb_internal_encoding('UTF-8');
ignore_user_abort(true);
set_time_limit(0);
ini_set('display_errors', '0');

const RELEASE_API_BASE = 'https://api.github.com/repos/ChenZ2000/YTPlayer';
const RELEASE_LATEST_ENDPOINT = RELEASE_API_BASE . '/releases/latest';
const USER_AGENT = 'YTPlayer-Updater/1.0 (+https://yt.chenz.cloud)';

$baseDir = __DIR__;
$cacheDir = $baseDir . DIRECTORY_SEPARATOR . 'cache';
$cacheMetaFile = $cacheDir . DIRECTORY_SEPARATOR . 'metadata.json';
$cacheBinaryFile = $cacheDir . DIRECTORY_SEPARATOR . 'latest.bin';
$cacheLockFile = $cacheDir . DIRECTORY_SEPARATOR . '.lock';

if (!is_dir($cacheDir) && !mkdir($cacheDir, 0775, true) && !is_dir($cacheDir)) {
    http_response_code(500);
    header('Content-Type: application/json; charset=utf-8');
    $fallback = json_encode([
        'status' => 'error',
        'message' => 'Cache directory is not writable.',
    ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    echo $fallback === false ? '{"status":"error","message":"Cache unavailable"}' : $fallback;
    exit;
}

$action = strtolower($_GET['action'] ?? 'check');
$progress = new ProgressTracker();
$releaseClient = new ReleaseClient();
$cacheStore = new CacheStore($cacheMetaFile, $cacheBinaryFile, $cacheLockFile);

try {
    switch ($action) {
        case 'download':
            handleDownload($progress, $releaseClient, $cacheStore);
            break;
        case 'check':
        default:
            handleCheck($progress, $releaseClient);
            break;
    }
} catch (Throwable $e) {
    error_log('[update.php] ' . $e->getMessage());
    sendJsonResponse([
        'error' => 'Update service error',
        'detail' => $e->getMessage(),
    ], $progress, 500);
}

function handleCheck(ProgressTracker $progress, ReleaseClient $client): void
{
    $progress->start('fetch_release', 'Fetching latest release metadata', 15);
    $release = $client->fetchLatest();
    $progress->complete('fetch_release', 'Release metadata retrieved', 70);

    $currentVersionInput = $_REQUEST['version'] ?? null;
    $currentVersion = normalizeVersion(is_string($currentVersionInput) ? $currentVersionInput : null);
    $latestTag = (string)($release['tag_name'] ?? '');
    $latestVersion = normalizeVersion($latestTag);

    $updateAvailable = null;
    if ($currentVersion !== null && $latestVersion !== null) {
        $updateAvailable = version_compare($latestVersion, $currentVersion, '>');
    }

    $assets = array_map(static function (array $asset): array {
        $name = (string)($asset['name'] ?? '');
        return [
            'name' => $name,
            'size' => (int)($asset['size'] ?? 0),
            'contentType' => $asset['content_type'] ?? 'application/octet-stream',
            'updatedAt' => $asset['updated_at'] ?? null,
            'download' => buildDownloadUrl($name),
        ];
    }, array_values($release['assets'] ?? []));

    $progress->complete('compute', 'Version comparison complete', 100);

    $payload = [
        'clientVersion' => $currentVersion,
        'latest' => [
            'tag' => $latestTag,
            'semantic' => $latestVersion,
            'name' => $release['name'] ?? $latestTag,
            'publishedAt' => $release['published_at'] ?? null,
            'notes' => trim((string)($release['body'] ?? '')),
            'releasePage' => buildReleaseUrl($latestTag),
        ],
        'updateAvailable' => $updateAvailable,
        'assets' => $assets,
    ];

    sendJsonResponse($payload, $progress);
}

function handleDownload(ProgressTracker $progress, ReleaseClient $client, CacheStore $cacheStore): void
{
    $assetQueryInput = $_REQUEST['asset'] ?? null;
    $assetQuery = is_string($assetQueryInput) ? trim($assetQueryInput) : null;

    $progress->start('fetch_release', 'Fetching latest release metadata', 15);
    $release = $client->fetchLatest();
    $progress->complete('fetch_release', 'Release metadata retrieved', 40);

    $asset = selectAsset($release['assets'] ?? [], $assetQuery);
    $progress->start('cache', 'Inspecting cache state', 55);

    $cacheResult = $cacheStore->locked(
        function (CacheStore $store) use ($asset, $release, $progress) {
            $metadata = $store->readMetadata();
            $binaryPath = $store->binaryPath();
            $needsRefresh = true;

            if ($metadata !== null && is_file($binaryPath)) {
                if ((int)($metadata['asset_id'] ?? 0) === (int)($asset['id'] ?? -1)
                    && (int)($metadata['size'] ?? 0) === (int)($asset['size'] ?? -1)
                ) {
                    $needsRefresh = false;
                }
            }

            if (!$needsRefresh) {
                return [
                    'path' => $binaryPath,
                    'metadata' => $metadata,
                    'asset' => $asset,
                    'fresh' => true,
                ];
            }

            $progress->update('cache', 'Refreshing cached binary', 60);

            downloadAssetTo(
                assetUrl: (string)($asset['browser_download_url'] ?? ''),
                targetPath: $binaryPath,
                expectedSize: (int)($asset['size'] ?? 0),
                progress: $progress
            );

            $metadataPayload = [
                'asset_id' => (int)($asset['id'] ?? 0),
                'asset_name' => (string)($asset['name'] ?? 'update.bin'),
                'size' => (int)($asset['size'] ?? 0),
                'version' => normalizeVersion((string)($release['tag_name'] ?? '')),
                'tag' => $release['tag_name'] ?? null,
                'downloaded_at' => gmdate('c'),
            ];
            $store->writeMetadata($metadataPayload);

            return [
                'path' => $binaryPath,
                'metadata' => $metadataPayload,
                'asset' => $asset,
                'fresh' => false,
            ];
        }
    );

    $progress->complete(
        'cache',
        $cacheResult['fresh'] ? 'Cache hit, streaming binary' : 'Cache refreshed, streaming binary',
        85
    );

    $downloadName = (string)($cacheResult['metadata']['asset_name'] ?? ($cacheResult['asset']['name'] ?? 'update.bin'));
    streamBinaryFile($cacheResult['path'], $downloadName, $progress);
}

function selectAsset(array $assets, ?string $pattern): array
{
    if (empty($assets)) {
        throw new RuntimeException('Latest release does not include downloadable assets.');
    }

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
        }
        throw new RuntimeException(sprintf('Asset matching "%s" was not found in the latest release.', $pattern));
    }

    $priorityExtensions = ['.zip', '.7z', '.tar.gz', '.tar.xz', '.tar.bz2', '.appimage', '.exe'];
    foreach ($priorityExtensions as $extension) {
        foreach ($assets as $asset) {
            $name = (string)($asset['name'] ?? '');
            if ($name !== '' && endsWithIgnoreCase($name, $extension)) {
                return $asset;
            }
        }
    }

    return $assets[0];
}

function downloadAssetTo(string $assetUrl, string $targetPath, int $expectedSize, ProgressTracker $progress): void
{
    if ($assetUrl === '') {
        throw new RuntimeException('Asset download URL is missing.');
    }

    $progress->start('download', 'Downloading latest binary', 65);
    $tempFile = tempnam(dirname($targetPath), 'yt-update-');
    if ($tempFile === false) {
        throw new RuntimeException('Unable to create temporary file for download.');
    }

    $fileHandle = fopen($tempFile, 'wb');
    if ($fileHandle === false) {
        @unlink($tempFile);
        throw new RuntimeException('Unable to open temporary file for writing.');
    }

    $ch = curl_init($assetUrl);
    curl_setopt_array($ch, [
        CURLOPT_FILE => $fileHandle,
        CURLOPT_FOLLOWLOCATION => true,
        CURLOPT_USERAGENT => USER_AGENT,
        CURLOPT_TIMEOUT => 600,
        CURLOPT_CONNECTTIMEOUT => 15,
        CURLOPT_HTTPHEADER => [
            'Accept: application/octet-stream',
        ],
    ]);

    $success = curl_exec($ch);
    $downloadError = $success === false ? curl_error($ch) : null;
    $httpCode = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);
    fclose($fileHandle);

    if ($success === false || $httpCode >= 400) {
        @unlink($tempFile);
        throw new RuntimeException('GitHub asset download failed: ' . ($downloadError ?: ('HTTP ' . $httpCode)));
    }

    if ($expectedSize > 0 && filesize($tempFile) !== $expectedSize) {
        @unlink($tempFile);
        throw new RuntimeException('Downloaded asset size mismatch.');
    }

    if (!@rename($tempFile, $targetPath)) {
        if (!@copy($tempFile, $targetPath)) {
            @unlink($tempFile);
            throw new RuntimeException('Unable to move downloaded file into cache.');
        }
        @unlink($tempFile);
    }

    $progress->complete('download', 'Binary cached successfully', 80);
}

function streamBinaryFile(string $path, string $downloadName, ProgressTracker $progress): void
{
    if (!is_file($path)) {
        throw new RuntimeException('Cached binary not found.');
    }

    if (ob_get_level() > 0) {
        ob_end_clean();
    }

    $size = filesize($path);
    $progress->complete('serve', 'Streaming binary to client', 100);

    header('Content-Type: application/octet-stream');
    if ($size !== false) {
        header('Content-Length: ' . $size);
    }
    header('Content-Disposition: ' . buildContentDispositionValue($downloadName));
    header('Cache-Control: no-store, no-cache, must-revalidate');
    header('Pragma: no-cache');
    header('X-YT-Update-Progress: ' . $progress->toHeaderValue());

    $handle = fopen($path, 'rb');
    if ($handle === false) {
        throw new RuntimeException('Unable to open cached binary for streaming.');
    }

    while (!feof($handle)) {
        echo fread($handle, 8192);
        flush();
    }
    fclose($handle);
    exit;
}

function sendJsonResponse(array $payload, ProgressTracker $progress, int $statusCode = 200): void
{
    http_response_code($statusCode);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store, no-cache, must-revalidate');
    header('Pragma: no-cache');
    header('X-YT-Update-Progress: ' . $progress->toHeaderValue());

    $encoded = json_encode([
        'status' => $statusCode >= 400 ? 'error' : 'ok',
        'progress' => $progress->toArray(),
        'timestamp' => gmdate('c'),
        'data' => $payload,
    ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);

    if ($encoded === false) {
        $encoded = '{"status":"error","message":"Encoding failure"}';
    }

    echo $encoded;
    exit;
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
    return ltrim($trimmed, "vV");
}

function buildDownloadUrl(string $assetName): string
{
    $scriptUrl = currentScriptUrl();
    $query = http_build_query([
        'action' => 'download',
        'asset' => $assetName,
    ]);
    return $scriptUrl . '?' . $query;
}

function currentScriptUrl(): string
{
    $scheme = (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off') ? 'https' : 'http';
    $host = $_SERVER['HTTP_HOST'] ?? 'localhost';
    $script = $_SERVER['SCRIPT_NAME'] ?? '/update.php';
    return $scheme . '://' . $host . $script;
}

function buildReleaseUrl(string $tag): string
{
    $safeTag = $tag !== '' ? $tag : 'latest';
    return 'https://github.com/ChenZ2000/YTPlayer/releases/tag/' . rawurlencode($safeTag);
}

function endsWithIgnoreCase(string $haystack, string $needle): bool
{
    $haystackLower = mb_strtolower($haystack);
    $needleLower = mb_strtolower($needle);
    return str_ends_with($haystackLower, $needleLower);
}

function buildContentDispositionValue(string $filename): string
{
    $filename = trim($filename);
    if ($filename === '') {
        $filename = 'update.bin';
    }
    $fallback = preg_replace('/[^A-Za-z0-9._-]+/', '_', $filename);
    if ($fallback === null || $fallback === '') {
        $fallback = 'update.bin';
    }
    $encoded = rawurlencode($filename);
    $ascii = addcslashes($fallback, "\"\\");
    return sprintf('attachment; filename="%s"; filename*=UTF-8\'\'%s', $ascii, $encoded);
}

final class ReleaseClient
{
    public function fetchLatest(): array
    {
        $body = $this->request(RELEASE_LATEST_ENDPOINT);
        $decoded = json_decode($body, true, 512, JSON_THROW_ON_ERROR);
        if (!is_array($decoded)) {
            throw new RuntimeException('Unexpected response from GitHub releases API.');
        }
        return $decoded;
    }

    private function request(string $url): string
    {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_USERAGENT => USER_AGENT,
            CURLOPT_TIMEOUT => 20,
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_HTTPHEADER => [
                'Accept: application/vnd.github+json',
            ],
        ]);

        $response = curl_exec($ch);
        $curlError = $response === false ? curl_error($ch) : null;
        $statusCode = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);

        if ($response === false) {
            throw new RuntimeException('GitHub API request failed: ' . $curlError);
        }

        if ($statusCode >= 400) {
            throw new RuntimeException('GitHub API responded with HTTP ' . $statusCode);
        }

        return $response;
    }
}

final class CacheStore
{
    public function __construct(
        private readonly string $metadataPath,
        private readonly string $binaryPath,
        private readonly string $lockPath
    ) {
    }

    public function locked(callable $callback, int $lockType = LOCK_EX): mixed
    {
        $handle = fopen($this->lockPath, 'c+');
        if ($handle === false) {
            throw new RuntimeException('Unable to open cache lock file.');
        }

        try {
            if (!flock($handle, $lockType)) {
                throw new RuntimeException('Unable to obtain cache lock.');
            }
            return $callback($this);
        } finally {
            flock($handle, LOCK_UN);
            fclose($handle);
        }
    }

    public function readMetadata(): ?array
    {
        if (!is_file($this->metadataPath)) {
            return null;
        }

        $contents = file_get_contents($this->metadataPath);
        if ($contents === false || $contents === '') {
            return null;
        }

        try {
            $decoded = json_decode($contents, true, 512, JSON_THROW_ON_ERROR);
        } catch (Throwable) {
            return null;
        }

        return $decoded;
    }

    public function writeMetadata(array $metadata): void
    {
        $encoded = json_encode($metadata, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        if ($encoded === false) {
            throw new RuntimeException('Unable to encode cache metadata.');
        }

        if (file_put_contents($this->metadataPath, $encoded, LOCK_EX) === false) {
            throw new RuntimeException('Unable to write cache metadata.');
        }
    }

    public function binaryPath(): string
    {
        return $this->binaryPath;
    }
}

final class ProgressTracker
{
    /**
     * @var array<string, array<string, mixed>>
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

