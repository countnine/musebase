using Android.OS;
using Musebase.Engine;

namespace Musebase.Android.Services;

/// <summary>
/// <see cref="IEngineDispatcher"/>의 Android 구현 — 메인 Looper의 <see cref="Handler"/> 기반.
/// WpfEngineDispatcher(Dispatcher/DispatcherTimer)와 대칭: 콜백·타이머 틱을 모두
/// 메인(UI) 스레드에서 실행해 코디네이터 상태 갱신과 뷰 갱신을 한 스레드로 정렬한다.
/// </summary>
public sealed class AndroidEngineDispatcher : IEngineDispatcher
{
    private readonly Handler _handler = new(Looper.MainLooper!);

    public void Post(Action action) => _handler.Post(action);

    public IEngineTimer CreateTimer(TimeSpan interval, Action tick) =>
        new HandlerEngineTimer(_handler, interval, tick);

    /// <summary>
    /// PostDelayed 자기 재예약 방식의 주기 타이머. Handler는 공유되므로
    /// RemoveCallbacksAndMessages(null) 대신 자기 Runnable만 제거한다(다른 타이머/Post 보호).
    /// </summary>
    private sealed class HandlerEngineTimer : IEngineTimer
    {
        private readonly Handler _handler;
        private readonly long _intervalMs;
        private readonly Action _tick;
        private readonly Java.Lang.Runnable _runnable; // RemoveCallbacks 동일성 유지를 위해 1개만 생성
        private bool _running;

        public HandlerEngineTimer(Handler handler, TimeSpan interval, Action tick)
        {
            _handler = handler;
            _intervalMs = Math.Max(1, (long)interval.TotalMilliseconds);
            _tick = tick;
            _runnable = new Java.Lang.Runnable(OnTick);
        }

        private void OnTick()
        {
            if (!_running) return;
            _tick();
            if (_running) _handler.PostDelayed(_runnable, _intervalMs);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _handler.PostDelayed(_runnable, _intervalMs);
        }

        public void Stop()
        {
            _running = false;
            _handler.RemoveCallbacks(_runnable);
        }
    }
}
