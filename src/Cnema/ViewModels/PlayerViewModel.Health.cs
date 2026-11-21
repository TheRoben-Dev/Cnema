using System.Diagnostics;
using System.Runtime;
using Avalonia.Threading;
using Cnema.Services;

namespace Cnema.ViewModels;

/// <summary>
/// Keeps playback smooth while a video plays, and writes down what happened when it wasn't.
/// - App Nap stays off (macOS throttles a napping app's threads, libVLC's decoders with them).
/// - The garbage collector avoids long full collections (they pause the threads libVLC calls back into
///   for every video frame, and a big library makes them longer the longer Savefile runs).
/// - Every 10 s it checks libVLC's own counts of lost audio buffers and pictures, and when sound was lost
///   it logs those with what else was going on (window thread delays, garbage collections, CPU), so a
///   diagnostics report shows the cause.
/// </summary>
public partial class PlayerViewModel
{
    private IDisposable? _playAwake;
    private GCLatencyMode? _gcBefore;
    private DispatcherTimer? _healthTimer, _lagTimer;
    private long _lostAudio, _lostPictures, _playedAudio;
    private int _gen2;
    private TimeSpan _gcPause, _cpu;
    private readonly Stopwatch _healthClock = new();
    private readonly Stopwatch _lagClock = new();
    private double _maxLagMs;
    private int _healthQuiet;

    partial void OnIsPlayingChanged(bool value)
    {
        if (value) StartHealth();
        else StopHealth();
    }

    private void StartHealth()
    {
        _playAwake ??= PlatformShell.StayAwake("Playing a video");
        try
        {
            if (_gcBefore is null)
            {
                _gcBefore = GCSettings.LatencyMode;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            }
        }
        catch { /* not supported here */ }

        ResetHealthBaseline();
        _healthTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => CheckHealth());
        // A tick every 50 ms that measures how late it runs: how long the window's thread was busy.
        _lagTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, (_, _) =>
        {
            var late = _lagClock.Elapsed.TotalMilliseconds - 50;
            if (late > _maxLagMs) _maxLagMs = late;
            _lagClock.Restart();
        });
        _lagClock.Restart();
        _healthTimer.Start();
        _lagTimer.Start();
    }

    private void StopHealth()
    {
        _healthTimer?.Stop();
        _lagTimer?.Stop();
        try { if (_gcBefore is { } mode) GCSettings.LatencyMode = mode; } catch { /* as it was */ }
        _gcBefore = null;
        _playAwake?.Dispose();
        _playAwake = null;
    }

    private void ResetHealthBaseline()
    {
        var s = Stats();
        _lostAudio = s.LostAudio;
        _lostPictures = s.LostPictures;
        _playedAudio = s.PlayedAudio;
        _gen2 = GC.CollectionCount(2);
        _gcPause = GC.GetTotalPauseDuration();
        try { _cpu = Process.GetCurrentProcess().TotalProcessorTime; } catch { _cpu = TimeSpan.Zero; }
        _healthClock.Restart();
        _maxLagMs = 0;
    }

    private (long LostAudio, long LostPictures, long PlayedAudio) Stats()
    {
        try
        {
            using var media = _player?.Media;
            if (media is null) return (0, 0, 0);
            var st = media.Statistics;
            return (st.LostAudioBuffers, st.LostPictures, st.PlayedAudioBuffers);
        }
        catch { return (0, 0, 0); }
    }

    private void CheckHealth()
    {
        if (!IsPlaying) return;
        var s = Stats();
        var lostAudio = s.LostAudio - _lostAudio;
        var lostPictures = s.LostPictures - _lostPictures;
        var played = s.PlayedAudio - _playedAudio;
        // A new video starts its counts at zero again.
        if (lostAudio < 0 || lostPictures < 0 || played < 0) { ResetHealthBaseline(); return; }
        if (lostAudio > 0)
        {
            var seconds = Math.Max(0.001, _healthClock.Elapsed.TotalSeconds);
            var cpu = 0.0;
            try { cpu = (Process.GetCurrentProcess().TotalProcessorTime - _cpu).TotalSeconds / seconds * 100; } catch { }
            var pause = (GC.GetTotalPauseDuration() - _gcPause).TotalMilliseconds;
            var mem = GC.GetTotalMemory(false) / (1024 * 1024);
            // Not every 10 s for a whole film: the first few, then one a minute.
            if (_healthQuiet++ < 6 || _healthQuiet % 6 == 0)
                Log.Warn($"Playback: {lostAudio} of {played + lostAudio} sound buffers lost in {seconds:0} s " +
                         $"(pictures lost {lostPictures}), window thread up to {_maxLagMs:0} ms late, " +
                         $"full GCs {GC.CollectionCount(2) - _gen2}, GC pauses {pause:0} ms, heap {mem} MB, " +
                         $"Savefile CPU {cpu:0}%, background held {BackgroundHold.IsHeld}, " +
                         $"background processes {BackgroundHold.RunningCount}");
        }
        ResetHealthBaseline();
    }
}
