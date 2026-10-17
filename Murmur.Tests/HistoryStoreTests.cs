using Murmur.Core.Models;
using Murmur.Core.Services;
using Xunit;

namespace Murmur.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _tempFile;

    public HistoryStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"murmur_history_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempFile))
            {
                File.Delete(_tempFile);
            }
        }
        catch
        {
            // Best effort
        }
    }

    [Fact]
    public void Add_ItemCanBeRetrieved()
    {
        var store = new HistoryStore(_tempFile);
        var item = new DictationHistoryItem
        {
            CleanTranscript = "Hello world",
            RawTranscript = "hello world",
            AppName = "Code",
            WindowTitle = "main.cs",
            DurationSeconds = 2.5
        };

        store.Add(item);

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("Hello world", all[0].CleanTranscript);
        Assert.Equal("Code", all[0].AppName);
    }

    [Fact]
    public void GetAll_ReturnsNewestFirst()
    {
        var store = new HistoryStore(_tempFile);
        store.Add(new DictationHistoryItem { CleanTranscript = "First" });
        store.Add(new DictationHistoryItem { CleanTranscript = "Second" });

        var all = store.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("Second", all[0].CleanTranscript);
        Assert.Equal("First", all[1].CleanTranscript);
    }

    [Fact]
    public void Delete_RemovesItem()
    {
        var store = new HistoryStore(_tempFile);
        var item1 = new DictationHistoryItem { CleanTranscript = "To Keep" };
        var item2 = new DictationHistoryItem { CleanTranscript = "To Delete" };
        store.Add(item1);
        store.Add(item2);

        var deleted = store.Delete(item2.Id);

        Assert.True(deleted);
        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("To Keep", all[0].CleanTranscript);
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var store = new HistoryStore(_tempFile);
        store.Add(new DictationHistoryItem { CleanTranscript = "One" });
        store.Add(new DictationHistoryItem { CleanTranscript = "Two" });

        store.Clear();

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Search_FiltersCorrectly()
    {
        var store = new HistoryStore(_tempFile);
        store.Add(new DictationHistoryItem { CleanTranscript = "Fix the login bug", AppName = "Slack" });
        store.Add(new DictationHistoryItem { CleanTranscript = "Add tests for auth", AppName = "Code" });
        store.Add(new DictationHistoryItem { CleanTranscript = "Order lunch pizza", AppName = "Browser" });

        var bugResults = store.Search("bug");
        Assert.Single(bugResults);
        Assert.Equal("Slack", bugResults[0].AppName);

        var codeResults = store.Search("code");
        Assert.Single(codeResults);
        Assert.Equal("Add tests for auth", codeResults[0].CleanTranscript);

        var emptyResults = store.Search("");
        Assert.Equal(3, emptyResults.Count);
    }

    [Fact]
    public void HistorySurvivesStoreRecreation()
    {
        var store1 = new HistoryStore(_tempFile);
        store1.Add(new DictationHistoryItem { CleanTranscript = "Persistent dictation" });

        var store2 = new HistoryStore(_tempFile);
        var all = store2.GetAll();

        Assert.Single(all);
        Assert.Equal("Persistent dictation", all[0].CleanTranscript);
    }

    [Fact]
    public void HistoryChanged_EventFiresOnAddAndClear()
    {
        var store = new HistoryStore(_tempFile);
        int changeCount = 0;
        store.HistoryChanged += (s, e) => changeCount++;

        store.Add(new DictationHistoryItem { CleanTranscript = "Test" });
        store.Clear();

        Assert.Equal(2, changeCount);
    }
}
