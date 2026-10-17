using Moq;
using Murmur.Core.Interfaces;
using Murmur.Core.Models;
using Murmur.Core.Services;
using Xunit;

namespace Murmur.Tests;

public class SoundEffectServiceTests
{
    [Fact]
    public void Constructor_NullSettingsManager_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SoundEffectService(null!));
    }

    [Fact]
    public void PlayRecordingStart_WhenSoundsDisabled_DoesNotThrow()
    {
        var mockSettings = new Mock<ISettingsManager>();
        mockSettings.Setup(s => s.Load()).Returns(new AppSettings { PlayAlertSounds = false });

        var service = new SoundEffectService(mockSettings.Object);
        var exception = Record.Exception(() => service.PlayRecordingStart());

        Assert.Null(exception);
    }

    [Fact]
    public void PlayDictationSuccess_WhenSoundsEnabled_ExecutesSafely()
    {
        var mockSettings = new Mock<ISettingsManager>();
        mockSettings.Setup(s => s.Load()).Returns(new AppSettings { PlayAlertSounds = true, SoundVolume = 50 });

        var service = new SoundEffectService(mockSettings.Object);
        var exception = Record.Exception(() => service.PlayDictationSuccess());

        Assert.Null(exception);
    }

    [Fact]
    public void PlayError_WhenSoundsEnabled_ExecutesSafely()
    {
        var mockSettings = new Mock<ISettingsManager>();
        mockSettings.Setup(s => s.Load()).Returns(new AppSettings { PlayAlertSounds = true, SoundVolume = 80 });

        var service = new SoundEffectService(mockSettings.Object);
        var exception = Record.Exception(() => service.PlayError());

        Assert.Null(exception);
    }
}
