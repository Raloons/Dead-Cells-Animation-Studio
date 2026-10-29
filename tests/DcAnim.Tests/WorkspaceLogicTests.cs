using DcAnim.Core.Catalog;
using DcAnim.Core.Playback;
using DcAnim.Core.Services;

namespace DcAnim.Tests;

public sealed class WorkspaceLogicTests
{
    [Theory]
    [InlineData("AtkKatanaB2", AnimationCategory.Attacks)]
    [InlineData("atkTombstoneB5", AnimationCategory.Attacks)]
    [InlineData("combAtkA", AnimationCategory.Attacks)]
    [InlineData("gunShootA", AnimationCategory.Attacks)]
    [InlineData("blowgunAtkShoot", AnimationCategory.Attacks)]
    [InlineData("HeavyBowShoot", AnimationCategory.Attacks)]
    [InlineData("rapierCombo", AnimationCategory.Attacks)]
    [InlineData("dualAxeSequence", AnimationCategory.Attacks)]
    [InlineData("lethalHit", AnimationCategory.Attacks)]
    [InlineData("castA", AnimationCategory.Attacks)]
    [InlineData("LoadAtkKatanaA", AnimationCategory.Attacks)]
    [InlineData("gunA", AnimationCategory.Attacks)]
    [InlineData("closeCombatBowCrouchShoot", AnimationCategory.Attacks)]
    public void Categorize_Attacks(string id, AnimationCategory expected) =>
        Assert.Equal(expected, AnimationCategorizer.Categorize(id));

    [Theory]
    [InlineData("idle", AnimationCategory.Idle)]
    [InlineData("idleKnee", AnimationCategory.Idle)]
    [InlineData("idleGunA", AnimationCategory.Idle)] // stance wins over weapon keyword
    [InlineData("climbIdleA", AnimationCategory.Idle)]
    [InlineData("travoltaIdle", AnimationCategory.Idle)]
    [InlineData("wineThrowIdle", AnimationCategory.Idle)]
    [InlineData("atkBarrelLauncherIdle", AnimationCategory.Idle)]
    [InlineData("idleAtkBarrelLauncher", AnimationCategory.Idle)]
    public void Categorize_IdleTakesPrecedence(string id, AnimationCategory expected) =>
        Assert.Equal(expected, AnimationCategorizer.Categorize(id));

    [Theory]
    [InlineData("run", AnimationCategory.Movement)]
    [InlineData("dash", AnimationCategory.Movement)]
    [InlineData("wallRun", AnimationCategory.Movement)]
    [InlineData("wallClimbA", AnimationCategory.Movement)]
    [InlineData("teleport", AnimationCategory.Movement)]
    [InlineData("crouch", AnimationCategory.Movement)]
    [InlineData("jumpUp", AnimationCategory.Movement)]
    public void Categorize_Movement(string id, AnimationCategory expected) =>
        Assert.Equal(expected, AnimationCategorizer.Categorize(id));

    [Theory]
    [InlineData("blockRampart", AnimationCategory.Other)] // defensive, not an attack
    [InlineData("dead", AnimationCategory.Other)]
    [InlineData("openChest", AnimationCategory.Other)]
    [InlineData("think", AnimationCategory.Other)]
    [InlineData("richterMode_tackle_loop", AnimationCategory.Other)]
    [InlineData("", AnimationCategory.Other)]
    public void Categorize_AmbiguousIsOther(string id, AnimationCategory expected) =>
        Assert.Equal(expected, AnimationCategorizer.Categorize(id));

    [Fact]
    public void Categorize_FullNativeConventions_NoCrashesOrEmpty()
    {
        // Spot-check a realistic sweep: every id gets exactly one category.
        foreach (var id in new[] { "AtkKatanaA", "run", "idle", "block", "sit", "fly", "stun", "volte", "no", "me" })
            Assert.True(Enum.IsDefined(AnimationCategorizer.Categorize(id)));
    }

    [Theory]
    [InlineData(0.0, 11, 16.0, 0)]
    [InlineData(15.9, 11, 16.0, 0)]
    [InlineData(16.0, 11, 16.0, 1)]
    [InlineData(9999.0, 11, 16.0, 10)]
    [InlineData(-5.0, 11, 16.0, 0)]
    public void TimelineMath_FrameAtX(double x, int frames, double ppf, int expected) =>
        Assert.Equal(expected, TimelineMath.FrameAtX(x, frames, ppf));

    [Fact]
    public void TimelineMath_FractionAtX_Clamps()
    {
        Assert.Equal(0.0, TimelineMath.FractionAtX(-10, 10, 16.0));
        Assert.Equal(1.0, TimelineMath.FractionAtX(1e6, 10, 16.0));
        Assert.Equal(0.5, TimelineMath.FractionAtX(80, 10, 16.0), precision: 9);
        Assert.Equal(0.0, TimelineMath.FractionAtX(10, 0, 16.0));
    }

    [Fact]
    public void TimelineMath_LabelEvery_ScalesWithZoom()
    {
        Assert.Equal(1, TimelineMath.LabelEvery(11, 64.0));
        Assert.True(TimelineMath.LabelEvery(100, 8.0) > 1);
    }

    [Fact]
    public void TimelineMath_GridLines_AlignWithFrameMapping()
    {
        // 11 frames at 32 px: minors on every boundary from origin, majors on
        // labeled intervals, covering the width including unused space.
        var (minor, major) = TimelineMath.GridLines(11, 32.0, 400.0, originX: 4);
        Assert.Equal(4, minor[0]);
        for (int i = 1; i < minor.Length; i++)
            Assert.Equal(32.0, minor[i] - minor[i - 1], precision: 9);
        // Majors are a subset of minors, starting at frame 0.
        Assert.Equal(minor[0], major[0]);
        foreach (var m in major)
            Assert.Contains(m, minor);
        // Every minor maps back to its frame index.
        for (int i = 0; i < 11; i++)
            Assert.Equal(i, TimelineMath.FrameAtX(4 + i * 32.0 + 1, 11, 32.0));
    }

    [Fact]
    public void TimelineMath_GridLines_EmptyForDegenerateInput()
    {
        Assert.Empty(TimelineMath.GridLines(0, 32.0, 400.0).Minor);
        Assert.Empty(TimelineMath.GridLines(11, 0.0, 400.0).Minor);
        Assert.Empty(TimelineMath.GridLines(11, 32.0, 0.0).Minor);
    }

    [Fact]
    public void TimelineMath_FormatTime()
    {
        Assert.Equal("0:00.000", TimelineMath.FormatTime(0, 30));
        Assert.Equal("0:00.500", TimelineMath.FormatTime(15, 30));
        Assert.Equal("0:01.000", TimelineMath.FormatTime(30, 30));
    }

    [Fact]
    public void WorkspaceSettings_RoundTrip_PreservesAndClamps()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new WorkspaceSettings
            {
                LeftPanelWidth = 340,
                RightPanelWidth = 9999, // clamped on save
                TimelineHeight = 260,
                WindowWidth = 1920,
                WindowHeight = 1080,
            };
            settings.Save(path);
            var loaded = WorkspaceSettings.Load(path);
            Assert.Equal(340, loaded.ClampedLeftWidth);
            Assert.Equal(560, loaded.ClampedRightWidth);
            Assert.Equal(260, loaded.ClampedTimelineHeight);
            Assert.Equal(1920, loaded.WindowWidth);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void WorkspaceSettings_MissingOrCorrupt_ReturnsDefaults()
    {
        Assert.Equal(300, WorkspaceSettings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")).ClampedLeftWidth);
        string bad = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}.json");
        File.WriteAllText(bad, "{ not json");
        try
        {
            Assert.Equal(300, WorkspaceSettings.Load(bad).ClampedLeftWidth);
        }
        finally
        {
            File.Delete(bad);
        }
    }
}
