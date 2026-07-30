using Android.Content;
using Android.Media;
// 암시적 using의 System.IO.Stream과 모호 참조(CS0104) 방지 — AndroidNowPlayingSource의
// MediaController 별칭과 같은 이유·같은 처리.
using Stream = Android.Media.Stream;

namespace Musebase.Android.Services;

/// <summary>
/// 광고 구간에 미디어 볼륨을 내리고 끝나면 되돌린다.
///
/// <b>이건 앱별 음소거가 아니라 기기 전체 미디어 볼륨이다.</b> 안드로이드에는 다른 앱의 볼륨만
/// 조절하는 공개 API가 없다(Windows의 Core Audio 세션 뮤트에 해당하는 것이 없다). 폰은 보통
/// 한 번에 한 앱만 소리를 내므로 실용적으로는 문제가 적지만, <b>복구에 실패하면 피해가 훨씬 크다</b> —
/// 그래서 아래 세 가지를 지킨다.
///
/// 1. 원래 볼륨을 <see cref="AndroidSettings.AdMuteSavedVolume"/>에 <b>즉시</b> 기록한다.
///    프로세스가 광고 도중 강제 종료돼도 다음 실행이 <see cref="RestoreOrphanedVolume"/>으로 되돌린다.
/// 2. 진입 시점에 이미 0이면 사용자가 직접 내린 것으로 보고 손대지 않는다.
/// 3. 뮤트 중 볼륨이 0이 아니게 되면 사용자가 볼륨 키로 개입한 것 → 그 구간은 포기한다.
///    사용자와 볼륨을 다투지 않는다.
/// </summary>
public sealed class MediaVolumeMuter
{
    private const Stream MediaStream = Stream.Music;

    private readonly AudioManager? _audio;
    private readonly AndroidSettings _settings;

    private bool _muted;

    /// <summary>
    /// 사용자가 뮤트 중 볼륨을 올렸다 — 음악이 돌아올 때까지(<see cref="ResetUserOverride"/>)
    /// 이 광고 구간 전체에서 손을 뗀다.
    /// </summary>
    private bool _userOverrode;

    public MediaVolumeMuter(Context context, AndroidSettings settings)
    {
        _audio = (AudioManager?)context.ApplicationContext?.GetSystemService(Context.AudioService);
        _settings = settings;
    }

    /// <summary>우리가 볼륨을 내려 둔 상태인지.</summary>
    public bool IsMuted => _muted;

    /// <summary>
    /// 이전 세션이 광고 도중 죽어 볼륨이 0으로 남았으면 되돌린다.
    /// <b>기능이 꺼져 있어도 앱 시작 시 반드시 호출해야 한다</b> — 켜 둔 채로 죽었을 수 있다.
    /// </summary>
    public void RestoreOrphanedVolume()
    {
        var saved = _settings.AdMuteSavedVolume;
        if (saved < 0) return;

        global::Android.Util.Log.Info("Musebase", $"ad-mute: 남아 있던 볼륨 복구 ({saved})");
        TrySetVolume(saved);
        _settings.AdMuteSavedVolume = -1;
        _muted = false;
        _userOverrode = false;
    }

    /// <summary>
    /// 광고 시작 — 미디어 볼륨을 0으로. 이미 0이거나 이번 광고 구간을 포기했으면 아무것도 하지 않는다.
    /// </summary>
    /// <returns>실제로 볼륨을 내렸으면 true. 호출자가 이 값으로 로그를 남겨야 한다 —
    /// 결과와 무관하게 "볼륨 0" 로그를 찍으면 프로브가 거짓말을 하게 된다.</returns>
    public bool Mute()
    {
        if (_audio is null || _muted) return false;

        // 사용자가 이 광고 구간에서 볼륨을 올렸다면, 다음 광고 세그먼트에도 손대지 않는다.
        // (Spotify 광고는 보통 2개 연속이라, 세그먼트 단위로 풀면 사용자가 볼륨을 올린 지
        //  20초 만에 다시 내리누르게 된다 — 실측으로 확인한 동작이다.)
        if (_userOverrode) return false;

        int current;
        try { current = _audio.GetStreamVolume(MediaStream); }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn("Musebase", $"ad-mute: 볼륨 읽기 실패 — {e.Message}");
            return false;
        }

        // 사용자가 이미 음소거해 뒀다면 우리가 끼어들 이유도, 나중에 되돌릴 이유도 없다.
        if (current <= 0) return false;

        _muted = true;
        // 볼륨을 실제로 내리기 **전에** 기록한다 — 이 사이에 죽으면 복구할 값이 없다.
        _settings.AdMuteSavedVolume = current;
        TrySetVolume(0);
        return true;
    }

    /// <summary>광고 종료 — 저장해 둔 볼륨으로 되돌린다.</summary>
    /// <returns>실제로 볼륨을 되돌렸으면 true(로그 정확성을 위해 — <see cref="Mute"/> 참고).</returns>
    public bool Unmute()
    {
        if (!_muted)
        {
            // 뮤트한 적이 없어도 기록이 남아 있으면(이전 세션 잔재) 정리한다.
            if (_settings.AdMuteSavedVolume >= 0) { RestoreOrphanedVolume(); return true; }
            return false;
        }

        var saved = _settings.AdMuteSavedVolume;
        _muted = false;
        _settings.AdMuteSavedVolume = -1;

        // 사용자가 도중에 볼륨을 올렸으면 그 값을 존중하고 덮어쓰지 않는다.
        // _userOverrode는 여기서 내리지 않는다 — 음악이 돌아올 때까지(ResetUserOverride) 유지해야
        // 다음 광고 세그먼트에서 다시 내리누르지 않는다.
        if (_userOverrode) return false;

        if (saved < 0) return false;
        TrySetVolume(saved);
        return true;
    }

    /// <summary>
    /// 실제 음악이 돌아왔을 때 호출한다 — 사용자 개입 기억을 여기서 지운다.
    /// 광고 세그먼트 사이의 짧은 공백에서는 호출되지 않아야 한다(그러면 포기가 풀려 버린다).
    /// </summary>
    public void ResetUserOverride() => _userOverrode = false;

    /// <summary>
    /// 뮤트 중 주기적으로 호출한다(광고 진행 중 1초 틱). 볼륨이 0이 아니게 됐으면
    /// <b>이번 광고 구간을 포기한다</b> — 다시 내리누르지 않는다.
    ///
    /// 이름 그대로 "포기 여부 확인"이지 재적용이 아니다. 볼륨이 되살아나는 경로는 둘
    /// (사용자 볼륨 키 / 출력 장치 전환)인데 볼륨 값만으로는 구분할 수 없어, 사용자 쪽으로
    /// 해석해 손을 뗀다. 블루투스 연결 등으로 되살아난 경우엔 남은 광고가 그대로 들리지만,
    /// 사용자가 올린 볼륨을 계속 0으로 되돌리는 것보다 낫다.
    /// (구분하려면 <c>AudioDeviceCallback</c>으로 장치 변경 시점을 받아 그 직후의 볼륨 변화만
    /// 장치 탓으로 돌려야 한다 — 후속 과제.)
    /// </summary>
    public void CheckUserOverride()
    {
        if (_audio is null || !_muted || _userOverrode) return;

        int current;
        try { current = _audio.GetStreamVolume(MediaStream); }
        catch (Exception) { return; }

        if (current <= 0) return;

        _userOverrode = true;
        // 뮤트를 놓았으므로 _muted도 내린다. 이걸 안 내리면 (a) 다음 광고 세그먼트에서
        // Mute()가 "이미 뮤트 중"으로 보고 빠져나가 재뮤트가 안 되고, (b) 다음 틱의
        // CheckUserOverride()가 볼륨이 0이 아니라며 또 포기한다.
        // 일시정지 보류 때문에 세그먼트 사이에 Unmute()가 호출되지 않아 스스로 풀리지 않는다.
        _muted = false;
        _settings.AdMuteSavedVolume = -1;
        global::Android.Util.Log.Info("Musebase",
            $"ad-mute: 볼륨이 {current}로 바뀌어 이번 광고 구간은 포기 (음악이 돌아올 때까지 유지)");
    }

    private void TrySetVolume(int volume)
    {
        if (_audio is null) return;
        try
        {
            // ShowUi 없이 조용히 — 광고마다 볼륨 팝업이 뜨면 안 된다.
            _audio.SetStreamVolume(MediaStream, volume, VolumeNotificationFlags.RemoveSoundAndVibrate);
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn("Musebase", $"ad-mute: 볼륨 설정 실패({volume}) — {e.Message}");
        }
    }
}
