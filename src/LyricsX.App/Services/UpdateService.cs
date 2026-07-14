using Velopack;
using Velopack.Sources;

namespace LyricsX.App.Services;

/// <summary>
/// Velopack 기반 자동 업데이트. GitHub Releases(countnine/LyricsX-Windows)를 업데이트 소스로 사용한다.
/// 개발/디버그 실행(설치본이 아님)에서는 IsInstalled=false라 조용히 무동작한다.
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/countnine/LyricsX-Windows";

    private readonly UpdateManager _mgr;

    public UpdateService() =>
        _mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    /// <summary>Velopack 설치본으로 실행 중인가. false면 업데이트 확인 불가(개발 빌드 등).</summary>
    public bool IsInstalled => _mgr.IsInstalled;

    /// <summary>현재 버전. 설치본이 아니면 어셈블리 버전으로 폴백.</summary>
    public string CurrentVersion =>
        _mgr.CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>새 버전 확인. 최신이거나 미설치면 null.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!_mgr.IsInstalled) return null;
        return await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
    }

    /// <summary>업데이트 다운로드 후 적용하고 재시작한다. 이 호출 이후 프로세스는 종료된다.</summary>
    public async Task DownloadAndApplyAsync(UpdateInfo update)
    {
        await _mgr.DownloadUpdatesAsync(update).ConfigureAwait(false);
        _mgr.ApplyUpdatesAndRestart(update);
    }
}
