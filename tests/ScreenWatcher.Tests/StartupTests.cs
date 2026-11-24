using ScreenWatcher.Config;
using ScreenWatcher.Diagnostics;
using ScreenWatcher.UI;

namespace ScreenWatcher.Tests;

/// <summary>1.3.0 never opened its window: building the kill row (2 sub-lines) threw in AlertRow's
/// layout. These build every main-window row and load old / broken settings files.</summary>
public class StartupTests
{
    static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("failed on the STA thread", error);
    }

    [Fact]
    public void EveryCatalogRowBuildsAndLaysOut() => OnSta(() =>
    {
        foreach (var spec in AlertRowCatalog.All)
        {
            using var row = new AlertRow(spec);
            row.SetBounds(0, 0, 440, spec.Height);
            row.PerformLayout();
        }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RowWithSubLinesBuilds(int lines) => OnSta(() =>
    {
        var subs = Enumerable.Range(0, lines).Select(i => new SubLine("sub" + i, "line " + i)).ToArray();
        var spec = new AlertRowSpec("x", "row", Implemented: true, Subs: subs);
        using var row = new AlertRow(spec);
        row.SetBounds(0, 0, 440, spec.Height);
        row.PerformLayout();
        row.SetSubAlertEnabled("sub" + (lines - 1), false);
        Assert.False(row.GetSubAlertEnabled("sub" + (lines - 1)));
        row.SetSubNow("sub0", "มีบัพ");
    });

    [Fact]
    public void KillRowHasTwoSubLines() =>
        Assert.Equal(2, AlertRowCatalog.Get(AlertIds.Kill300).Subs!.Length);

    static AppSettings LoadText(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), "sw-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        try { return AppSettings.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OwnersSettingsFile() => Assert.Equal(CollectMode.Off, LoadText("﻿{ \"CollectData\": false }").Collect);

    [Fact]
    public void Settings122EraLoads()
    {
        var s = LoadText("""
            { "CollectData": false, "CollectMode": "auto", "SaveAlertEvidence": true,
              "DiscordGuildId": 1, "DiscordChannelId": 2, "DiscordChannelLabel": "S › #c",
              "AlertsEnabled": { "death": true, "kill300": false, "expCard": false },
              "Thresholds": { "ammoBox": 5, "warPillar": 10 }, "WatchRight": { "hpPotions": true } }
            """);
        Assert.Equal(CollectMode.Auto, s.Collect);
        Assert.False(s.IsAlertEnabled("KILL300"));
        Assert.True(s.IsAlertEnabled(AlertIds.Handshake));   // new in 1.3: missing = on
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"CollectData\": \"yes\" }")]
    [InlineData("{ \"Thresholds\": { \"ammoBox\": \"five\" } }")]
    [InlineData("{ \"Thresholds\": null, \"AlertsEnabled\": null }")]
    [InlineData("{ \"DiscordGuildId\": -5 }")]
    [InlineData("{ \"CollectMode\": \"bogus\" }")]
    public void BadSettingsFallBackToDefaults(string json)
    {
        var s = LoadText(json);
        Assert.Equal(CollectMode.Off, s.Collect);
        Assert.True(s.IsAlertEnabled(AlertIds.Death));
    }

    [Fact]
    public void KeysDifferingOnlyInCaseDoNotThrow()
    {
        var s = LoadText("{ \"AlertsEnabled\": { \"death\": true, \"Death\": false } }");
        Assert.False(s.IsAlertEnabled(AlertIds.Death));
    }

    [Fact]
    public void OutOfRangeThresholdUsesDefault()
    {
        var s = LoadText("{ \"Thresholds\": { \"warPillar\": 999 } }");
        var spec = AlertRowCatalog.Get(AlertIds.WarPillar);
        Assert.Equal(spec.DefaultThreshold, AlertRowCatalog.Threshold(s, spec));
    }

    [Fact]
    public void CrashIsJournalled()
    {
        string root = Path.Combine(Path.GetTempPath(), "sw-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            CrashReport.Record(root, Path.Combine(root, "log.txt"), "startup", new InvalidOperationException("boom"));
            string text = File.ReadAllText(Directory.GetFiles(root, "*.jsonl").Single());
            Assert.Contains("\"type\":\"app_crash\"", text);
            Assert.Contains("\"stage\":\"startup\"", text);
            Assert.Contains("boom", text);
            Assert.Contains("เปิดไม่ได้", CrashReport.ThaiMessage("startup", new Exception("x")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
