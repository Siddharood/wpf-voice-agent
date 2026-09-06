using System;
using System.Threading;
using System.Threading.Tasks;

namespace WpfVoiceAgent.Audio
{
    public interface IAudioPlayback : IDisposable
    {
        bool IsPlaying { get; }
        event EventHandler PlaybackStarted;
        event EventHandler PlaybackStopped;
        Task PlayWavAsync(byte[] wavBytes, CancellationToken cancellationToken);
    }
}
