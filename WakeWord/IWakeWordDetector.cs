using System;
using System.Threading;
using System.Threading.Tasks;

namespace WpfVoiceAgent.WakeWord
{
    public interface IWakeWordDetector : IDisposable
    {
        Task<bool> DetectAsync(
            byte[] pcm,
            CancellationToken cancellationToken);

        void SetEnabled(bool enabled);
    }
}