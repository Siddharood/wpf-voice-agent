namespace WpfVoiceAgent.Audio
{
    public interface IAecProcessor
    {
        byte[] ProcessCapture(byte[] microphonePcm);

        void PushPlaybackReference(byte[] pcm);

        void Reset();
    }
}