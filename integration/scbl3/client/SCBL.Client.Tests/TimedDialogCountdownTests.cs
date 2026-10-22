using SplinterCellCNLauncher;

namespace SCBL.Client.Tests;

public sealed class TimedDialogCountdownTests
{
    [Theory]
    [InlineData("继续", 5, "继续 (5)")]
    [InlineData("继续", 1, "继续 (1)")]
    [InlineData("继续覆盖", 5, "继续覆盖 (5)")]
    [InlineData("Overwrite", 1, "Overwrite (1)")]
    public void ConfirmationRemainsDisabledWhileCountdownIsRunning(
        string label,
        int remainingSeconds,
        string expectedText)
    {
        (string text, bool isEnabled) = MainWindow.BuildTimedConfirmButtonState(label, remainingSeconds);

        Assert.Equal(expectedText, text);
        Assert.False(isEnabled);
    }

    [Theory]
    [InlineData("继续")]
    [InlineData("继续覆盖")]
    [InlineData("Continue")]
    [InlineData("Overwrite")]
    public void ConfirmationBecomesEnabledOnlyWhenCountdownReachesZero(string label)
    {
        (string text, bool isEnabled) = MainWindow.BuildTimedConfirmButtonState(label, 0);

        Assert.Equal(label, text);
        Assert.True(isEnabled);
    }
}
