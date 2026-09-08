using System.Threading;
using System.Threading.Tasks;

namespace WpfVoiceAgent.Audio
{
    public interface ISpeakerEmbeddingProvider
    {
        Task<float[]> CreateEmbeddingAsync(
            byte[] pcm16Mono,
            int sampleRate,
            CancellationToken cancellationToken);
    }
}