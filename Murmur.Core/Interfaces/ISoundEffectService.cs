namespace Murmur.Core.Interfaces;

/// <summary>
/// Provides tactile auditory feedback cues for dictation pipeline events.
/// </summary>
public interface ISoundEffectService
{
    /// <summary>
    /// Plays an ascending cue indicating recording has started.
    /// </summary>
    void PlayRecordingStart();

    /// <summary>
    /// Plays a crisp confirmation cue indicating dictation has completed and pasted.
    /// </summary>
    void PlayDictationSuccess();

    /// <summary>
    /// Plays a subtle low cue indicating cancellation or error.
    /// </summary>
    void PlayError();
}
