<?php
declare(strict_types=1);

require __DIR__ . '/lib/bootstrap.php';

yt_ensure_cache_structure();

$metadata = yt_read_metadata();
if ($metadata === null) {
    yt_background_refresh('landing', ['force' => true]);
} else {
    if (yt_metadata_is_stale($metadata)) {
        yt_background_refresh('landing');
    }
    $primaryAsset = yt_select_asset_record($metadata);
    if ($primaryAsset !== null && !yt_asset_is_ready($primaryAsset)) {
        yt_background_refresh('landing', [
            'asset' => $primaryAsset['name'] ?? null,
            'force' => true,
        ]);
    }
}
?>
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width,initial-scale=1.0">
    <title>易听 YTPlayer</title>
    <link rel="stylesheet" href="styles.css">
</head>
<body>
<main class="app-shell">
    <header class="hero">
        <h1>易听 · 流畅无障碍的网易云音乐桌面客户端</h1>
        <p class="lede">
            专注高可用与无障碍，为享受音乐而生！
        </p>
        <div class="actions">
            <a role="button"
               class="btn btn-primary disabled"
               id="download-button"
               aria-disabled="true"
               tabindex="-1">正在检查最新版本 ...</a>
            <a role="button" class="btn btn-outline" href="https://github.com/ChenZ2000/YTPlayer/" target="_blank" rel="noopener noreferrer">
                GitHub 主页
            </a>
        </div>
    </header>

    <section class="feature-grid">
        <article class="feature-card">
            <h2>
                <div class="icon">🎧</div>
                沉浸播放
            </h2>
            <p>纯净、简洁，多音质自由切换，搭配丰富优雅的快捷键支持，带来舒适专注的聆听体验。</p>
        </article>
        <article class="feature-card">
            <h2>
                <div class="icon">💖</div>
                无障碍的使用体验
            </h2>
            <p>屏幕阅读器友好， UI 逻辑直观，还支持歌词实时输出</p>
            <p>丰富且河里的快捷键设计，让操作轻松高效</p>
        </article>
        <article class="feature-card">
            <h2>
                <div class="icon">⬇️</div>
                强大的下载能力
            </h2>
            <p>支持下载和批量下载歌曲/歌单/专辑，还支持单独下载歌词，妈妈再也不怕我出差没网啦！</p>
        </article>
    </section>

    <section class="donation-section">
        <div class="donation-panel">
            <h2>给开发者 ChenZ 买点零食</h2>
            <p class="donation-subtitle">你的支持是我前行的动力 💜</p>
            <div class="qr-frame">
                <img src="WeChatQRCode.jpg" alt="微信长按或扫一扫，给 ChenZ 买点零食">
            </div>
        </div>
    </section>
</main>
<script>
(function () {
    const button = document.getElementById('download-button');
    let pollTimer = null;
    let lastLabel = '';

    function setButton(label, href, disabled) {
        if (label && label !== lastLabel) {
            button.textContent = label;
            lastLabel = label;
        }
        if (disabled) {
            button.classList.add('disabled');
            button.setAttribute('aria-disabled', 'true');
            button.setAttribute('tabindex', '-1');
            button.removeAttribute('href');
        } else {
            button.classList.remove('disabled');
            button.removeAttribute('aria-disabled');
            button.removeAttribute('tabindex');
            if (href) {
                button.href = href;
            }
        }
    }

    function scheduleNext(delay) {
        if (pollTimer !== null) {
            clearTimeout(pollTimer);
        }
        pollTimer = window.setTimeout(fetchStatus, delay);
    }

    function extractLatestTag(data) {
        if (!data || typeof data !== 'object') {
            return '';
        }
        const latest = data.latest || {};
        if (typeof latest.tag === 'string' && latest.tag !== '') {
            return latest.tag;
        }
        if (typeof latest.name === 'string' && latest.name !== '') {
            return latest.name;
        }
        return '';
    }

    function handlePayload(payload, httpStatus) {
        const data = payload.data || {};
        const assets = Array.isArray(data.assets) ? data.assets : [];
        const latestTag = extractLatestTag(data);
        const readyAsset = assets.find(function (asset) {
            return asset && asset.ready;
        });
        const status = payload.status || (httpStatus === 202 ? 'pending' : 'ok');

        if (!latestTag && status === 'pending') {
            setButton('正在检查最新版本 ...', null, true);
        } else if (readyAsset && readyAsset.download) {
            setButton('立即下载 ' + latestTag, readyAsset.download, false);
        } else if (latestTag) {
            setButton('正在准备 ' + latestTag, null, true);
        } else {
            setButton('正在检查最新版本 ...', null, true);
        }

        const delay = (payload.nextPollAfter && Number(payload.nextPollAfter) > 0)
            ? Number(payload.nextPollAfter) * 1000
            : (readyAsset ? 12000 : 4000);
        scheduleNext(delay);
    }

    async function fetchStatus() {
        try {
            const response = await fetch('update.php?action=check', {
                method: 'GET',
                headers: {
                    'Accept': 'application/json',
                },
                cache: 'no-store',
            });
            const payload = await response.json();
            handlePayload(payload, response.status);
        } catch (error) {
            scheduleNext(10000);
        }
    }

    setButton('正在检查最新版本 ...', null, true);
    fetchStatus();
})();
</script>
</body>
</html>
