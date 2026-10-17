using Microsoft.Extensions.DependencyInjection;
using Murmur.Core.Interfaces;
using Murmur.Core.Services;
using Murmur.Core.Models;
using Moq;
using Xunit;

namespace Murmur.Tests.Integration;

/// <summary>
/// Integration tests for DI container and component wiring.
/// Tests that all services resolve correctly and interact as expected.
/// Requirements covered: 1.1, 3.3, 7.2
/// </summary>
public class ComponentWiringTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempSettingsPath;

    public ComponentWiringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MurmurWiringTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tempSettingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    #region DI Container Tests

    [Fact]
    public void ServiceCollection_ResolvesSettingsManager()
    {
        // Arrange
        var services = CreateServiceCollection();
        var provider = services.BuildServiceProvider();

        // Act
        var settingsManager = provider.GetService<ISettingsManager>();

        // Assert
        Assert.NotNull(settingsManager);
        Assert.IsType<SettingsManager>(settingsManager);
    }

    [Fact]
    public void ServiceCollection_ResolvesCredentialStore()
    {
        // Arrange
        var services = CreateServiceCollection();
        var provider = services.BuildServiceProvider();

        // Act
        var credentialStore = provider.GetService<ICredentialStore>();

        // Assert
        Assert.NotNull(credentialStore);
        Assert.IsType<CredentialStore>(credentialStore);
    }

    [Fact]
    public void ServiceCollection_ResolvesStartupManager()
    {
        // Arrange
        var services = CreateServiceCollection();
        var provider = services.BuildServiceProvider();

        // Act
        var startupManager = provider.GetService<IStartupManager>();

        // Assert
        Assert.NotNull(startupManager);
        Assert.IsType<StartupManager>(startupManager);
    }

    [Fact]
    public void ServiceCollection_ResolvesPipelineOrchestrator()
    {
        // Arrange
        var services = CreateServiceCollectionWithMocks();
        var provider = services.BuildServiceProvider();

        // Act
        var orchestrator = provider.GetService<IPipelineOrchestrator>();

        // Assert
        Assert.NotNull(orchestrator);
    }

    [Fact]
    public void ServiceCollection_SingletonsAreSameInstance()
    {
        // Arrange
        var services = CreateServiceCollection();
        var provider = services.BuildServiceProvider();

        // Act
        var settings1 = provider.GetService<ISettingsManager>();
        var settings2 = provider.GetService<ISettingsManager>();

        // Assert
        Assert.Same(settings1, settings2);
    }

    #endregion

    #region Settings Propagation Tests

    [Fact]
    public void SettingsChanges_PropagateToServices()
    {
        // Arrange
        var services = CreateServiceCollectionWithMocks();
        var provider = services.BuildServiceProvider();
        
        var settingsManager = provider.GetRequiredService<ISettingsManager>();
        var settings = settingsManager.Load();

        // Act - Modify settings
        settings.ApiBaseUrl = "https://custom-api.example.com";
        settingsManager.Save(settings);

        // Assert - Reload and verify
        var reloadedSettings = settingsManager.Load();
        Assert.Equal("https://custom-api.example.com", reloadedSettings.ApiBaseUrl);
    }

    [Fact]
    public void SettingsLoad_ReturnsDefaultsWhenNoFileExists()
    {
        // Arrange
        var services = CreateServiceCollection();
        var provider = services.BuildServiceProvider();
        var settingsManager = provider.GetRequiredService<ISettingsManager>();

        // Clear any existing settings by resetting
        settingsManager.Reset();

        // Act
        var settings = settingsManager.Load();

        // Assert
        Assert.NotNull(settings);
        Assert.NotNull(settings.HoldHotkey);
        Assert.NotNull(settings.ToggleHotkey);
    }

    #endregion

    #region Pipeline Integration Tests

    [Fact]
    public async Task Pipeline_StateChangesPropagate()
    {
        // Arrange
        var mockAudioRecorder = new Mock<IAudioRecorder>();
        var mockTranscription = new Mock<ITranscriptionService>();
        var mockPostProcessing = new Mock<IPostProcessingService>();
        var mockClipboard = new Mock<IClipboardManager>();
        var mockSettings = new Mock<ISettingsManager>();

        mockSettings.Setup(s => s.Load()).Returns(new AppSettings());
        mockAudioRecorder.Setup(a => a.StartRecordingAsync(It.IsAny<string?>())).Returns(Task.CompletedTask);

        var orchestrator = new PipelineOrchestrator(
            mockAudioRecorder.Object,
            mockTranscription.Object,
            mockPostProcessing.Object,
            mockClipboard.Object,
            mockSettings.Object);

        var stateChanges = new List<PipelineState>();
        orchestrator.StateChanged += (s, e) => stateChanges.Add(e.NewState);

        // Act
        await orchestrator.StartAsync(RecordingMode.Hold);

        // Assert
        Assert.Contains(PipelineState.Initializing, stateChanges);
        Assert.Contains(PipelineState.Recording, stateChanges);
    }

    #endregion

    #region Hotkey to Pipeline Integration Tests

    [Fact]
    public void HotkeyBinding_HashCodeEquality()
    {
        // Arrange
        var hotkey1 = new HotkeyBinding 
        { 
            Key = VirtualKey.F2, 
            Modifiers = ModifierKeys.Ctrl | ModifierKeys.Shift 
        };
        var hotkey2 = new HotkeyBinding 
        { 
            Key = VirtualKey.F2, 
            Modifiers = ModifierKeys.Ctrl | ModifierKeys.Shift 
        };

        // Assert
        Assert.Equal(hotkey1.GetHashCode(), hotkey2.GetHashCode());
        Assert.True(hotkey1.Equals(hotkey2));
    }

    [Fact]
    public void HotkeyBinding_DifferentKeysNotEqual()
    {
        // Arrange
        var hotkey1 = new HotkeyBinding { Key = VirtualKey.F2, Modifiers = ModifierKeys.None };
        var hotkey2 = new HotkeyBinding { Key = VirtualKey.F3, Modifiers = ModifierKeys.None };

        // Assert
        Assert.False(hotkey1.Equals(hotkey2));
    }

    #endregion

    #region Helper Methods

    private ServiceCollection CreateServiceCollection()
    {
        var services = new ServiceCollection();
        
        services.AddSingleton<ISettingsManager>(new SettingsManager(_tempSettingsPath, enforcePermissions: false));
        services.AddSingleton<ICredentialStore, CredentialStore>();
        services.AddSingleton<IStartupManager, StartupManager>();
        
        return services;
    }

    private ServiceCollection CreateServiceCollectionWithMocks()
    {
        var services = new ServiceCollection();

        // Real services for testing with isolated settings path
        services.AddSingleton<ISettingsManager>(new SettingsManager(_tempSettingsPath, enforcePermissions: false));
        services.AddSingleton<ICredentialStore, CredentialStore>();
        services.AddSingleton<IStartupManager, StartupManager>();

        // Mocks for services that require external resources
        var mockAudioRecorder = new Mock<IAudioRecorder>();
        var mockTranscription = new Mock<ITranscriptionService>();
        var mockPostProcessing = new Mock<IPostProcessingService>();
        var mockClipboard = new Mock<IClipboardManager>();
        var mockHotkey = new Mock<IHotkeyManager>();
        var mockTray = new Mock<ISystemTrayManager>();
        var mockToast = new Mock<IToastNotificationService>();

        services.AddSingleton(mockAudioRecorder.Object);
        services.AddSingleton(mockTranscription.Object);
        services.AddSingleton(mockPostProcessing.Object);
        services.AddSingleton(mockClipboard.Object);
        services.AddSingleton(mockHotkey.Object);
        services.AddSingleton(mockTray.Object);
        services.AddSingleton(mockToast.Object);

        // Pipeline orchestrator with mocks
        services.AddSingleton<IPipelineOrchestrator>(sp => new PipelineOrchestrator(
            sp.GetRequiredService<IAudioRecorder>(),
            sp.GetRequiredService<ITranscriptionService>(),
            sp.GetRequiredService<IPostProcessingService>(),
            sp.GetRequiredService<IClipboardManager>(),
            sp.GetRequiredService<ISettingsManager>()));

        return services;
    }

    #endregion
}
