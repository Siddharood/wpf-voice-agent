using System;
using NAudio.Wave;

namespace WpfVoiceAgent.Audio
{
    public interface IAudioCapture : IDisposable
    {
        WaveFormat WaveFormat { get; }
        event EventHandler<WaveInEventArgs> DataAvailable;
        void Start();
        void Stop();
    }
}
