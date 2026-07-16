using LyricsX.Engine;
using Xunit;

namespace LyricsX.Core.Tests;

/// <summary>소스 → 코디네이터 → 표시 상태 → VM(INotifyPropertyChanged) 종단 배선.</summary>
public class PlaybackViewModelTests
{
    private sealed class FakeSource : INowPlayingSource
    {
        public TrackInfo? CurrentTrack { get; set; }
        public bool IsPlaying { get; set; }
        public event Action<TrackInfo?>? TrackChanged;
        public event Action<bool>? IsPlayingChanged;
        public TimeSpan? GetEstimatedPosition() => TimeSpan.Zero;
        public PlaybackControls GetControls() => new(true, true, true);
        public Task<bool> TogglePlayPauseAsync() => Task.FromResult(true);
        public Task<bool> SkipNextAsync() => Task.FromResult(true);
        public Task<bool> SkipPreviousAsync() => Task.FromResult(true);

        public void RaisePlaying(bool playing) { IsPlaying = playing; IsPlayingChanged?.Invoke(playing); }
    }

    // Post/타이머를 즉시(인라인) 실행 — 테스트 동기화용
    private sealed class InlineDispatcher : IEngineDispatcher
    {
        public void Post(Action action) => action();
        public IEngineTimer CreateTimer(TimeSpan interval, Action tick) => new NoopTimer();
        private sealed class NoopTimer : IEngineTimer { public void Start() { } public void Stop() { } }
    }

    [Fact]
    public void ViewModel_ReflectsState_AndRaisesPropertyChanged_OnPlayingChange()
    {
        var source = new FakeSource { CurrentTrack = new TrackInfo("Title", "Artist", "", null, "app"), IsPlaying = false };
        using var coordinator = new LyricsCoordinator(source, new InlineDispatcher());
        using var vm = new PlaybackViewModel(coordinator);

        var raised = 0;
        vm.PropertyChanged += (_, _) => raised++;

        source.RaisePlaying(true);

        Assert.True(vm.State.IsPlaying);
        Assert.Equal("Title", vm.State.TrackTitle);
        Assert.True(vm.State.CanNext); // FakeSource.GetControls()
        Assert.True(raised > 0);
    }
}
