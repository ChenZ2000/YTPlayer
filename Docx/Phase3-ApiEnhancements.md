## 阶段 3 开发记录（播放 URL & 账号数据路径）

### 改动概览
- **EAPI 用户等级刷新**：`/api/user/level`（useIosHeaders + skipErrorHandling）替换原 `/weapi/user/level`，确保等级信息走更可靠的 EAPI 通道。
- **指纹管线已完善**：EAPI/WEAPI 统一通过 `ApplyFingerprintHeaders` 注入随机中国 IP、Accept/Connection/Accept-Encoding；EAPI Cookie 补全 deviceId/os/osver/appver/_ntes_nuid/NMTID/WNMCID/MUSIC_A/__csrf。
- **播放上报已完成（前序阶段）**：start/play/playend 批量上报 + EAPI fallback + scrobble，完播阈值控制+落盘重试。

### 未完成 / 待继续
1. **Song URL EAPI 兜底/并行**  
   - 在 `GetSongUrlAsync` 里为 `/song/enhance/player/url` 加入 EAPI fallback（或并行取优）并映射 level/encodeType/immerseType；出现 404/509/风控码时自动切换。
   - 更新流缓存键包含 level。
2. **账号刷新路径**  
   - `/nuser/account/get` 改用新的指纹管线或 EAPI 路径（若有）；确保登录后的 AccountState 同步指纹/AntiCheat。
3. **回归脚本（阶段 4）**  
   - PowerShell/Node 脚本：scrobble + weblog + user_record + song_url + login_refresh，输出 JSON 日志。

### 编译状态
- `MSBuild ... /t:Rebuild /p:Configuration=Debug /p:Platform="Any CPU"`：通过，0 错误。

### 注意
- 此文档位于 `Docx/`，未修改 README.md。
